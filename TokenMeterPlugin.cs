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

    /// <summary>每个接口各自的云端账本与水位线，键 = ProviderConfig.Id。
    /// 必须分份：共用一个水位线时，A 拉过的区间会把 B 永久遮住，那块数据再也拉不回来。
    /// 账本要和水位线一起恢复，只恢复水位线等于把已拉区间连同数据一起弄丢。</summary>
    private readonly Dictionary<string, UsageLedger> _serverLedgers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime?> _coveredUntil = new(StringComparer.Ordinal);
    private MultiProviderState _states = new();
    private string _cacheDir = string.Empty;

    /// <summary>当前生效的接口列表（来自 providers.json）与读到时文件的修改时刻。</summary>
    private List<ProviderConfig> _providers = new();
    private DateTime _providersStamp = DateTime.MinValue;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource _cts = new();
    private bool _stopped;
    private bool _busy;
    private int _secondsSinceRefresh;

    /// <summary>把最新结果回推给还开着的设置页。</summary>
    public event Action<Snapshot?>? SnapshotApplied;

    /// <summary>后台是否在拉取中，用来给设置页按钮做即时反馈。</summary>
    public event Action<bool>? BusyChanged;

    /// <summary>每个接口各自的「上一次失败原因」：日志去重与恢复提示都要按接口分开算，
    /// 否则 A 家正常会把 B 家的告警当成"已恢复"。</summary>
    private readonly Dictionary<string, string> _lastProviderNotes = new(StringComparer.Ordinal);

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
        RestoreProviderState(LoadProviders());

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
        // provider 键不再监听：接口配置已搬到 providers.json，由心跳查文件变化
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
        ExpandedSize = new Windows.Foundation.Size(430, 188),
        OnTap = OpenSpotlight,          // 点击岛体 = 打开聚光卡（§16 规则 2）
    };

    private void OnTick()
    {
        _secondsSinceRefresh++;

        // 手改了配置文件就自动生效，不要求用户去点按钮。这里只比一次时间戳，很便宜。
        if (!_busy && ProvidersFileChanged())
        {
            _secondsSinceRefresh = 0;
            _ = RefreshAsync("配置文件已修改");
            return;
        }

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
            // 接口列表必须在调用线程读：LoadProviders 里要碰宿主设置，
            // 而后台线程读设置是这类插件出 COMException 的经典位置（原来也是「外面读、里面解析」）
            var providers = LoadProviders();
            // 被删掉的接口不能留下孤儿账本：状态文件会一直涨，而且再也无人去读它
            _states.DropOrphans(providers.Select(p => p.Id));
            try
            {
                snap = await Task.Run(async () =>
                {
                    token.ThrowIfCancellationRequested();

                    // 本地日志始终先扫：它既是默认数据源，也是接口挂掉时的兜底
                    var local = _scanner.Scan(roots, token);
                    if (!_stopped) _scanner.SaveCache();

                    var active = providers.Where(p => p.Enabled).ToList();
                    if (active.Count == 0)
                    {
                        // 用户把所有接口都关了：不能拿旧的服务端数据去压制本地日志
                        _serverLedgers.Clear();
                        _coveredUntil.Clear();
                    }

                    // 先把本地结果立即上屏：接口慢、卡住或在翻页时，界面也不会空着等你。
                    // 这里必须走多来源合成：否则这一帧只有本地一块，
                    // 双柱图与云端 KPI 栏会在每次刷新中途闪没一下（已发生过）。
                    RunOnUI(() => Apply(ComposeSnapshot(local, pricing, providers, "云端拉取中…")));

                    // 逐个接口独立成败：一家挂了不能连累其它家，也不能让整轮拿不到数
                    var notes = new List<string>();
                    foreach (var cfg in active)
                    {
                        token.ThrowIfCancellationRequested();

                        if (!cfg.IsUsable(out var why))
                        {
                            notes.Add($"{cfg.DisplayName}：接口未就绪（{why}）");
                            RememberProviderFailure(cfg.Id, $"{cfg.DisplayName} 接口未就绪（已降级本地日志）：{why}");
                            continue;
                        }

                        try
                        {
                            await PullProviderAsync(cfg, token).ConfigureAwait(false);
                            ClearProviderFailure(cfg.Id);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (ProviderException ex)
                        {
                            notes.Add($"{cfg.DisplayName}：接口不可用，已降级本地日志（{ex.Message}）");
                            RememberProviderFailure(cfg.Id, $"{cfg.DisplayName} 接口不可用：{ex.Message}");
                        }
                        catch (Exception ex)
                        {
                            notes.Add($"{cfg.DisplayName}：接口异常，已降级本地日志（{ex.Message}）");
                            RememberProviderFailure(cfg.Id, $"{cfg.DisplayName} 接口异常：{ex.Message}");
                        }
                    }

                    var s = ComposeSnapshot(local, pricing, providers, string.Join(" ; ", notes));
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

    /// <summary>多接口时取最早的那个水位线：云端数据只可信到「最落后的那家也拉完」为止。
    /// 取最大值会虚报覆盖范围，让本地日志在还没同步的区间上被误当成已核对过。</summary>
    private DateTime? CoveredFloor(IReadOnlyList<ProviderConfig> live)
    {
        DateTime? min = null;
        foreach (var p in live)
        {
            var c = _coveredUntil.GetValueOrDefault(p.Id);
            if (c is null) continue;
            if (min is null || c < min) min = c;
        }
        return min;
    }

    /// <summary>
    /// 把本地账本与各接口账本算成【分列的多块】。不同运营商的用量不相交、币种也不同，
    /// 所以绝不合并相加；只有被某个接口认领的模型才从本地剔出去（避免同一模型算两遍）。
    /// Blocks[0] 正常就是本地块 —— 视图拿主块显示岛面标题。
    /// </summary>
    private Snapshot ComposeSnapshot(UsageLedger local, PricingConfig pricing, IReadOnlyList<ProviderConfig> providers, string note)
    {
        var blocks = new List<SourceBlock>();
        var splitNote = string.Empty;
        var splitWarning = string.Empty;
        var undercount = false;
        var undercountDetail = string.Empty;

        // 只有「启用了且真的拿到数据」的接口才参与认领：空账本参与的话，
        // 一个还没拉通的接口会把模型从本地剔走，让数字凭空消失
        var live = providers
            .Where(p => p.Enabled && _serverLedgers.ContainsKey(p.Id) && _serverLedgers[p.Id].Days.Count > 0)
            .ToList();

        SourceBlock primary;
        if (live.Count > 0)
        {
            var tuples = new List<(string, string, UsageLedger, ModelScope, DateTime?, int)>(live.Count);
            foreach (var p in live)
                tuples.Add((p.Id, p.DisplayName, _serverLedgers[p.Id],
                    ModelScope.Parse(p.ModelScope), _coveredUntil.GetValueOrDefault(p.Id), p.UndercountWarnPercent));

            var (localPart, assigned, split) = LedgerSplitter.SplitMany(local, tuples,
                ModelScope.Parse(Settings.Get("localScope", string.Empty)));

            var lb = SnapshotBuilder.BuildBlock(localPart, pricing, "本地日志");
            blocks.Add(lb);

            for (var i = 0; i < live.Count; i++)
            {
                // 每个接口用自己的币种；没报回金额的模型仍回退到本地单价估算
                var serverPricing = PricingConfig.Parse(pricing.ToJson());
                serverPricing.Currency = string.IsNullOrWhiteSpace(live[i].Currency) ? "$" : live[i].Currency.Trim();

                var sb = SnapshotBuilder.BuildBlock(assigned[i].Ledger, serverPricing, live[i].DisplayName);
                blocks.Add(sb);
                MaybeLogReconcileSample(sb, live[i]);
            }

            splitNote = split.Note;
            splitWarning = split.DroppedNote;
            undercount = split.Undercount;
            undercountDetail = split.UndercountDetail;

            // 冲突与低估值都走 ProviderNote（状态行与聚光卡都读它）。
            // 冲突尤其必须报出来：那些模型谁都没认领，用户不去设范围就永远少一块云端数
            if (!string.IsNullOrEmpty(split.ConflictNote))
                note = string.IsNullOrEmpty(note) ? split.ConflictNote : note + "；" + split.ConflictNote;
            if (split.Undercount)
                note = string.IsNullOrEmpty(note) ? split.UndercountDetail : note + "；" + split.UndercountDetail;

            primary = lb.HasData ? lb : blocks[1];
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
            SourceMode = live.Count > 0
                ? (live.Count == 1 ? "本地 + 云端（分列）" : $"本地 + {live.Count} 个云端（分列）")
                : "本地日志",
            ProviderSummary = live.Count > 0
                ? string.Join(" ｜ ", live.Select(p => p.SafeSummary()))
                : (providers.Any(p => p.Enabled) ? "已启用的接口本轮没有可用数据" : "服务端接口未启用"),
            CoveredUntilUtc = CoveredFloor(live),
            ProviderNote = note,
            SplitWarning = live.Count > 0 ? splitWarning : string.Empty,
            ServerUndercount = undercount,
            UndercountDetail = undercountDetail,
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
        var st = _states.For(cfg.Id);
        var plan = ProviderClient.PlanWindows(_coveredUntil.GetValueOrDefault(cfg.Id), nowUtc, cfg);
        var merged = _serverLedgers.TryGetValue(cfg.Id, out var prev) ? prev.Clone() : new UsageLedger();
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
            : (_coveredUntil.GetValueOrDefault(cfg.Id) ?? nowUtc.AddMinutes(-cfg.LagMinutes));

        _serverLedgers[cfg.Id] = merged;
        st.SaveLedger(merged, cfg.LedgerKeepDays);
        _coveredUntil[cfg.Id] = advanced;
        st.CoveredUntilUtc = advanced;
        st.LastSuccessUtc = nowUtc;
        st.LastAttemptUtc = nowUtc;
        st.LastError = null;
        st.ServerTotalTokens = merged.TotalTokens();
        SaveStates();

        Log.Info($"服务端用量拉取完成：{windows} 段 / {pages} 页 / {Format.Bytes(bytes)}"
               + (hitBudget ? $"（本轮接口预算 {cfg.ProviderBudgetSeconds}s 用完，剩余窗口下轮继续）" : string.Empty)
               + $"，水位线推到 {advanced:yyyy-MM-dd HH:mm}Z，累计 {Format.Tokens(st.ServerTotalTokens)} token");
    }

    private readonly Dictionary<string, DateTime> _lastReconcileLogUtc = new(StringComparer.Ordinal);

    /// <summary>
    /// 对账辅助输出：把云端块最近若干天的「按单价推算费用」与「接口上报金额」并排写进日志。
    /// InputIncludesCached 这个口径本机没有可验证的样本（账号里还没有 cache_tokens&gt;0 的记录），
    /// 只能靠用户拿某一天的上报金额去和网关控制台账单对，所以这里只出数、不下结论。
    /// 半小时最多一条：今日数字每分钟都在涨，不设闸就会刷屏（同类告警刷屏修过一次）。
    /// </summary>
    private void MaybeLogReconcileSample(SourceBlock serverBlock, ProviderConfig cfg)
    {
        if (!serverBlock.HasData || serverBlock.Last7.Count == 0) return;

        var nowUtc = DateTime.UtcNow;
        if (_lastReconcileLogUtc.TryGetValue(cfg.Id, out var lastLog) && nowUtc - lastLog < TimeSpan.FromMinutes(30)) return;
        _lastReconcileLogUtc[cfg.Id] = nowUtc;

        var cur = string.IsNullOrWhiteSpace(cfg.Currency) ? "$" : cfg.Currency.Trim();
        var lines = new List<string>();
        foreach (var p in serverBlock.Last7)
        {
            // 没数据的日子不能印 0：0 会被读成「这一天花了 0 元」，跟「没有数据」是两回事
            if (p.Total <= 0 && p.Cost == 0m && p.ServerCost == 0m)
            {
                lines.Add($"  {p.Day:yyyy-MM-dd}｜无数据");
                continue;
            }

            var est = p.Cost > 0m ? p.Cost.ToString("0.####") + cur : "—";
            var rep = p.ServerCost > 0m ? p.ServerCost.ToString("0.####") + cur : "—";
            var diff = p.Cost > 0m && p.ServerCost > 0m
                ? $"{(int)Math.Round((double)(p.Cost - p.ServerCost) * 100.0 / (double)p.ServerCost):+0;-0;0}%"
                : "—";
            lines.Add($"  {p.Day:yyyy-MM-dd}｜{Format.Tokens(p.Total)} tok｜推算 {est}｜上报 {rep}｜推算比上报 {diff}");
        }

        Log.Info($"对账辅助（当前 InputIncludesCached={(cfg.InputIncludesCached ? "true" : "false")}）："
            + "挑一天拿「上报」去对网关控制台账单。推算持续**高于**上报＝有东西被重复计费，"
            + "当前若是 false 就该改 true；持续**低于**＝少算了，当前若是 true 就该改 false。"
            + "两种情况都要先排除单价本身填错或该模型未定价。\n" + string.Join("\n", lines));
    }

    private void RememberProviderFailure(string id, string note)
    {
        var st = _states.For(id);
        st.LastAttemptUtc = DateTime.UtcNow;
        st.LastError = note;
        SaveStates();

        // 同一个原因每分钟刷一条 WARN 会把日志埋掉（实测 121 条）：只在原因变化时记
        if (_lastProviderNotes.TryGetValue(id, out var last) && string.Equals(note, last, StringComparison.Ordinal)) return;
        _lastProviderNotes[id] = note;
        Log.Warn(note + "（保留该接口上一次的服务端数据，并继续用本地日志兜底）");
    }

    /// <summary>本轮这个接口成功了：只在它之前确实报过故障时补一条恢复日志。
    /// 恢复提示必须按接口算 —— 否则 A 家正常会把 B 家的告警误报成"已恢复"。</summary>
    private void ClearProviderFailure(string id)
    {
        if (!_lastProviderNotes.TryGetValue(id, out var last)) return;
        _lastProviderNotes.Remove(id);
        Log.Info("接口已恢复，上一轮问题：" + last);
    }

    private string StatePathV2() => Path.Combine(_cacheDir, "provider-state-v2.json");
    private string StatePathV1() => Path.Combine(_cacheDir, "provider-state-v1.json");

    /// <summary>读按 id 分份的同步状态。v2 不存在时，把单接口时代的 v1（全局水位线 + 全局账本）
    /// 迁移到列表第一个接口名下。v1 文件留着不动：迁移只在首次发生，坏了也不影响 v2 读写。</summary>
    private MultiProviderState LoadStates(IReadOnlyList<ProviderConfig> providers)
    {
        try
        {
            if (File.Exists(StatePathV2())) return MultiProviderState.Parse(File.ReadAllText(StatePathV2()));

            if (providers.Count > 0 && File.Exists(StatePathV1()))
            {
                var legacy = ProviderState.Parse(File.ReadAllText(StatePathV1()));
                if (legacy.CoveredUntilUtc is not null || legacy.HasPersistedLedger)
                {
                    var fresh = new MultiProviderState();
                    fresh.AdoptLegacy(providers[0].Id, legacy);
                    Log.Info("已把单接口时代的同步状态迁移到当前接口名下");
                    return fresh;
                }
            }
        }
        catch (Exception ex) { Log.Warn($"读不到同步水位线（忽略，按全新安装处理）：{ex.Message}"); }

        return new MultiProviderState();
    }

    /// <summary>恢复每个接口各自的账本与水位线。云端账本必须和水位线一起回来：
    /// 只回水位线的话，它会宣称"这段时间已经拉过了"，而那段时间的数据其实全丢了。</summary>
    private void RestoreProviderState(IReadOnlyList<ProviderConfig> providers)
    {
        _states = LoadStates(providers);
        _serverLedgers.Clear();
        _coveredUntil.Clear();

        foreach (var p in providers)
        {
            var st = _states.For(p.Id);
            _coveredUntil[p.Id] = st.CoveredUntilUtc;
            if (st.HasPersistedLedger) _serverLedgers[p.Id] = st.LoadLedger();
        }
    }

    private void SaveStates()
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            File.WriteAllText(StatePathV2(), _states.ToJson());
        }
        catch (Exception)
        {
            // 状态写不下去只影响下次从哪里续拉，不影响本轮显示
        }
    }

    /// <summary>单价/目录变了但没重扫：直接用已有账本重算快照（很便宜，不必读盘）。</summary>
    private void RecomputeFromLedger(string reason)
    {
        var pricing = PricingConfig.Parse(Settings.Get("pricing", string.Empty));
        UsageLedger? local;
        lock (_ledgerLock) { local = _lastLedger; }

        if (local is null)
        {
            RunOnUI(() => Apply(new Snapshot { Currency = pricing.Currency, Error = "还没有扫描结果" }));
            return;
        }

        var snap = ComposeSnapshot(local, pricing, LoadProviders(), string.Empty);
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

        // ---- 服务端用量接口：配置搬到插件目录的 providers.json，这一页只负责看和重载 ----
        page.Children.Add(new TextBlock
        {
            Text = "服务端用量接口（配置文件）",
            FontSize = 13,
            Margin = new Thickness(0, 8, 0, 0),
        });
        page.Children.Add(new TextBlock
        {
            Text = "接口配置不在这一页上编辑了。它以前住在宿主设置里，配一套按 id 关联的表单，"
                 + "而那套表单出过事故（一点即删、套预设会整份覆盖），把真实配置弄没过一次。"
                 + "现在它是插件目录下一个普通的 JSON 数组文件：可以手改、可以备份、可以拿 git 比差异。"
                 + "保存后插件会在下一次心跳自动读到；不放心就点下面的「重新读取」。"
                 + "每个接口各自独立统计、分列显示，绝不跨币种相加；同一个模型被两个接口同时上报时谁都不认，"
                 + "会改由本地日志显示并在下面状态里报出来。密钥不写在文件里，只写环境变量名。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });

        var provPathBox = new TextBox
        {
            Header = "配置文件路径",
            Text = ProvidersPath(),
            IsReadOnly = true,
            MinWidth = 640,
        };
        page.Children.Add(Card(provPathBox));

        var provStatusText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };

        void RenderProviderStatus()
        {
            var list = LoadProviders();
            provStatusText.Text = list.Count == 0
                ? "当前没有加载到任何接口。"
                : "已加载 " + list.Count + " 个接口："
                  + string.Join("；", list.Select(p => $"{LabelOf(p)}（{(p.Enabled ? "启用" : "未启用")}｜{p.BaseUrl}{p.Path}）"));
        }

        RenderProviderStatus();
        page.Children.Add(Card(provStatusText));

        var provReload = new Button { Content = "重新读取配置文件", VerticalAlignment = VerticalAlignment.Center };
        var provSample = new Button { Content = "生成示例配置（仅当文件不存在）", VerticalAlignment = VerticalAlignment.Center };
        var provRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        provRow.Children.Add(provReload);
        provRow.Children.Add(provSample);
        page.Children.Add(Card(provRow));

        provReload.Click += (_, _) =>
        {
            RenderProviderStatus();
            _ = RefreshAsync("手动重新读取配置");
        };

        // 只在文件不存在时生成：文件已经存在就绝不覆盖，这样「生成示例」不可能再弄丢真实配置
        provSample.Click += (_, _) =>
        {
            if (File.Exists(ProvidersPath()))
            {
                provStatusText.Text = "配置文件已经存在，没有动它。要换接口请直接编辑那个文件。";
                return;
            }

            var seed = new List<ProviderConfig> { ProviderPresets.All[1].Build() };
            seed[0].Id = ProviderRegistry.NewId();
            seed[0].Enabled = false;
            provStatusText.Text = WriteProvidersFile(seed, out var err)
                ? "已生成示例配置（未启用）。请把 New-Api-User 等字段填成你自己的值，再把 Enabled 改成 true。"
                : "生成失败：" + err;
            RenderProviderStatus();
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
            _serverLedgers.Clear();
            _coveredUntil.Clear();
            _states = new MultiProviderState();
            SaveStates();
            Status("已清空缓存（含云端水位线），正在从头重扫…（第一次会慢）");
            _ = RefreshAsync("手动完整重扫");
        };

        refreshButton.Click += (_, _) =>
        {
            CommitRoots(rootsBox);
            CommitCurrency(currencyBox);
            CommitPer(perBox);
            CommitPriority(priorityBox);
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

    /// <summary>接口配置文件：插件目录下的一个普通 JSON 数组。</summary>
    private string ProvidersPath() => Path.Combine(PluginDirectory, "providers.json");

    /// <summary>
    /// 读接口配置，**文件是唯一真相**。
    /// 它以前住在宿主设置里，配一套按 id 关联的设置页表单，而那套表单出过事故
    /// （一点即删、套预设会整份覆盖），把真实配置弄没过一次。搬成纯文本之后
    /// 可手改、可备份、可 diff，而且设置页再也没有能力覆盖它。
    /// 读不通时沿用上一次成功的副本且绝不写回 —— 坏文件不该把好配置盖掉。
    /// </summary>
    private List<ProviderConfig> LoadProviders()
    {
        var path = ProvidersPath();
        try
        {
            if (File.Exists(path))
            {
                _providersStamp = new FileInfo(path).LastWriteTimeUtc;
                var text = File.ReadAllText(path);
                var reg = ProviderRegistry.Parse(text);
                if (reg is null)
                {
                    Log.Warn("providers.json 读不通（JSON 损坏或结构不认识）：本轮沿用上一次成功读到的配置，不会写回文件");
                    return _providers;
                }
                _providers = reg.Items;

                // 旧版本写出来的是单行、中文还被转义成 \uXXXX，人根本没法读。
                // 解析成功就顺手规范化一次：内容不变，只是重新序列化。
                if (!text.Contains('\n'))
                {
                    WriteProvidersFile(reg.Items, out _);
                    Log.Info("providers.json 已重新格式化为缩进可读形式");
                }
                return _providers;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读 providers.json 失败，沿用上一次成功读到的配置：{ex.Message}");
            return _providers;
        }

        // 文件还不存在：把宿主设置里的旧配置迁出来一次，此后设置里那份不再参与
        var legacy = ProviderRegistry.Parse(Settings.Get("provider", string.Empty));
        if (legacy is { Items.Count: > 0 })
        {
            _providers = legacy.Items;
            if (WriteProvidersFile(_providers, out _))
                Log.Info($"接口配置已从宿主设置迁移到 {path}（此后以文件为准）");
            return _providers;
        }

        if (_providers.Count > 0) return _providers;

        _providers = new List<ProviderConfig> { new() { Id = ProviderRegistry.NewId() } };
        WriteProvidersFile(_providers, out _);
        return _providers;
    }

    private bool WriteProvidersFile(List<ProviderConfig> list, out string error)
    {
        error = string.Empty;
        try
        {
            var path = ProvidersPath();
            File.WriteAllText(path, new ProviderRegistry { Items = list }.ToJson());
            _providersStamp = new FileInfo(path).LastWriteTimeUtc;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warn($"写 providers.json 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>配置文件有没有被外部改过。心跳里查一次，改了就走一轮重载 ——
    /// 用户手改保存之后不用点任何按钮也会生效。</summary>
    private bool ProvidersFileChanged()
    {
        try
        {
            var path = ProvidersPath();
            return File.Exists(path) && new FileInfo(path).LastWriteTimeUtc != _providersStamp;
        }
        catch (Exception) { return false; }
    }

    private static string LabelOf(ProviderConfig p)
        => string.IsNullOrWhiteSpace(p.DisplayName) ? p.Id : p.DisplayName;

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
