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

    /// <summary>余额/汇总型接口的最近读数，键 = ProviderConfig.Id。
    /// 单独一份而不是塞进 <see cref="_serverLedgers"/>：余额不是某一天的用量，
    /// 进了账本就会在柱状图上长出一根假巨柱，并且每天被重复计入。</summary>
    private readonly Dictionary<string, (decimal Amount, DateTime AtUtc)> _balances = new(StringComparer.Ordinal);

    private MultiProviderState _states = new();
    private string _cacheDir = string.Empty;

    /// <summary>当前生效的接口列表（来自 providers.json）与读到时文件的修改时刻。</summary>
    private List<ProviderConfig> _providers = new();
    private DateTime _providersStamp = DateTime.MinValue;

    /// <summary>近 7 天图的画法。缓化成字段：ComposeSnapshot 在后台线程跑，
    /// 而后台线程读宿主设置是这类插件出 COMException 的经典位置，所以只在调用线程读一次。</summary>
    private string _chartMode = ChartLayout.Grouped;

    /// <summary>本地日志的模型范围与今日预算，同上：只在调用线程读，ComposeSnapshot 用缓存值。</summary>
    private string _localScopeText = string.Empty;
    private double _localBudget;

    /// <summary>「清空缓存并完整重扫」的请求标记。真正的清空在 RefreshAsync 里做 ——
    /// 那里持有 _gate；在 UI 线程上直接 clear 会和正在跑的后台拉取撞同一批字典。</summary>
    private int _rescanRequested;

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
        Context.Register(Context.OnSettingsChanged("localScope", () => _ = RecomputeAsync("模型归属变更")));
        Context.Register(Context.OnSettingsChanged("localBudget", () => _ = RecomputeAsync("本地预算变更")));
        Context.Register(Context.OnSettingsChanged("chartMode", () => _ = RecomputeAsync("图表画法变更")));
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
        // 复用的 HttpClient 在这里一次性释放（以前每轮新建，靠 using 顺手关掉）
        foreach (var client in _clients.Values)
        {
            try { client.Dispose(); } catch (Exception) { }
        }
        _clients.Clear();
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
        // 心跳是宿主直接调的：这里漏一个异常出去，累计几次就会被宿主自动停用（技能 §15 的硬规则）。
        // 心跳本身没有必须让调用方知道的失败，所以整段兜住、只写日志。
        try
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
        catch (Exception ex)
        {
            Log.Warn($"心跳出错（已忽略，下一轮继续）：{ex.Message}");
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
            _localScopeText = Settings.Get("localScope", string.Empty);
            _localBudget = ReadAmountSetting("localBudget");
            _chartMode = ChartLayout.NormalizeMode(Settings.Get("chartMode", ChartLayout.Grouped));
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

                    // 「完整重扫」是在这里落地的，不是在按钮处理器里 —— 此刻 _gate 已经拿着，
                    // 后台拉取不可能同时改这些字典。在 UI 线程上直接 clear 会撞车：
                    // Dictionary 并发写不抛异常也不一定报错，可能只是把内部桶结构弄坏，
                    // 之后某次枚举无限循环或读到半截数据，那种失败比崩掉更难查。
                    if (Interlocked.Exchange(ref _rescanRequested, 0) == 1)
                    {
                        // 只清本地：云端账本与水位线是从接口取回来的，重读本地文件跟它们无关。
                        // 以前连它们一起清，而重扫后只按 InitialBackfillDays 往回追，
                        // 于是比这个窗口更早的云端天就永远回不来了（接口那边数据其实还在）。
                        lock (_ledgerLock) { _lastLedger = null; }
                        _scanner.ForgetCache();
                        _lastAlertNote = string.Empty;
                        Log.Info("已清空本地扫描缓存，开始从头重扫（云端账本与水位线保留）");
                    }

                    // 本地日志始终先扫：它既是默认数据源，也是接口挂掉时的兜底
                    var local = _scanner.Scan(roots, token);
                    if (!_stopped) _scanner.SaveCache();

                    var active = providers.Where(p => p.Enabled).ToList();
                    if (active.Count == 0)
                    {
                        // 用户把所有接口都关了：不能拿旧的服务端数据去压制本地日志
                        _serverLedgers.Clear();
                        _coveredUntil.Clear();
                        _balances.Clear();
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
        var splitNote = string.Empty;
        var splitWarning = string.Empty;
        var undercount = false;
        var undercountDetail = string.Empty;

        // 各来源的块先按接口 id 收着，最后再按配置文件里的书写顺序排（见 SnapshotBuilder.OrderBlocks）
        var byId = new Dictionary<string, SourceBlock>(StringComparer.Ordinal);
        SourceBlock localBlock;

        // 只有「启用了且真的拿到数据」的接口才参与认领：空账本参与的话，
        // 一个还没拉通的接口会把模型从本地剔走，让数字凭空消失。
        // 余额型接口不在这里：它不认领模型、不产生账本，走下面单独的一行。
        var live = providers
            .Where(p => p.Enabled && !p.IsBalance && _serverLedgers.ContainsKey(p.Id) && _serverLedgers[p.Id].Days.Count > 0)
            .ToList();
        var balances = providers
            .Where(p => p.Enabled && p.IsBalance && _balances.ContainsKey(p.Id))
            .ToList();

        if (live.Count > 0)
        {
            var tuples = new List<(string, string, UsageLedger, ModelScope, DateTime?, int)>(live.Count);
            foreach (var p in live)
                tuples.Add((p.Id, p.DisplayName, _serverLedgers[p.Id],
                    ModelScope.Parse(p.ModelScope), _coveredUntil.GetValueOrDefault(p.Id), p.UndercountWarnPercent));

            var (localPart, assigned, split) = LedgerSplitter.SplitMany(local, tuples,
                ModelScope.Parse(_localScopeText));

            localBlock = SnapshotBuilder.BuildBlock(localPart, pricing, "本地日志");

            for (var i = 0; i < live.Count; i++)
            {
                // 每个接口用自己的币种；没报回金额的模型仍回退到本地单价估算
                var serverPricing = PricingConfig.Parse(pricing.ToJson());
                serverPricing.Currency = string.IsNullOrWhiteSpace(live[i].Currency) ? "$" : live[i].Currency.Trim();

                var sb = SnapshotBuilder.BuildBlock(assigned[i].Ledger, serverPricing, live[i].DisplayName);
                (sb.AlertTag, sb.AlertDetail) = SourceAlert.ForBudget(
                    sb.Today.Cost, live[i].DailyBudget, live[i].DisplayName, sb.Currency);
                byId[live[i].Id] = sb;
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
        }
        else
        {
            localBlock = SnapshotBuilder.BuildBlock(local, pricing, "本地日志");
        }

        // 本地块的预算走宿主设置，和 roots / localScope 同类：本地侧的旋钮不进 providers.json，
        // 那个文件只管云端接口。值由调用线程读进 _localBudget —— ComposeSnapshot 在后台线程跑，
        // 后台线程读宿主设置是这类插件出 COMException 的经典位置。
        (localBlock.AlertTag, localBlock.AlertDetail) = SourceAlert.ForBudget(
            localBlock.Today.Cost, _localBudget, "本地日志", localBlock.Currency);

        foreach (var p in balances)
        {
            var (amount, atUtc) = _balances[p.Id];
            var balanceCurrency = string.IsNullOrWhiteSpace(p.Currency) ? "$" : p.Currency.Trim();
            var block = new SourceBlock
            {
                Name = p.DisplayName,
                Currency = balanceCurrency,
                HasData = true,
                SummaryValue = amount,
                SummaryAtUtc = atUtc,
            };
            (block.AlertTag, block.AlertDetail) = SourceAlert.ForBalance(
                amount, p.BalanceWarnBelow, p.DisplayName, balanceCurrency);
            byId[p.Id] = block;
        }

        // 显示顺序 = providers.json 里的书写顺序（本地块恒为第一位）：
        // 岛面只够几行，谁被折掉就是这条顺序决定的，写在前面就排前面
        var blocks = SnapshotBuilder.OrderBlocks(
            providers.Select(p => p.Id).ToList(), localBlock, byId);

        // 阈值告警的完整句子并进 ProviderNote —— 那是状态行与聚光卡都在读的现成通道。
        // 岛面那一行只放短标签：句子在 430 宽的岛上放不下，而且会挤掉数字。
        var alerts = blocks
            .Select(b => b.AlertDetail)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
        if (alerts.Count > 0)
            note = string.IsNullOrEmpty(note) ? string.Join("；", alerts) : note + "；" + string.Join("；", alerts);
        MaybeLogAlerts(alerts);

        // Blocks[0] 恒为本地块，主块从这里挑。余额块不当选：它没有「今日/本周」的概念，
        // 让它当主块会让岛面标题显示成一个余额数字。
        var primary = blocks[0].HasData
            ? blocks[0]
            : blocks.FirstOrDefault(b => !b.IsSummary && b.HasData) ?? blocks[0];

        var modeParts = new List<string> { "本地日志" };
        if (live.Count > 0) modeParts.Add(live.Count == 1 ? "云端用量（分列）" : $"{live.Count} 个云端用量（分列）");
        if (balances.Count > 0) modeParts.Add($"{balances.Count} 个余额读值");

        var snap = new Snapshot
        {
            Currency = primary.Currency,
            RootDescription = local.RootDescription,
            Files = local.Files,
            ScannedBytes = local.ScannedBytes,
            Blocks = blocks,
            SplitNote = splitNote,
            SourceMode = string.Join(" ＋ ", modeParts),
            ProviderSummary = live.Count + balances.Count > 0
                ? string.Join(" ｜ ", live.Concat(balances).Select(p => p.SafeSummary()))
                : (providers.Any(p => p.Enabled) ? "已启用的接口本轮没有可用数据" : "服务端接口未启用"),
            CoveredUntilUtc = CoveredFloor(live),
            ProviderNote = note,
            ChartMode = _chartMode,
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
    ///
    /// 余额型接口走另一条路：它没有「补历史」的概念，一次请求就一个数。
    /// </summary>
    private async Task PullProviderAsync(ProviderConfig cfg, CancellationToken ct)
    {
        if (cfg.IsBalance)
        {
            await PullBalanceAsync(cfg, ct).ConfigureAwait(false);
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var st = _states.For(cfg.Id);
        var plan = ProviderClient.PlanWindows(_coveredUntil.GetValueOrDefault(cfg.Id), nowUtc, cfg);
        var merged = _serverLedgers.TryGetValue(cfg.Id, out var prev) ? prev.Clone() : new UsageLedger();
        var windows = 0;
        var pages = 0;
        long bytes = 0;

        var client = ClientFor(cfg.TimeoutSeconds);
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

    /// <summary>
    /// 读一次余额/汇总型接口。只存「最近一个读数 + 时刻」，不进按天账本、不进近 7 天图。
    /// 失败时保留上一次读数：上一刻的余额仍然是比「没有余额」更好的信息，
    /// 但界面会带上它是几点看的，不会让人误当成实时值。
    /// </summary>
    private async Task PullBalanceAsync(ProviderConfig cfg, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var st = _states.For(cfg.Id);

        var client = ClientFor(cfg.TimeoutSeconds);
        var r = await client.FetchBalanceAsync(cfg, ct).ConfigureAwait(false);

        _balances[cfg.Id] = (r.Amount, nowUtc);
        st.LatestBalance = (double)r.Amount;
        st.BalanceAtUtc = nowUtc;
        st.LastSuccessUtc = nowUtc;
        st.LastAttemptUtc = nowUtc;
        st.LastError = null;
        SaveStates();

        var cur = string.IsNullOrWhiteSpace(cfg.Currency) ? "$" : cfg.Currency.Trim();
        Log.Info($"接口余额读取完成：{Format.Money(cur, r.Amount)}"
               + $"（接口原值 {r.RawValue.ToString(CultureInfo.InvariantCulture)}，"
               + $"÷{cfg.CostUnitDivisor.ToString(CultureInfo.InvariantCulture)} ×{cfg.CostScale.ToString(CultureInfo.InvariantCulture)}）"
               + $"，{Format.Bytes(r.Bytes)}");
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

    /// <summary>读一个金额型设置。填错、留空、填 0 或负数一律当"不启用" ——
    /// 一个手抖打错的预算线不该变成"任何用量都超预算"。
    /// ⚠ 只能在调用线程（UI）上调，结果缓存进字段；不要在 ComposeSnapshot 里直接调它。</summary>
    private double ReadAmountSetting(string key)
    {
        var raw = (Settings.Get(key, string.Empty) ?? string.Empty).Trim();
        foreach (var sym in new[] { "¥", "￥", "$", "％", "%" }) raw = raw.Replace(sym, string.Empty);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0
             && !double.IsNaN(v) && !double.IsInfinity(v)
            ? v : 0;
    }

    private string _lastAlertNote = string.Empty;

    /// <summary>阈值告警只有一条：内容变了才记，全部解除时补一条恢复。
    /// 预算和余额都是"每分钟看一次"的指标，不设这个闸就会像当初的 401 那样把日志埋掉。</summary>
    private void MaybeLogAlerts(List<string> alerts)
    {
        var joined = string.Join("；", alerts);
        if (string.Equals(joined, _lastAlertNote, StringComparison.Ordinal)) return;

        if (alerts.Count > 0) Log.Warn("阈值告警：" + joined + "（只是提示，不改任何数字）");
        else if (_lastAlertNote.Length > 0) Log.Info("阈值告警已解除，上一轮：" + _lastAlertNote);

        _lastAlertNote = joined;
    }

    /// <summary>
    /// 取宿主主题里的样式资源。取不到就返回 null，让调用方退回自己设字号/字重 ——
    /// 这些资源名是宿主内部的，不是 SDK 承诺的契约：以前这里是硬转换，
    /// 而 SettingsCardStyle 那处几乎每一行设置都要用，宿主哪天改名或换主题字典，
    /// 整个设置页就会在构建时抛异常打不开（插件最显眼的那半边直接没了）。
    /// </summary>
    private static Style? NamedStyle(string key)
    {
        try
        {
            return Application.Current.Resources.TryGetValue(key, out var res) ? res as Style : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 按超时值复用接口客户端。HttpClient 每次刷新都新建会累积 TIME_WAIT 连接，
    /// 而它的超时是**每个实例**固定的，所以按超时秒数分档缓存，正好也保住"改了配置就换档"。
    /// 只在持有 _gate 时调用，不需要额外加锁；停用时统一释放。
    /// </summary>
    private readonly Dictionary<int, ProviderClient> _clients = new();

    private ProviderClient ClientFor(int timeoutSeconds)
    {
        var key = Math.Clamp(timeoutSeconds, 2, 120);
        if (!_clients.TryGetValue(key, out var client))
        {
            client = new ProviderClient(key);
            _clients[key] = client;
        }
        return client;
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
                    // v1 装的是「按天账本 + 水位线」，只能挂到用量型接口名下：
                    // 挂给余额型接口的话这笔历史永远不会被任何代码读到
                    var target = providers.FirstOrDefault(p => !p.IsBalance) ?? providers[0];
                    fresh.AdoptLegacy(target.Id, legacy);
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
        _balances.Clear();

        foreach (var p in providers)
        {
            var st = _states.For(p.Id);
            _coveredUntil[p.Id] = st.CoveredUntilUtc;
            if (st.HasPersistedLedger) _serverLedgers[p.Id] = st.LoadLedger();

            // 缓存文件是外部输入：写坏过一次的数字（NaN、1e30）不能把 double→decimal 炸成异常，
            // 那会让整个恢复流程挂掉、界面上一次读数全丢
            if (st.LatestBalance is {} bal && st.BalanceAtUtc is not null && bal >= -1e15 && bal <= 1e15)
                _balances[p.Id] = ((decimal)bal, st.BalanceAtUtc.Value);
        }
    }

    private void SaveStates()
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            // 必须 tmp + Move 原子替换：状态文件记着每个接口的云端账本和水位线，
            // 写一半被打断（断电、宿主被强杀、并发写）会留下半个 JSON，
            // 下次启动读不回来就等于把云端历史整份丢掉 —— 而用户只会看到"云端块是空的"。
            var tmp = StatePathV2() + ".tmp";
            File.WriteAllText(tmp, _states.ToJson());
            File.Move(tmp, StatePathV2(), overwrite: true);
        }
        catch (Exception)
        {
            // 状态写不下去只影响下次从哪里续拉，不影响本轮显示
        }
    }

    /// <summary>
    /// 单价/目录/预算/画法变了但没重扫：直接用已有账本重算快照（很便宜，不必读盘）。
    ///
    /// 必须和 RefreshAsync 共用同一把闸门：设置回调在 UI 线程上，而后台那一轮正在写
    /// _serverLedgers / _balances / _states。两个线程同时动一个 Dictionary，最坏情况不是抛异常
    /// 而是把内部桶结构弄坏，之后某次枚举读到半截数据或转不出来 —— 那种故障比崩掉难查得多。
    /// 宿主设置也只在调用线程读一次，等锁之后不再碰（后台线程读设置是这类插件出 COMException 的经典位置）。
    /// </summary>
    private async Task RecomputeAsync(string reason)
    {
        _localScopeText = Settings.Get("localScope", string.Empty);
        _localBudget = ReadAmountSetting("localBudget");
        _chartMode = ChartLayout.NormalizeMode(Settings.Get("chartMode", ChartLayout.Grouped));

        var pricing = PricingConfig.Parse(Settings.Get("pricing", string.Empty));
        var providers = LoadProviders();
        UsageLedger? local;
        lock (_ledgerLock) { local = _lastLedger; }

        if (local is null)
        {
            RunOnUI(() => Apply(new Snapshot { Currency = pricing.Currency, Error = "还没有扫描结果", ChartMode = _chartMode }));
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        if (_stopped)
        {
            _gate.Release();
            return;
        }

        try
        {
            var snap = ComposeSnapshot(local, pricing, providers, string.Empty);
            Log.Info($"重算快照（{reason}）：今日 {Format.Amount(snap.Today.Cost)}{snap.Currency}，{snap.Blocks.Count} 个来源");
            RunOnUI(() => Apply(snap));
        }
        finally
        {
            _gate.Release();
        }
    }

    private Snapshot BuildFromLedgerLocked(PricingConfig pricing, string error)
    {
        lock (_ledgerLock)
        {
            var snap = _lastLedger is null
                ? new Snapshot { Currency = pricing.Currency, Error = error }
                : SnapshotBuilder.Build(_lastLedger, pricing);
            if (string.IsNullOrEmpty(snap.Error)) snap.Error = error;
            snap.ChartMode = _chartMode;
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

    private void OnPricingChanged() => _ = RecomputeAsync("单价变更");

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

        var titleStyle = NamedStyle("TitleTextBlockStyle");
        var title = new TextBlock { Text = Manifest.Name, Style = titleStyle };
        // 取不到样式才自己设：本地值会盖过样式里的 setter，所以有样式时**不能**顺手赋 0
        if (titleStyle is null)
        {
            title.FontSize = 20;
            title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        }
        page.Children.Add(title);

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

        // ---- 本地日志的今日预算（云端各家的预算写在 providers.json 里）----
        var localBudgetBox = new TextBox
        {
            Header = "本地日志今日预算（用单价配置里的货币符号；留空或 0 = 不提醒）",
            Text = Settings.Get("localBudget", string.Empty),
            PlaceholderText = "例如 200",
            MinWidth = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        localBudgetBox.LostFocus += (_, _) => CommitLocalBudget(localBudgetBox);
        localBudgetBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) CommitLocalBudget(localBudgetBox);
        };
        page.Children.Add(Card(localBudgetBox));

        // ---- 近 7 天图的画法 ----
        // 顺序很关键：先把项加满、把当前选中位置写好，**最后**才挂 SelectionChanged。
        // 程序性赋值会触发这个事件（同一个坑在接口选择器上踩过一次，那次是 StackOverflow 直接杀死宿主）
        var chartBox = new ComboBox
        {
            Header = "近 7 天柱状图",
            MinWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        chartBox.Items.Add("分组（一个来源一根柱，并排）");
        chartBox.Items.Add("堆叠（一天一根柱，按来源叠色）");
        chartBox.SelectedIndex = ChartLayout.NormalizeMode(Settings.Get("chartMode", ChartLayout.Grouped))
                                 == ChartLayout.Stacked ? 1 : 0;
        chartBox.SelectionChanged += (_, _) =>
        {
            var mode = chartBox.SelectedIndex == 1 ? ChartLayout.Stacked : ChartLayout.Grouped;
            if (string.Equals(Settings.Get("chartMode", ChartLayout.Grouped), mode, StringComparison.Ordinal)) return;
            Settings.Set("chartMode", mode);
            Status($"近 7 天图已改为「{(mode == ChartLayout.Stacked ? "堆叠" : "分组")}」");
        };
        page.Children.Add(Card(chartBox));

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
                  + string.Join("；", list.Select(p => $"{LabelOf(p)}（{(p.Enabled ? "启用" : "未启用")}｜{(p.IsBalance ? "余额" : "用量")}｜{p.BaseUrl}{p.Path}）"));
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

            // 写出去的是手写的带注释模板，不是序列化结果 —— 序列化会丢注释，
            // 而这个文件存在的意义就是给人读、给人改。
            string? err = null;
            try { File.WriteAllText(ProvidersPath(), ProviderRegistry.SampleTemplate); }
            catch (Exception ex) { err = ex.Message; }

            provStatusText.Text = err is null
                ? "已生成带注释的示例配置。注意 Enabled 是 true，但地址与 New-Api-User 还是占位值，先改成你自己的再保存。"
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
            // 只立标记，不在这里 clear —— 真正的清空在 RefreshAsync 里（持有 _gate）做，
            // 否则会和正在跑的后台拉取撞同一批字典
            Interlocked.Exchange(ref _rescanRequested, 1);
            Status("已排队清空本地扫描缓存，正在从头重扫…（第一次会慢，云端账本保留）");
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

    private Border Card(UIElement child)
    {
        var style = NamedStyle("SettingsCardStyle");
        var card = new Border { Child = child, Style = style };
        // 这个样式几乎每一行设置都要用。取不到时给一点内边距让页面仍然可读，
        // 而不是在这里抛异常把整个设置页带走
        if (style is null) card.Padding = new Thickness(12, 8, 12, 8);
        return card;
    }

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

                // 迁移之后插件**再也不写这个文件**。它一旦能被人手加注释，
                // 任何自动重写都会把注释抹掉 —— 所以这里只读不写。
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

    private void CommitLocalBudget(TextBox box)
    {
        var text = (box.Text ?? string.Empty).Trim();
        if (string.Equals(text, Settings.Get("localBudget", string.Empty), StringComparison.Ordinal)) return;
        Settings.Set("localBudget", text);   // 触发 RecomputeFromLedger
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
