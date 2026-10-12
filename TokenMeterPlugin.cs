using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace TokenMeter;

/// <summary>
/// 插件入口。一个包只允许一个 IIslandPlugin 实现，必须 public sealed + 公共无参构造。
///
/// 三条来自铁律/§15 的硬性实现：
///   1. 刷新用 SemaphoreSlim 排队串行化，绝不「忙就 return」丢请求；
///   2. 每次取数都重新读设置，不用初始化时缓存的字段；
///   3. 任何取数失败都兜住异常并保留上一次数据，不让异常冒到宿主。
/// </summary>
public sealed class TokenMeterPlugin : IslandPluginBase
{
    private TokenMeterView _view = null!;
    private TokenMeterSpotlightView? _spotlightView;
    private IslandLiveContent _content = null!;
    private UsageScanner _scanner = null!;

    private UsageLedger? _lastLedger;
    private Snapshot? _lastSnapshot;

    /// <summary>服务端账本：按天累加，同一轮里只往水位线后面追，不重复拉。</summary>
    private UsageLedger? _serverLedger;
    private DateTime? _coveredUntilUtc;
    private ProviderState _providerState = new();
    private string _cacheDir = string.Empty;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource _cts = new();
    private bool _stopped;
    private bool _busy;
    private int _secondsSinceRefresh;

    /// <summary>把最新结果回推给还开着的设置页。</summary>
    public event Action<Snapshot?>? SnapshotApplied;

    /// <summary>后台是否在拉取中，用来给设置页按钮做即时反馈。</summary>
    public event Action<bool>? BusyChanged;

    private string _lastProviderNote = string.Empty;

    protected override Task OnInitializeAsync()
    {
        Log.Info($"启动：{Manifest.Id} {Manifest.Version}（宿主 {HostVersion}），插件目录 {PluginDirectory}");

        _stopped = false;
        _cts = new CancellationTokenSource();
        _secondsSinceRefresh = 0;

        // 扫描缓存放在插件自己的目录里：宿主卸载就是删掉 plugins\<id>\，缓存跟着走，
        // 不会在 %LOCALAPPDATA% 留下残留文件（市场审核标准第一条）。
        // 只有插件目录真的不可写时才退回 %LOCALAPPDATA%，并在日志里明说。
        var cacheDir = ResolveCacheDir();
        _cacheDir = cacheDir;
        _scanner = new UsageScanner(cacheDir);
        _providerState = LoadProviderState();
        _coveredUntilUtc = _providerState.CoveredUntilUtc;
        // 云端账本要和水位线一起恢复，否则重启后水位线会遮住已经拉过的数据
        if (_providerState.HasPersistedLedger) _serverLedger = _providerState.LoadLedger();

        _view = new TokenMeterView(Manifest, Theme);
        _view.SetScanning(true);

        _content = CreateContent();

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            Manifest.Id, Manifest.Name, Manifest.IconGlyph ?? "\uE713", BuildSettingsPage, order: 100));

        Context.Register(Context.OnSettingsChanged("enabled", OnEnabledChanged));
        Context.Register(Context.OnSettingsChanged("priority", OnPriorityChanged));
        Context.Register(Context.OnSettingsChanged("pricing", OnPricingChanged));
        Context.Register(Context.OnSettingsChanged("roots", () => _ = RefreshAsync("数据目录变更")));
        Context.Register(Context.OnSettingsChanged("localScope", () => RecomputeFromLedger("模型归属变更")));
        Context.Register(Context.OnSettingsChanged("provider", OnProviderConfigChanged));
        Context.Register(Context.OnSettingsChanged("refreshSeconds", () => { _secondsSinceRefresh = 0; }));

        if (Settings.Get("enabled", true)) SetContent(_content);

        // 1 秒心跳：只做「相对时间刷新 + 到点触发扫描」，改扫描间隔不需要重建定时器
        Context.CreateTimer(TimeSpan.FromSeconds(1), repeat: true, OnTick);

        _ = RefreshAsync("首次加载");
        return Task.CompletedTask;
    }

    protected override Task OnShutdownAsync()
    {
        Log.Info("已停用");
        _stopped = true;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        SetContent(null);
        try { Context.Island.CloseSpotlight(); } catch (Exception ex) { Log.Warn($"收起聚光卡失败（已忽略）：{ex.Message}"); }
        _spotlightView = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// IslandLiveContent 的属性是 init-only（只能初始化时赋值），所以改优先级不能就地改，
    /// 只能整份重建 —— 视图实例仍然复用，岛上的动画和已有内容不受影响。
    /// </summary>
    private string ResolveCacheDir()
    {
        try
        {
            var local = Path.Combine(PluginDirectory, ".cache");
            Directory.CreateDirectory(local);
            var probe = Path.Combine(local, ".writable-probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            Log.Info($"扫描缓存目录（随插件卸载一起删除）：{local}");
            return local;
        }
        catch (Exception ex)
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "token-meter");
            Log.Warn($"插件目录不可写（{ex.Message}），缓存退回 {fallback}；卸载后请手动删除该目录");
            return fallback;
        }
    }

    private IslandLiveContent CreateContent() => new()
    {
        Priority = Settings.Get("priority", 10),
        OwnerLabel = Manifest.Name,
        OwnerGlyph = Manifest.IconGlyph,
        OwnerAccent = Windows.UI.Color.FromArgb(255, 0, 122, 255),
        MorphView = _view,
        CompactSize = new Windows.Foundation.Size(268, 44),
        ExpandedSize = new Windows.Foundation.Size(430, 166),
        OnTap = OpenSpotlight,          // 点击岛体 = 打开聚光卡（§16 规则 2）
    };

    private void OnTick()
    {
        _secondsSinceRefresh++;

        var interval = Math.Max(10, Settings.Get("refreshSeconds", 60));
        if (_secondsSinceRefresh >= interval && !_busy)
        {
            _secondsSinceRefresh = 0;
            _ = RefreshAsync("定时刷新");
        }
    }

    // ---------------- 取数 ----------------

    /// <summary>
    /// 刷新：同一时刻只跑一次，但后来的请求排队等待，绝不静默丢弃。
    /// </summary>
    private async Task RefreshAsync(string reason)
    {
        if (_stopped) return;

        await _gate.WaitAsync().ConfigureAwait(false);
        if (_stopped)
        {
            _gate.Release();
            return;
        }

        _busy = true;
        RaiseBusy(true);
        RunOnUI(() => _view.SetScanning(true));

        try
        {
            // 真正的取数入口重新读设置，不用初始化时缓存的字段（§15.1）
            var rootsText = Settings.Get("roots", string.Empty);
            var pricingJson = Settings.Get("pricing", string.Empty);
            var roots = UsageScanner.ResolveRoots(rootsText);
            var pricing = PricingConfig.Parse(pricingJson);
            var token = _cts.Token;

            Snapshot snap;
            var providerJson = Settings.Get("provider", string.Empty);
            try
            {
                snap = await Task.Run(async () =>
                {
                    token.ThrowIfCancellationRequested();

                    // 本地日志始终先扫：它既是默认数据源，也是接口挂掉时的兜底
                    var local = _scanner.Scan(roots, token);
                    if (!_stopped) _scanner.SaveCache();

                    var cfg = ProviderConfig.Parse(providerJson);
                    if (!cfg.Enabled)
                    {
                        // 用户主动关掉接口：不能拿旧的服务端数据去压制本地日志
                        _serverLedger = null;
                        _coveredUntilUtc = null;
                    }

                    // 先把本地结果立即上屏：接口慢、卡住或在翻页时，界面也不会空着等你。
                    // 这里必须走多来源合成：否则这一帧只有本地一块，
                    // 双柱图与云端 KPI 栏会在每次刷新中途闪没一下（已发生过）。
                    RunOnUI(() => Apply(ComposeSnapshot(local, pricing, cfg, "云端拉取中…")));

                    var note = string.Empty;
                    if (cfg.Enabled)
                    {
                        if (cfg.IsUsable(out var why))
                        {
                            try
                            {
                                await PullProviderAsync(cfg, token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (ProviderException ex)
                            {
                                note = "接口不可用，已降级本地日志：" + ex.Message;
                                RememberProviderFailure(note);
                            }
                            catch (Exception ex)
                            {
                                note = "接口异常，已降级本地日志：" + ex.Message;
                                RememberProviderFailure(note);
                            }
                        }
                        else
                        {
                            note = "接口未就绪（已降级本地日志）：" + why;
                            RememberProviderFailure(note);
                        }
                    }

                    var s = ComposeSnapshot(local, pricing, cfg, note);
                    lock (_ledgerLock) { _lastLedger = local; }
                    return s;
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 失败保留上一次数据继续显示，比清空界面友好（§15.2）
                Log.Error("扫描失败（保留上一次数据）", ex);
                var fallback = BuildFromLedgerLocked(pricing, ex.Message);
                RunOnUI(() => Apply(fallback));
                return;
            }

            Log.Info($"扫描完成（{reason}）：{snap.Files} 个文件，本轮新读 {Format.Bytes(snap.ScannedBytes)}，"
                   + $"账本 {snap.LedgerDays} 天，今日 {Format.Tokens(snap.Today.C.Total)} tok / {snap.Today.C.Requests} 次请求，"
                   + $"费用 {Format.Amount(snap.Today.Cost)}{snap.Currency}，未定价模型 {snap.UnpricedNames.Count} 个");

            RunOnUI(() => Apply(snap));
        }
        finally
        {
            _busy = false;
            RaiseBusy(false);
            _gate.Release();
        }
    }

    private void RaiseBusy(bool busy)
    {
        try
        {
            Context.RunOnUI(() =>
            {
                try { BusyChanged?.Invoke(busy); }
                catch (Exception ex) { Log.Warn($"按钮状态回推失败（已忽略）：{ex.Message}"); }
            });
        }
        catch (Exception)
        {
            // 插件正在停用时调度失败是正常的，忽略
        }
    }

    /// <summary>
    /// 把本地账本与云端账本算成【分列的两块】。两个运营商的用量不相交、币种也不同，
    /// 所以绝不合并相加；只有被云端声明的模型才从本地剔出去（避免同一模型算两遍）。
    /// </summary>
    private Snapshot ComposeSnapshot(UsageLedger local, PricingConfig pricing, ProviderConfig cfg, string note)
    {
        var blocks = new List<SourceBlock>();
        var splitNote = string.Empty;
        var splitWarning = string.Empty;
        var haveServer = cfg.Enabled && _serverLedger is not null && _serverLedger.Days.Count > 0;

        SourceBlock primary;
        if (haveServer)
        {
            var (localPart, serverPart, split) = LedgerSplitter.Split(
                local, _serverLedger!,
                ModelScope.Parse(Settings.Get("localScope", string.Empty)),
                ModelScope.Parse(cfg.ModelScope));

            // 云端用自己的币种；没拿到 quota 的模型仍回退到本地单价估算
            var serverPricing = PricingConfig.Parse(pricing.ToJson());
            serverPricing.Currency = string.IsNullOrWhiteSpace(cfg.Currency) ? "$" : cfg.Currency.Trim();

            var lb = SnapshotBuilder.BuildBlock(localPart, pricing, "本地日志");
            var sb = SnapshotBuilder.BuildBlock(serverPart, serverPricing, cfg.DisplayName);
            blocks.Add(lb);
            blocks.Add(sb);
            splitNote = split.Note;
            splitWarning = split.DroppedNote;
            primary = lb.HasData ? lb : sb;
        }
        else
        {
            primary = SnapshotBuilder.BuildBlock(local, pricing, "本地日志");
            blocks.Add(primary);
        }

        var snap = new Snapshot
        {
            Currency = primary.Currency,
            RootDescription = local.RootDescription,
            Files = local.Files,
            ScannedBytes = local.ScannedBytes,
            Blocks = blocks,
            SplitNote = splitNote,
            SourceMode = haveServer ? "本地 + 云端（分列）" : "本地日志",
            ProviderSummary = cfg.Enabled ? cfg.SafeSummary() : "服务端接口未启用",
            CoveredUntilUtc = haveServer ? _coveredUntilUtc : null,
            ProviderNote = note,
            SplitWarning = haveServer ? splitWarning : string.Empty,
        };
        SnapshotBuilder.CopyBlock(snap, primary);
        return snap;
    }

    private readonly object _ledgerLock = new();

    // ---------------- 服务端用量接口 ----------------

    /// <summary>
    /// 从水位线滚到「现在 - 滞后带」：跨度超过 WindowMaxDays 就切段，
    /// 每段内部把分页翻完；一轮最多 MaxWindowsPerRun 段，避免首次同步把界面卡死。
    /// </summary>
    private async Task PullProviderAsync(ProviderConfig cfg, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var plan = ProviderClient.PlanWindows(_coveredUntilUtc, nowUtc, cfg);
        var merged = _serverLedger?.Clone() ?? new UsageLedger();
        var windows = 0;
        var pages = 0;
        long bytes = 0;

        using var client = new ProviderClient(cfg.TimeoutSeconds);
        var deadline = nowUtc.AddSeconds(cfg.ProviderBudgetSeconds);
        var hitBudget = false;
        foreach (var (from, to) in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline)
            {
                hitBudget = true;
                break;
            }

            var r = await client.FetchAsync(cfg, from, to, ct).ConfigureAwait(false);
            merged.MergeOverwrite(r.Ledger);
            windows++;
            pages += r.Pages;
            bytes += r.BytesIn;
        }

        var advanced = plan.Count > 0
            ? (hitBudget ? plan[Math.Max(0, windows - 1)].To : plan[^1].To)
            : (_coveredUntilUtc ?? nowUtc.AddMinutes(-cfg.LagMinutes));

        _serverLedger = merged;
        _providerState.SaveLedger(merged);
        _coveredUntilUtc = advanced;
        _providerState.CoveredUntilUtc = advanced;
        _providerState.LastSuccessUtc = nowUtc;
        _providerState.LastAttemptUtc = nowUtc;
        _providerState.LastError = null;
        _providerState.ServerTotalTokens = merged.TotalTokens();
        SaveProviderState();

        // 上一轮失败、本轮成功：补一条恢复日志（失败那边的重复告警已做去重）
        if (!string.IsNullOrEmpty(_lastProviderNote))
        {
            Log.Info("接口已恢复，上一轮问题：" + _lastProviderNote);
            _lastProviderNote = string.Empty;
        }

        Log.Info($"服务端用量拉取完成：{windows} 段 / {pages} 页 / {Format.Bytes(bytes)}"
               + (hitBudget ? $"（本轮接口预算 {cfg.ProviderBudgetSeconds}s 用完，剩余窗口下轮继续）" : string.Empty)
               + $"，水位线推到 {advanced:yyyy-MM-dd HH:mm}Z，累计 {Format.Tokens(_providerState.ServerTotalTokens)} token");
    }

    private void RememberProviderFailure(string note)
    {
        _providerState.LastAttemptUtc = DateTime.UtcNow;
        _providerState.LastError = note;
        SaveProviderState();

        // 同一个原因每分钟刷一条 WARN 会把日志埋掉（实测 121 条）：只在原因变化时记
        if (string.Equals(note, _lastProviderNote, StringComparison.Ordinal)) return;
        _lastProviderNote = note;
        Log.Warn(note + "（保留上一次服务端数据，并继续用本地日志兜底）");
    }

    private void OnProviderConfigChanged()
    {
        var cfg = ProviderConfig.Parse(Settings.Get("provider", string.Empty));
        if (!cfg.Enabled)
        {
            // 关掉接口就不能再拿旧的服务端数据压制本地日志
            _serverLedger = null;
            _coveredUntilUtc = null;
            _providerState.CoveredUntilUtc = null;
            SaveProviderState();
            Log.Info("服务端接口已关闭，回到纯本地日志模式");
        }
        _ = RefreshAsync("接口配置变更");
    }

    private string ProviderStatePath() => Path.Combine(_cacheDir, "provider-state-v1.json");

    private ProviderState LoadProviderState()
    {
        try
        {
            if (File.Exists(ProviderStatePath()))
                return ProviderState.Parse(File.ReadAllText(ProviderStatePath()));
        }
        catch (Exception ex) { Log.Warn($"读不到同步水位线（忽略）：{ex.Message}"); }
        return new ProviderState();
    }

    private void SaveProviderState()
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            File.WriteAllText(ProviderStatePath(), _providerState.ToJson());
        }
        catch (Exception)
        {
            // 水位线写不下去只影响下次从哪里续拉，不影响本轮显示
        }
    }

    /// <summary>单价/目录变了但没重扫：直接用已有账本重算快照（很便宜，不必读盘）。</summary>
    private void RecomputeFromLedger(string reason)
    {
        var pricing = PricingConfig.Parse(Settings.Get("pricing", string.Empty));
        var cfg = ProviderConfig.Parse(Settings.Get("provider", string.Empty));
        UsageLedger? local;
        lock (_ledgerLock) { local = _lastLedger; }

        if (local is null)
        {
            RunOnUI(() => Apply(new Snapshot { Currency = pricing.Currency, Error = "还没有扫描结果" }));
            return;
        }

        var snap = ComposeSnapshot(local, pricing, cfg, string.Empty);
        if (snap.ServerUndercount) snap.ProviderNote = snap.SplitNote;
        Log.Info($"重算快照（{reason}）：今日 {Format.Amount(snap.Today.Cost)}{snap.Currency}，{snap.Blocks.Count} 个来源");
        RunOnUI(() => Apply(snap));
    }

    private Snapshot BuildFromLedgerLocked(PricingConfig pricing, string error)
    {
        lock (_ledgerLock)
        {
            var snap = _lastLedger is null
                ? new Snapshot { Currency = pricing.Currency, Error = error }
                : SnapshotBuilder.Build(_lastLedger, pricing);
            if (string.IsNullOrEmpty(snap.Error)) snap.Error = error;
            return snap;
        }
    }

    private void Apply(Snapshot snap)
    {
        _lastSnapshot = snap;
        _view.SetScanning(false);
        _view.Apply(snap);
        _spotlightView?.Apply(snap);

        try { SnapshotApplied?.Invoke(snap); }
        catch (Exception ex) { Log.Warn($"设置页状态同步失败（已忽略）：{ex.Message}"); }
    }

    // ---------------- 聚光卡 ----------------

    private void OpenSpotlight()
    {
        if (_spotlightView is null)
        {
            _spotlightView = new TokenMeterSpotlightView(Manifest, Theme);
            _spotlightView.RefreshRequested += () => _ = RefreshAsync("卡片内手动刷新");
        }

        _spotlightView.Apply(_lastSnapshot ?? new Snapshot { Error = "还没有数据" });

        Context.Island.OpenSpotlight(new IslandSpotlight
        {
            Content = _spotlightView,
            Size = new Windows.Foundation.Size(TokenMeterSpotlightView.CardWidth, TokenMeterSpotlightView.CardHeight),
            OnClosed = () =>
            {
                var v = _spotlightView;
                _spotlightView = null;                       // 卡片窗口已经卸掉，下次重新建一棵树
                v?.OnHostClosed();
            },
        });
    }

    // ---------------- 设置变更 ----------------

    private void OnEnabledChanged()
    {
        var on = Settings.Get("enabled", true);
        Log.Info($"显示开关变更为 {on}");
        SetContent(on ? _content : null);
    }

    private void OnPriorityChanged()
    {
        _content = CreateContent();
        if (Settings.Get("enabled", true)) Context.Island.SetContent(_content);
        Log.Info($"常驻优先级变更为 {Settings.Get("priority", 10)}");
    }

    private void OnPricingChanged() => RecomputeFromLedger("单价变更");

    /// <summary>
    /// 设置页顶部状态行。按用户要求只留「时间 + 口径」与出事才说的告警；
    /// 数据源路径、会话文件数与新读字节、单价基数、接口概要、缓存命中率、未定价清单全部不再在这里显示。
    /// </summary>
    private string HintLine(Snapshot s)
    {
        var parts = new List<string>
        {
            $"更新于 {Format.Ago(s.GeneratedUtc)}（{Format.Clock(s.GeneratedUtc)}）",
            $"当前口径：{s.SourceMode}",
            $"费用口径：{s.Today.CostBasis}",
        };

        // 这三条不是说明文字，而是「出事才会出现」的告警：接口降级/对账偏低、扫描提示、扫描出错
        if (!string.IsNullOrEmpty(s.ProviderNote)) parts.Add(s.ProviderNote);
        if (!string.IsNullOrEmpty(s.SplitWarning)) parts.Add(s.SplitWarning);
        if (!string.IsNullOrEmpty(s.Note)) parts.Add($"提示：{s.Note}");
        if (!string.IsNullOrEmpty(s.Error)) parts.Add($"出错：{s.Error}");

        return string.Join(" ｜ ", parts);
    }

    // ---------------- 设置页 ----------------

    private UIElement BuildSettingsPage()
    {
        var page = new StackPanel { Spacing = 10, Padding = new Thickness(0, 4, 0, 12) };

        page.Children.Add(new TextBlock
        {
            Text = Manifest.Name,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
        });

        page.Children.Add(new TextBlock
        {
            Text = "费用是按你填的单价算出来的，没设置单价的模型会被标成「未定价」。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });

        // 状态行先声明，下面的所有事件处理器都要用它（晚声明会被「未赋值」拦住）
        var statusText = new TextBlock { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };

        void Status(string text) => statusText.Text = text;

        // 岛面上不再显示的那行提示，改到这里：跟着每次扫描回推实时更新，页面关掉时退订
        var hintText = new TextBlock { FontSize = 12, Opacity = 0.72, TextWrapping = TextWrapping.Wrap };
        Action<Snapshot?> onSnapshot = s =>
        {
            if (s is not null) hintText.Text = HintLine(s);
        };
        SnapshotApplied += onSnapshot;
        page.Unloaded += (_, _) => SnapshotApplied -= onSnapshot;
        if (_lastSnapshot is not null) hintText.Text = HintLine(_lastSnapshot);

        // ---- 实时状态 ----
        page.Children.Add(hintText);

        // ---- 显示开关 ----
        var toggle = new ToggleSwitch
        {
            Header = "在灵动岛上显示",
            IsOn = Settings.Get("enabled", true),
        };
        toggle.Toggled += (_, _) => Settings.Set("enabled", toggle.IsOn);
        page.Children.Add(Card(toggle));

        // ---- 刷新间隔 ----
        var intervalBox = new ComboBox
        {
            Header = "刷新间隔",
            MinWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var label in new[] { "10 秒", "30 秒", "1 分钟", "2 分钟", "5 分钟", "10 分钟" })
            intervalBox.Items.Add(label);

        var current = Settings.Get("refreshSeconds", 60);
        var seconds = new[] { 10, 30, 60, 120, 300, 600 };
        var idx = Array.IndexOf(seconds, current);
        intervalBox.SelectedIndex = idx >= 0 ? idx : 2;

        intervalBox.SelectionChanged += (_, _) =>
        {
            var i = intervalBox.SelectedIndex;
            if (i < 0 || i >= seconds.Length) return;
            if (Settings.Get("refreshSeconds", 60) == seconds[i]) return;   // 值没变就别写
            Settings.Set("refreshSeconds", seconds[i]);
            Status($"刷新间隔已改为 {intervalBox.SelectedItem}");
        };
        page.Children.Add(Card(intervalBox));

        // ---- 小岛常驻优先级 ----
        var priorityBox = new TextBox
        {
            Header = "小岛常驻优先级（多个插件抢常驻位时调大；只影响小岛常驻排序）",
            Text = Settings.Get("priority", 10).ToString(System.Globalization.CultureInfo.InvariantCulture),
            MinWidth = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        priorityBox.LostFocus += (_, _) => CommitPriority(priorityBox);
        priorityBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) CommitPriority(priorityBox);
        };
        page.Children.Add(Card(priorityBox));

        // ---- 数据目录 ----
        var rootsBox = new TextBox
        {
            Header = "会话记录目录（留空 = 自动）",
            Text = Settings.Get("roots", string.Empty),
            PlaceholderText = UsageScanner.DefaultRoot(),
            MinWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        rootsBox.LostFocus += (_, _) => CommitRoots(rootsBox);
        rootsBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) CommitRoots(rootsBox);
        };
        page.Children.Add(Card(rootsBox));

        // ---- 本地日得的模型归属（手动填）----
        var localScopeBox = new TextBox
        {
            Header = "本地日志统计哪些模型（逗号分隔，留空 = 除云端认领外的全部）",
            Text = Settings.Get("localScope", string.Empty),
            PlaceholderText = "例如 SCNet-Max, GLM-",
            MinWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        localScopeBox.LostFocus += (_, _) => CommitLocalScope(localScopeBox);
        localScopeBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) CommitLocalScope(localScopeBox);
        };
        page.Children.Add(Card(localScopeBox));

        // ---- 货币符号 + 单价基数 ----
        var currencyBox = new TextBox
        {
            Header = "货币符号",
            Text = PricingConfig.Parse(Settings.Get("pricing", string.Empty)).Currency,
            MinWidth = 110,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var perBox = new TextBox
        {
            Header = "单价基数（每多少 token）",
            Text = PricingConfig.Parse(Settings.Get("pricing", string.Empty)).PerTokens
                       .ToString(System.Globalization.CultureInfo.InvariantCulture),
            MinWidth = 190,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        currencyBox.LostFocus += (_, _) => CommitCurrency(currencyBox);
        currencyBox.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) CommitCurrency(currencyBox); };
        perBox.LostFocus += (_, _) => CommitPer(perBox);
        perBox.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) CommitPer(perBox); };

        var moneyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        moneyRow.Children.Add(currencyBox);
        moneyRow.Children.Add(perBox);
        page.Children.Add(Card(moneyRow));

        // ---- 服务端用量接口（优先于本地日志，失败自动降级）----
        var prov = ProviderConfig.Parse(Settings.Get("provider", string.Empty));

        page.Children.Add(new TextBlock
        {
            Text = "服务端用量接口（可选）",
            FontSize = 13,
            Margin = new Thickness(0, 8, 0, 0),
        });
        page.Children.Add(new TextBlock
        {
            Text = "启用后优先用服务端数据计费与跨设备汇总；接口不可用、超时或权限不足时自动降级回本地日志。"
                 + "密钥不保存在插件里，只填环境变量名。下面这些默认值只是 OpenAI 风格的示例，"
                 + "请按你账号实际能调通的接口改（字段名、分页、时间跨度限制都在高级 JSON 里）。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });

        var provToggle = new ToggleSwitch { Header = "启用服务端接口", IsOn = prov.Enabled };
        provToggle.Toggled += (_, _) => SetProvider(c => c.Enabled = provToggle.IsOn);
        page.Children.Add(Card(provToggle));

        // 云端只算它自己报上来的模型；两边重叠时以云端为准并从本地剔掉，避免算两遍
        var provScopeBox = MakeLine("云端统计哪些模型（逗号分隔，留空 = 上报什么就归什么）", prov.ModelScope, "例如 deepseek-, claude-", 460);
        var provCurrencyBox = MakeLine("云端币种", prov.Currency, "$", 120);
        CommitOnEnter(provScopeBox, () => SetProvider(c => c.ModelScope = (provScopeBox.Text ?? string.Empty).Trim()));
        CommitOnEnter(provCurrencyBox, () => SetProvider(c => c.Currency = (provCurrencyBox.Text ?? string.Empty).Trim()));
        page.Children.Add(Card(provScopeBox));
        page.Children.Add(Card(provCurrencyBox));

        var baseUrlBox = MakeLine("接口根地址 BaseUrl", prov.BaseUrl, "https://api.example.com/v1", 460);
        var pathBox = MakeLine("用量查询路径 Path", prov.Path, "/organization/usage/completions", 460);
        CommitOnEnter(baseUrlBox, () => SetProvider(c => c.BaseUrl = (baseUrlBox.Text ?? string.Empty).Trim().TrimEnd('/')));
        CommitOnEnter(pathBox, () => SetProvider(c =>
        {
            var p = (pathBox.Text ?? string.Empty).Trim();
            if (p.Length > 0 && !p.StartsWith('/')) p = "/" + p;
            c.Path = p;
        }));
        page.Children.Add(Card(baseUrlBox));
        page.Children.Add(Card(pathBox));

        var envBox = MakeLine("密钥所在环境变量名", prov.ApiKeyEnvVar, "TOKEN_METER_API_KEY", 300);
        var provNameBox = MakeLine("显示名", prov.DisplayName, "自定义接口", 200);
        CommitOnEnter(envBox, () => SetProvider(c => c.ApiKeyEnvVar = (envBox.Text ?? string.Empty).Trim()));
        CommitOnEnter(provNameBox, () => SetProvider(c => c.DisplayName = (provNameBox.Text ?? string.Empty).Trim()));
        var provRow1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        provRow1.Children.Add(provNameBox);
        provRow1.Children.Add(envBox);
        page.Children.Add(Card(provRow1));

        var winBox = MakeLine("单段最大天数", prov.WindowMaxDays.ToString(CultureInfo.InvariantCulture), "30", 150);
        var lagBox = MakeLine("滞后分钟", prov.LagMinutes.ToString(CultureInfo.InvariantCulture), "120", 150);
        var backfillBox = MakeLine("首次往回追天数", prov.InitialBackfillDays.ToString(CultureInfo.InvariantCulture), "7", 150);
        CommitOnEnter(winBox, () => SetProvider(c => { if (TryInt(winBox.Text, out var v)) c.WindowMaxDays = v; }));
        CommitOnEnter(lagBox, () => SetProvider(c => { if (TryInt(lagBox.Text, out var v)) c.LagMinutes = v; }));
        CommitOnEnter(backfillBox, () => SetProvider(c => { if (TryInt(backfillBox.Text, out var v)) c.InitialBackfillDays = v; }));
        var provRow2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        provRow2.Children.Add(winBox);
        provRow2.Children.Add(lagBox);
        provRow2.Children.Add(backfillBox);
        page.Children.Add(Card(provRow2));

        // 预设：把「接一家要改哪十来个字段」变成一次选择。套用后仍要自己填 BaseUrl 与环境变量密钥。
        var presetBox = new ComboBox
        {
            Header = "套用接口预设",
            MinWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var pr in ProviderPresets.All) presetBox.Items.Add(pr.Name);
        presetBox.PlaceholderText = "选一个，再点右边按钮";
        var applyPreset = new Button { Content = "套用所选预设", VerticalAlignment = VerticalAlignment.Center };
        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        presetRow.Children.Add(presetBox);
        presetRow.Children.Add(applyPreset);
        page.Children.Add(Card(presetRow));

        // 高级：整份配置直接改。Parse 对坏 JSON 会静默回默认值，所以这里先严格校验再落盘。
        var jsonBox = new TextBox
        {
            Header = "完整配置 JSON（字段映射、分页、口径都在这里改）",
            Text = prov.ToJson(),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 640,
            MinHeight = 220,
            FontFamily = new FontFamily("Consolas, Cascadia Mono"),
            Visibility = Visibility.Collapsed,
        };
        var jsonStatus = new TextBlock { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        var jsonToggle = new Button { Content = "展开编辑完整配置 JSON" };

        void CommitProviderJson()
        {
            var text = (jsonBox.Text ?? string.Empty).Trim();
            if (text.Length == 0) return;
            try
            {
                using var _ = JsonDocument.Parse(text);   // 先验语法，坏就拒绝保存
            }
            catch (Exception ex)
            {
                jsonStatus.Text = "JSON 格式不对，没有保存：" + ex.Message;
                return;
            }
            var cfg = ProviderConfig.Parse(text);
            var normalized = cfg.ToJson();
            if (string.Equals(normalized, Settings.Get("provider", string.Empty), StringComparison.Ordinal)) return;
            Settings.Set("provider", normalized);
            jsonBox.Text = normalized;                     // 回填规范化后的内容，让用户看到默认值被补全了什么
            jsonStatus.Text = "已保存并重新拉取。";
        }
        jsonBox.LostFocus += (_, _) => CommitProviderJson();

        // 默认收起：整份 JSON 平铺在页面上既占地方也容易误改，点按钮才展开编辑
        jsonToggle.Click += (_, _) =>
        {
            if (jsonBox.Visibility == Visibility.Visible)
            {
                CommitProviderJson();          // 收起前先落盘，不依赖 LostFocus
                jsonBox.Visibility = Visibility.Collapsed;
                jsonToggle.Content = "展开编辑完整配置 JSON";
            }
            else
            {
                // 每次展开都从设置里重读，避免刚才用上面那些单字段框改过之后看到旧内容
                jsonBox.Text = ProviderConfig.Parse(Settings.Get("provider", string.Empty)).ToJson();
                jsonBox.Visibility = Visibility.Visible;
                jsonToggle.Content = "保存并收起";
                jsonBox.Focus(FocusState.Programmatic);
            }
        };

        page.Children.Add(jsonToggle);
        page.Children.Add(jsonBox);
        page.Children.Add(jsonStatus);

        // 套用预设：不改用户当前的联网开关，也不保存任何密钥
        applyPreset.Click += (_, _) =>
        {
            var idx = presetBox.SelectedIndex;
            if (idx < 0 || idx >= ProviderPresets.All.Length)
            {
                Status("先从下拉里选一个预设。");
                return;
            }

            var preset = ProviderPresets.All[idx];
            var cfg = preset.Build();
            cfg.Enabled = ProviderConfig.Parse(Settings.Get("provider", string.Empty)).Enabled;
            var alias = (provNameBox.Text ?? string.Empty).Trim();
            if (alias.Length > 0) cfg.DisplayName = alias;

            Settings.Set("provider", cfg.ToJson());   // 触发 OnProviderConfigChanged → 重新拉取

            if (provToggle.IsOn != cfg.Enabled) provToggle.IsOn = cfg.Enabled;
            baseUrlBox.Text = cfg.BaseUrl;
            pathBox.Text = cfg.Path;
            envBox.Text = cfg.ApiKeyEnvVar;
            winBox.Text = cfg.WindowMaxDays.ToString(CultureInfo.InvariantCulture);
            lagBox.Text = cfg.LagMinutes.ToString(CultureInfo.InvariantCulture);
            backfillBox.Text = cfg.InitialBackfillDays.ToString(CultureInfo.InvariantCulture);
            jsonBox.Text = cfg.ToJson();
            Status($"已套用「{preset.Name}」（{preset.Basis}）。密钥请自己设进环境变量 {cfg.ApiKeyEnvVar}，插件不保存它。");
        };

        // ---- 模型单价表 ----
        page.Children.Add(new TextBlock
        {
            Text = "模型单价（都按「每基数 token 多少钱」填，直接照服务商价目表抄）",
            FontSize = 13,
            Margin = new Thickness(0, 6, 0, 0),
        });

        var priceList = new StackPanel { Spacing = 8 };
        page.Children.Add(priceList);

        var emptyHint = new TextBlock
        {
            Text = "还没有配置任何模型 —— 费用会显示「未定价」。用下面的一行添加。",
            FontSize = 12,
            Opacity = 0.7,
            Visibility = Visibility.Collapsed,
        };
        page.Children.Add(emptyHint);

        // ---- 新增模型：名字可手输，也可从已发现的模型里选 ----
        var nameBox = new TextBox
        {
            Header = "模型名",
            PlaceholderText = "例如 gpt-4o-mini / claude-sonnet",
            MinWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var discoveredBox = new ComboBox
        {
            Header = "已发现的模型",
            MinWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var name in DiscoveredModels()) discoveredBox.Items.Add(name);
        discoveredBox.PlaceholderText = discoveredBox.Items.Count > 0 ? "选一个填到左边" : "暂无扫描结果";
        discoveredBox.SelectionChanged += (_, _) =>
        {
            var picked = discoveredBox.SelectedItem as string;
            if (!string.IsNullOrWhiteSpace(picked)) nameBox.Text = picked;    // 只是填输入框，不写设置
        };

        var newInput = new TextBox { Header = "输入", PlaceholderText = "0", Width = 110 };
        var newOutput = new TextBox { Header = "输出", PlaceholderText = "0", Width = 110 };
        var newCacheRead = new TextBox { Header = "缓存读", PlaceholderText = "0", Width = 110 };
        var newCacheWrite = new TextBox { Header = "缓存写", PlaceholderText = "0", Width = 110 };

        // 定价输入分两行：一行摆模型名与选取，一行摆四个价格，否则单行太宽装不下
        var addRowNames = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        addRowNames.Children.Add(nameBox);
        addRowNames.Children.Add(discoveredBox);
        page.Children.Add(addRowNames);

        var addRowPrices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        addRowPrices.Children.Add(newInput);
        addRowPrices.Children.Add(newOutput);
        addRowPrices.Children.Add(newCacheRead);
        addRowPrices.Children.Add(newCacheWrite);
        page.Children.Add(addRowPrices);

        var addButton = new Button { Content = "添加 / 更新单价" };
        var rescanButton = new Button { Content = "清空缓存并完整重扫" };
        var refreshButton = new Button { Content = "立即刷新" };
        var testButton = new Button { Content = "发一条测试消息" };

        addButton.Click += (_, _) =>
        {
            // 动作之前先提交本页其它还没保存的输入（按钮点击早于 LostFocus，§15.1）
            CommitCurrency(currencyBox);
            CommitPer(perBox);
            CommitRoots(rootsBox);

            var name = (nameBox.Text ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                Status("先给模型起个名字（可以直接从「已发现的模型」里选）。");
                return;
            }
            if (!PricingConfig.TryParseAmount(newInput.Text, out var vi) ||
                !PricingConfig.TryParseAmount(newOutput.Text, out var vo) ||
                !PricingConfig.TryParseAmount(newCacheRead.Text, out var vcr) ||
                !PricingConfig.TryParseAmount(newCacheWrite.Text, out var vcw))
            {
                Status("单价得是数字，比如 2.5 。留空按 0 计。");
                return;
            }

            var cfg = PricingConfig.Parse(Settings.Get("pricing", string.Empty));
            cfg.Upsert(new ModelPrice { Model = name, Input = vi, Output = vo, CacheRead = vcr, CacheWrite = vcw });
            Settings.Set("pricing", cfg.ToJson());     // 触发 OnPricingChanged → 重算快照

            RebuildPriceList(priceList, emptyHint, statusText);
            nameBox.Text = string.Empty;
            newInput.Text = newOutput.Text = newCacheRead.Text = newCacheWrite.Text = string.Empty;
            Status($"已保存 {name} 的单价。");
        };

        rescanButton.Click += (_, _) =>
        {
            CommitRoots(rootsBox);
            CommitCurrency(currencyBox);
            CommitPer(perBox);
            lock (_ledgerLock) { _lastLedger = null; }
            _scanner.ForgetCache();
            // 「完整重扫」必须连云端水位线一起清：否则水位线会遮住本该重拉的那段，
            // 而“完整”两个字就是假的
            _serverLedger = null;
            _coveredUntilUtc = null;
            _providerState = new ProviderState();
            SaveProviderState();
            Status("已清空缓存（含云端水位线），正在从头重扫…（第一次会慢）");
            _ = RefreshAsync("手动完整重扫");
        };

        refreshButton.Click += (_, _) =>
        {
            CommitRoots(rootsBox);
            CommitCurrency(currencyBox);
            CommitPer(perBox);
            CommitPriority(priorityBox);
            CommitProviderJson();
            Status("正在刷新…");
            _ = RefreshAsync("手动刷新");
        };

        testButton.Click += (_, _) =>
        {
            var s = _lastSnapshot;
            Context.Island.ShowMessage(new IslandMessage
            {
                Title = Manifest.Name,
                Text = s is null || !s.HasData
                    ? "还没有可显示的用量数据"
                    : $"今日 {Format.Money(s.Currency, s.Today.Cost)} / {Format.Tokens(s.Today.C.Total)} tok",
                Glyph = Manifest.IconGlyph ?? "\uE8EF",
                Duration = TimeSpan.FromSeconds(3),
            });
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        buttons.Children.Add(addButton);
        buttons.Children.Add(refreshButton);
        buttons.Children.Add(rescanButton);
        buttons.Children.Add(testButton);
        page.Children.Add(buttons);
        page.Children.Add(statusText);

        // 把后台忙碌状态接回按钮：以前只改了一行小字（在页面底部容易被忽略），
        // 点了以后看起来「没反应」
        Action<bool> onBusy = busy =>
        {
            refreshButton.IsEnabled = !busy;
            rescanButton.IsEnabled = !busy;
            refreshButton.Content = busy ? "刷新中…" : "立即刷新";
            rescanButton.Content = busy ? "重扫中…" : "清空缓存并完整重扫";
        };
        BusyChanged += onBusy;
        page.Unloaded += (_, _) => BusyChanged -= onBusy;

        RebuildPriceList(priceList, emptyHint, statusText);
        if (_lastSnapshot is not null) statusText.Text = StatusLine(_lastSnapshot);

        return page;
    }

    private string StatusLine(Snapshot s) => s.HasData
        ? $"今日 {Format.Money(s.Currency, s.Today.Cost)} ／ {Format.Tokens(s.Today.C.Total)} tok ／ {s.Today.C.Requests} 次请求 ｜ 覆盖 {s.Files} 个文件、{s.LedgerDays} 天记录"
          + (s.UnpricedNames.Count > 0 ? $" ｜ {s.UnpricedNames.Count} 个模型未定价" : "")
        : "账本里还没有记录（确认一下上面的目录对不对）。";

    private IEnumerable<string> DiscoveredModels()
    {
        UsageLedger? ledger;
        lock (_ledgerLock) { ledger = _lastLedger; }
        if (ledger is null) return Array.Empty<string>();
        try { return ledger.AllModels().ToList(); }
        catch (Exception ex)
        {
            Log.Warn($"取模型清单失败（已忽略）：{ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>重建单价区。每次点进设置页、或增删单价后调用。</summary>
    private void RebuildPriceList(StackPanel host, TextBlock emptyHint, TextBlock statusText)
    {
        host.Children.Clear();
        var cfg = PricingConfig.Parse(Settings.Get("pricing", string.Empty));

        if (cfg.Models.Count == 0)
        {
            emptyHint.Visibility = Visibility.Visible;
            return;
        }
        emptyHint.Visibility = Visibility.Collapsed;

        foreach (var price in cfg.Models.ToList())
        {
            host.Children.Add(BuildPriceCard(price, cfg, statusText, host));
        }
    }

    private UIElement BuildPriceCard(ModelPrice price, PricingConfig cfg, TextBlock statusText, StackPanel host)
    {
        var titleRow = new Grid { ColumnSpacing = 10 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = price.Model,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 0);

        var remove = new Button { Content = "删除", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(remove, 1);
        titleRow.Children.Add(name);
        titleRow.Children.Add(remove);

        var input = new TextBox { Header = "输入", Text = Num(price.Input), Width = 110 };
        var output = new TextBox { Header = "输出", Text = Num(price.Output), Width = 110 };
        var cacheRead = new TextBox { Header = "缓存读", Text = Num(price.CacheRead), Width = 110 };
        var cacheWrite = new TextBox { Header = "缓存写", Text = Num(price.CacheWrite), Width = 110 };

        // 四个价格框回到一行（框宽不变，仍为 110）
        var editors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 6, 0, 0) };
        editors.Children.Add(input);
        editors.Children.Add(output);
        editors.Children.Add(cacheRead);
        editors.Children.Add(cacheWrite);

        var box = new StackPanel { Spacing = 4 };
        box.Children.Add(titleRow);
        box.Children.Add(editors);

        var card = Card(box);
        var captured = price.Model;

        void Commit()
        {
            if (!PricingConfig.TryParseAmount(input.Text, out var vi) ||
                !PricingConfig.TryParseAmount(output.Text, out var vo) ||
                !PricingConfig.TryParseAmount(cacheRead.Text, out var vcr) ||
                !PricingConfig.TryParseAmount(cacheWrite.Text, out var vcw))
            {
                statusText.Text = $"{captured}：单价里有不是数字的内容，没有保存。";
                return;
            }

            var current = PricingConfig.Parse(Settings.Get("pricing", string.Empty));
            var existing = current.Find(captured);
            if (existing is not null && existing.Input == vi && existing.Output == vo
                && existing.CacheRead == vcr && existing.CacheWrite == vcw)
            {
                return;                      // 值没变就别写，免得每次失焦都触发一次重算
            }

            current.Upsert(new ModelPrice { Model = captured, Input = vi, Output = vo, CacheRead = vcr, CacheWrite = vcw });
            Settings.Set("pricing", current.ToJson());
            statusText.Text = $"{captured} 的单价已保存。";
        }

        foreach (var tb in new[] { input, output, cacheRead, cacheWrite })
        {
            tb.LostFocus += (_, _) => Commit();
            tb.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Commit(); };
        }

        remove.Click += (_, _) =>
        {
            var current = PricingConfig.Parse(Settings.Get("pricing", string.Empty));
            if (!current.Remove(captured))
            {
                statusText.Text = $"{captured} 已经不在了。";
                return;
            }
            Settings.Set("pricing", current.ToJson());
            host.Children.Remove(card);
            statusText.Text = $"已删除 {captured} 的单价。";
        };

        return card;
    }

    private static string Num(decimal v)
        => v == 0m ? "0" : v.ToString("0.################", System.Globalization.CultureInfo.InvariantCulture);

    private Border Card(UIElement child) => new()
    {
        Style = (Style)Application.Current.Resources["SettingsCardStyle"],
        Child = child,
    };

    // ---- 三个提交点：失焦 + 回车 + 动作前（§15.1）----

    /// <summary>改一个配置字段：只在真的变了时写回，避免每次失焦都触发一轮重拉。</summary>
    private void SetProvider(Action<ProviderConfig> mutate)
    {
        var cfg = ProviderConfig.Parse(Settings.Get("provider", string.Empty));
        var before = cfg.ToJson();
        mutate(cfg);
        var after = cfg.ToJson();
        if (string.Equals(before, after, StringComparison.Ordinal)) return;
        Settings.Set("provider", after);   // 触发 OnProviderConfigChanged
    }

    private static TextBox MakeLine(string header, string text, string placeholder, double minWidth)
        => new()
        {
            Header = header,
            Text = text,
            PlaceholderText = placeholder,
            MinWidth = minWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

    private void CommitOnEnter(TextBox box, Action commit)
        => box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) commit();
        };

    private static bool TryInt(string? text, out int value)
        => int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value > 0;

    private void CommitLocalScope(TextBox box)
    {
        var text = (box.Text ?? string.Empty).Trim();
        if (string.Equals(text, Settings.Get("localScope", string.Empty), StringComparison.Ordinal)) return;
        Settings.Set("localScope", text);   // 触发 RecomputeFromLedger
    }

    private void CommitRoots(TextBox box)
    {
        var text = (box.Text ?? string.Empty).Trim();
        if (string.Equals(text, Settings.Get("roots", string.Empty), StringComparison.Ordinal)) return;
        Settings.Set("roots", text);     // 会触发 OnSettingsChanged("roots") → 重扫
    }

    private void CommitPriority(TextBox box)
    {
        var raw = (box.Text ?? string.Empty).Trim();
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p))
            return;
        p = Math.Clamp(p, -1000, 1000);
        if (Settings.Get("priority", 10) == p) return;      // 值没变就别写
        Settings.Set("priority", p);                        // 触发 OnPriorityChanged → 重建常驻内容
    }

    private void CommitCurrency(TextBox box)
    {
        var symbol = (box.Text ?? string.Empty).Trim();
        if (symbol.Length == 0 || symbol.Length > 3) return;

        var cfg = PricingConfig.Parse(Settings.Get("pricing", string.Empty));
        if (string.Equals(cfg.Currency, symbol, StringComparison.Ordinal)) return;
        cfg.Currency = symbol;
        Settings.Set("pricing", cfg.ToJson());
    }

    private void CommitPer(TextBox box)
    {
        var raw = (box.Text ?? string.Empty).Replace(",", string.Empty).Trim();
        if (raw.Length == 0) return;
        if (!long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var per) || per <= 0)
            return;

        var cfg = PricingConfig.Parse(Settings.Get("pricing", string.Empty));
        if (cfg.PerTokens == per) return;
        cfg.PerTokens = per;
        Settings.Set("pricing", cfg.ToJson());
    }
}
