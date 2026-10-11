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

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource _cts = new();
    private bool _stopped;
    private bool _busy;
    private int _secondsSinceRefresh;

    /// <summary>把最新结果回推给还开着的设置页。</summary>
    public event Action<Snapshot?>? SnapshotApplied;

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
        _scanner = new UsageScanner(cacheDir);

        _view = new TokenMeterView(Manifest, Theme);
        _view.SetScanning(true);

        _content = CreateContent();

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            Manifest.Id, Manifest.Name, Manifest.IconGlyph ?? "\uE713", BuildSettingsPage, order: 100));

        Context.Register(Context.OnSettingsChanged("enabled", OnEnabledChanged));
        Context.Register(Context.OnSettingsChanged("priority", OnPriorityChanged));
        Context.Register(Context.OnSettingsChanged("pricing", OnPricingChanged));
        Context.Register(Context.OnSettingsChanged("roots", () => _ = RefreshAsync("数据目录变更")));
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
            try
            {
                snap = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var ledger = _scanner.Scan(roots, token);
                    // 停用/卸载中不再落盘，避免把缓存目录重新建回来
                    if (!_stopped) _scanner.SaveCache();
                    lock (_ledgerLock) { _lastLedger = ledger; }
                    return SnapshotBuilder.Build(ledger, pricing);
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
            _gate.Release();
        }
    }

    private readonly object _ledgerLock = new();

    /// <summary>单价/目录变了但没重扫：直接用已有账本重算快照（很便宜，不必读盘）。</summary>
    private void RecomputeFromLedger(string reason)
    {
        var pricing = PricingConfig.Parse(Settings.Get("pricing", string.Empty));
        Snapshot snap;
        lock (_ledgerLock)
        {
            snap = _lastLedger is null
                ? new Snapshot { Currency = pricing.Currency, Error = "还没有扫描结果" }
                : SnapshotBuilder.Build(_lastLedger, pricing);
        }
        Log.Info($"重算快照（{reason}）：今日 {Format.Amount(snap.Today.Cost)}{snap.Currency}");
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
            Size = new Windows.Foundation.Size(616, 476),   // 880×680 的 70%；内容已按同一系数缩排版
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
    /// 诊断文本全部集中在设置页：岛面和聚光卡只留数据，不再挂提示行。
    /// （本来卡片顶部/底部那两行诊断文字也搬到这里了）
    /// </summary>
    private string HintLine(Snapshot s)
    {
        var pricing = PricingConfig.Parse(Settings.Get("pricing", string.Empty));
        var parts = new List<string>
        {
            $"更新于 {Format.Ago(s.GeneratedUtc)}（{Format.Clock(s.GeneratedUtc)}）",
            $"数据源 {s.RootDescription}",
            $"{s.Files} 个会话文件、{s.LedgerDays} 天记录，本轮新读 {Format.Bytes(s.ScannedBytes)}",
            $"单价基数：每 {Format.Tokens(pricing.PerTokens)} token 一个价，货币 {pricing.Currency}",
        };

        if (s.Today.UnpricedModels > 0)
            parts.Add($"今日 {s.Today.UnpricedModels} 个模型未定价，费用可能偏低");
        if (s.Today.C.Total > 0)
            parts.Add($"今日缓存命中 {Format.Percent(s.Today.C.CacheHitRate)}");
        if (s.UnpricedNames.Count > 0)
            parts.Add($"这些模型还没配单价（费用被算作 0）：{string.Join("、", s.UnpricedNames)}");
        else if (s.HasData)
            parts.Add("全部模型都已配单价。");
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
            Text = "费用是按你填的单价算出来的：会话记录里的 cost 全是 0，只有 token 数是真实的。没配单价的模型会被标成「未定价」。",
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
            PlaceholderText = "例如 SCNet-Max",
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
            Status("已清空缓存，正在从头重扫全部文件…（第一次会慢）");
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

        var editors = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        var editorRow1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        editorRow1.Children.Add(input);
        editorRow1.Children.Add(output);
        var editorRow2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        editorRow2.Children.Add(cacheRead);
        editorRow2.Children.Add(cacheWrite);
        editors.Children.Add(editorRow1);
        editors.Children.Add(editorRow2);

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
