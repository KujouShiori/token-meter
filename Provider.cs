using System.Text.Json;

namespace TokenMeter;

/// <summary>
/// 服务商用量接口的配置。
///
/// 设计原则：**不把任何厂商的具体限制写死**。
/// 端点、参数名、分页字段、时间字段路径、字段映射、窗口上限、滞后带全部可配置，
/// 因为不同服务商、不同权限等级（组织级 / 项目级 / 个人 Key）的覆盖范围与返回结构都不一样。
/// 下面的默认值只是一个「OpenAI 风格」的起点示例，**未对照官方文档核实**，
/// 使用前必须按自己账号的实际情况改；猜错的表现是数字偏低或拉不到数据，而不是报错。
/// </summary>
public sealed class ProviderConfig
{
    /// <summary>总开关。关掉 = 只用本地日志，行为和 1.0.0 完全一致。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 接口给的是什么数：
    /// <c>usage</c>（默认）= 逐条/逐天的用量明细，进按天账本与近 7 天柱状图；
    /// <c>balance</c> = 某一刻的总额（账户余额、剩余额度），只在岛面单独占一行。
    ///
    /// 为什么要分开：总额不是流量。把余额塞进按天账本会造出一根巨大的假柱子，
    /// 而且同一条余额每天都会被再计一次。
    /// </summary>
    public string Kind { get; set; } = "usage";

    /// <summary>
    /// 余额型接口的取值字段（点号路径，支持数组下标 <c>data[0].total_balance</c>、
    /// 「字符串里包的 JSON」，以及逗号分隔多路径求和——分钱包余额就靠这个）。
    /// 只有 Kind=balance 时用到。
    /// </summary>
    public string BalanceField { get; set; } = "";

    /// <summary>这个接口是「读一个总额」型的。</summary>
    public bool IsBalance
        => string.Equals((Kind ?? string.Empty).Trim(), "balance", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 接口来源的稳定标识。云端账本与水位线按它分份，所以改 DisplayName 不会影响已有数据。
    /// 单接口时代的配置里没有这个字段，反序列化后为空，由 ProviderRegistry.EnsureIds 补。
    /// </summary>
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "自定义接口";

    /// <summary>接口根地址，例如 https://api.example.com/v1（不带末尾斜杠）。</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>用量查询路径，接在 BaseUrl 后面，例如 /organization/usage/completions。</summary>
    public string Path { get; set; } = "";

    /// <summary>鉴权头名字。多数实现用 Authorization；有的用 x-api-key。</summary>
    public string AuthHeader { get; set; } = "Authorization";

    /// <summary>鉴权值前缀，例如 "Bearer "（注意末尾空格）。留空则整个头的值就是密钥本身。</summary>
    public string AuthPrefix { get; set; } = "Bearer ";

    /// <summary>
    /// 密钥从哪个**环境变量**读。插件自己不保存密钥，因此密钥不会落进
    /// 宿主的 settings.json、不会被同步盘/备份带走，也不会进日志。
    /// </summary>
    public string ApiKeyEnvVar { get; set; } = "TOKEN_METER_API_KEY";

    // ---- 查询参数名 ----
    public string StartParam { get; set; } = "start_date";
    public string EndParam { get; set; } = "end_date";

    /// <summary>时间参数格式：date = yyyy-MM-dd；unix = 秒级时间戳；rfc3339 = 2026-10-01T00:00:00Z。</summary>
    public string TimeFormat { get; set; } = "date";

    /// <summary>额外固定查询串，原样拼上去，例如 bucket_width=1d。</summary>
    public string ExtraQuery { get; set; } = "";

    /// <summary>
    /// 额外固定请求头，多个用分号或换行分隔，形如 {@code anthropic-version=2023-06-01}。
    /// 有些接口除了鉴权头还强制要求版本头（例如 Anthropic 的 Admin API），不填会被拒。
    /// </summary>
    public string ExtraHeaders { get; set; } = "";

    // ---- 响应结构 ----
    /// <summary>条目数组在 JSON 里的位置，支持点号路径与数组展开：
    /// 扁平写法 {@code data}；两层嵌套写法 {@code data[].results}（先遍历 data 数组，再取每项的 results）。</summary>
    public string ItemsPath { get; set; } = "data";

    /// <summary>游标字段名（在响应根上）。留空表示这个接口不分页。</summary>
    public string PageTokenField { get; set; } = "next_page";

    /// <summary>下一页游标要回填到哪个查询参数上。</summary>
    public string PageParam { get; set; } = "after";

    /// <summary>是否有下一页的布尔字段名。留空 = 只看 PageTokenField 是否非空。</summary>
    public string HasMoreField { get; set; } = "has_more";

    /// <summary>分页方式：cursor = 用响应里的游标字段；
    /// page = 按页码递增（New API 这类只有 page/per_page/total、没有游标的接口）。</summary>
    public string PageMode { get; set; } = "cursor";

    /// <summary>page 模式的起始页码（有的接口从 0 开始，有的从 1 开始）。</summary>
    public int PageStart { get; set; } = 0;

    /// <summary>page 模式下每页条数的参数名与值；参数名留空则不传。</summary>
    public string PageSizeParam { get; set; } = "";

    public int PageSize { get; set; } = 0;

    /// <summary>page 模式下用于判断是否翻完的总数字段（点号路径）；
    /// 留空则只靠「本页为空」或「本页不满一页」停下来。</summary>
    public string TotalField { get; set; } = "";

    /// <summary>
    /// 每条记录的时间字段。取数时会先在条目里找，找不到再到「拥有该条目的上层对象」上找——
    /// 因为像 Anthropic 那种两层结构里，时间戳写在外层时间桶上而不是每条明细里。
    /// </summary>
    public string TimeField { get; set; } = "period.start";

    /// <summary>模型名字段；接口不按模型分组时留空，会归进 ModelFallback。</summary>
    public string ModelField { get; set; } = "model";

    public string ModelFallback { get; set; } = "服务端汇总";

    // ---- token 字段与口径 ----
    public string InputField { get; set; } = "input_tokens";
    public string OutputField { get; set; } = "output_tokens";
    public string CachedInputField { get; set; } = "input_cached_tokens";
    /// <summary>缓存写字段。支持逗号分隔的多个路径，会相加（Anthropic 就是 5 分钟 + 1 小时两档）。</summary>
    public string CacheWriteField { get; set; } = "";
    /// <summary>请求次数字段。很多用量接口不给这个值，留空则界面不显示服务端请求数。</summary>
    public string RequestsField { get; set; } = "";
    /// <summary>思考 token 字段。仅在 InputIncludesCached/独立计费的口径下才需要动。</summary>
    public string ReasoningField { get; set; } = "";

    /// <summary>这个接口上报的模型归它管（逗号分隔，* = 全部）；留空视为「上报什么就归什么」。</summary>
    public string ModelScope { get; set; } = "";

    /// <summary>云端金额的币种（New API 这类网关通常是美元），与本地 ¥ 不能相加。</summary>
    public string Currency { get; set; } = "$";

    /// <summary>每页最多要多少个时间桶；部分接口靠这个参数限制返回量。留空则不传。</summary>
    public string LimitParam { get; set; } = "";

    public int LimitPerRequest { get; set; } = 0;

    /// <summary>
    /// 关键口径开关：服务端的 InputField 是否**已经包含**缓存命中的部分。
    /// true  → 不命中缓存的输入 = InputField - CachedInputField（减错就会整段重复计费）
    /// false → 两者互斥，直接相加（Anthropic 官方就是这种：uncached_input_tokens 与 cache_read_input_tokens 分开）
    /// </summary>
    public bool InputIncludesCached { get; set; } = true;

    /// <summary>服务端返回里是否直接带金额字段；带就优先用它，省掉单价推算。</summary>
    /// <summary>服务端报回的金额字段（点号路径，支持钻进字符串里的 JSON）。</summary>
    public string CostField { get; set; } = "";

    /// <summary>金额换算率：金额字段的值 ÷ 该值 = 元/美元。
    /// New API 这类用「500000 单位 = $1」的记账法就填 500000。
    /// 用量接口作用于 CostField，余额接口作用于 BalanceField（同一个接口只会用到其中一个）。</summary>
    public double CostUnitDivisor { get; set; } = 1;

    /// <summary>所有 token 数值的统一乘数。接口返回的不是「个」而是「千/百万」时用它校正，默认 1。</summary>
    public double ValueScale { get; set; } = 1;

    /// <summary>金额字段的乘数（例如接口返回「分」时填 0.01）。默认 1。
    /// 与 CostUnitDivisor 是两回事：一个乘、一个除，两个都给是为了让
    /// 「50 万单位 = $1」和「数值本身就是分」两种写法都能表达。</summary>
    public double CostScale { get; set; } = 1;

    // ---- 拉取节奏 ----
    /// <summary>单次请求的最大时间跨度。接口对跨度有限制时调小它，分段滚动拉。</summary>
    public int WindowMaxDays { get; set; } = 30;

    /// <summary>滞后带（分钟）：晚于「现在 - 该值」的时间段不信服务端，继续用本地日志。</summary>
    public int LagMinutes { get; set; } = 120;

    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>一轮里接口阶段最多花多少秒。超过就停手、本轮先用本地日志，
    /// 避免接口卡住时把后面的手动刷新全排在队里等。</summary>
    public int ProviderBudgetSeconds { get; set; } = 45;
    public int MaxPagesPerWindow { get; set; } = 50;
    /// <summary>一轮最多拉多少个窗口，防止首次同步把 UI 卡住。</summary>
    public int MaxWindowsPerRun { get; set; } = 12;
    /// <summary>首次同步往回追多少天（没有水位线时）。</summary>
    public int InitialBackfillDays { get; set; } = 7;

    // ---- 对账告警 ----
    /// <summary>服务端总量比本地少超过这个比例时，界面标警告而不是静默覆盖。</summary>
    public int UndercountWarnPercent { get; set; } = 10;

    /// <summary>云端账本在状态文件里保留多少天，超出的整天直接丢。0 = 不清理。
    /// 默认 730：够月视图与「全部」口径用，同时给长期运行封个顶
    /// （每天×每模型一个条目，不清理的话状态文件会一直涨）。</summary>
    public int LedgerKeepDays { get; set; } = 730;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    public static ProviderConfig Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ProviderConfig();
        try
        {
            var cfg = JsonSerializer.Deserialize<ProviderConfig>(json, JsonOpts);
            if (cfg is null) return new ProviderConfig();
            cfg.BaseUrl = (cfg.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
            cfg.Kind = (cfg.Kind ?? string.Empty).Trim();
            cfg.BalanceField = (cfg.BalanceField ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(cfg.Path) && !cfg.Path.StartsWith('/')) cfg.Path = "/" + cfg.Path;
            cfg.WindowMaxDays = Math.Clamp(cfg.WindowMaxDays, 1, 366);
            cfg.LagMinutes = Math.Clamp(cfg.LagMinutes, 0, 2880);
            cfg.TimeoutSeconds = Math.Clamp(cfg.TimeoutSeconds, 2, 120);
            cfg.ProviderBudgetSeconds = Math.Clamp(cfg.ProviderBudgetSeconds, 5, 300);
            cfg.MaxPagesPerWindow = Math.Clamp(cfg.MaxPagesPerWindow, 1, 500);
            cfg.MaxWindowsPerRun = Math.Clamp(cfg.MaxWindowsPerRun, 1, 200);
            cfg.InitialBackfillDays = Math.Clamp(cfg.InitialBackfillDays, 1, 366);
            cfg.LedgerKeepDays = Math.Clamp(cfg.LedgerKeepDays, 0, 3660);
            return cfg;
        }
        catch (Exception)
        {
            return new ProviderConfig();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>配置是否已经填到能发请求的程度。</summary>
    public bool IsUsable(out string reason)
    {
        if (!Enabled) { reason = "未启用"; return false; }
        var kind = (Kind ?? string.Empty).Trim().ToLowerInvariant();
        if (kind.Length > 0 && kind != "usage" && kind != "balance")
        {
            reason = $"Kind「{Kind}」不认识，只支持 usage（按天用量）与 balance（读一个总额）";
            return false;
        }
        if (IsBalance && string.IsNullOrWhiteSpace(BalanceField))
        {
            reason = "余额型接口没填取值字段 BalanceField";
            return false;
        }
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(Path))
        {
            reason = "接口地址未填";
            return false;
        }
        if (string.IsNullOrWhiteSpace(ApiKeyEnvVar))
        {
            reason = "没指定密钥环境变量名";
            return false;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyEnvVar.Trim())))
        {
            reason = $"环境变量 {ApiKeyEnvVar.Trim()} 没有值";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>只报字段名，绝不报值。</summary>
    public string SafeSummary()
        => $"{DisplayName}｜{(IsBalance ? "余额" : "用量")}｜{BaseUrl}{Path}｜密钥来自环境变量 {ApiKeyEnvVar}｜"
         + (IsBalance
            ? $"取值 {BalanceField}"
            : $"窗口≤{WindowMaxDays}天｜滞后{LagMinutes}分钟｜输入{(InputIncludesCached ? "含" : "不含")}缓存");
}

/// <summary>一个可套用的接口预设。Basis 说清“这组值哪来的”：没核实的就写未核实，不假装成官方事实。</summary>
public sealed class ProviderPreset
{
    public string Name { get; }
    public string Basis { get; }
    private readonly Func<ProviderConfig> _build;

    public ProviderPreset(string name, string basis, Func<ProviderConfig> build)
    {
        Name = name;
        Basis = basis;
        _build = build;
    }

    /// <summary>生成一份配置；Enabled 由调用方按用户当前开关覆盖，预设不自作主张联网。</summary>
    public ProviderConfig Build() => _build();
}

public static class ProviderPresets
{
    public static readonly ProviderPreset[] All =
    {
        new("Anthropic 用量接口", "已对官方文档核实（platform.claude.com）", () => new ProviderConfig
        {
            DisplayName = "Anthropic 组织用量",
            BaseUrl = "https://api.anthropic.com",
            Path = "/v1/organizations/usage_report/messages",
            AuthHeader = "x-api-key",
            AuthPrefix = "",
            ApiKeyEnvVar = "ANTHROPIC_ADMIN_KEY",
            ExtraHeaders = "anthropic-version=2023-06-01",
            StartParam = "starting_at",
            EndParam = "ending_at",
            TimeFormat = "rfc3339",
            ExtraQuery = "bucket_width=1d&group_by[]=model",
            ItemsPath = "data[].results",
            TimeField = "starting_at",
            ModelField = "model",
            ModelFallback = "未分组",
            InputField = "uncached_input_tokens",
            OutputField = "output_tokens",
            CachedInputField = "cache_read_input_tokens",
            CacheWriteField = "cache_creation.ephemeral_5m_input_tokens,cache_creation.ephemeral_1h_input_tokens",
            InputIncludesCached = false,
            PageTokenField = "next_page",
            PageParam = "page",
            HasMoreField = "has_more",
            LimitParam = "limit",
            LimitPerRequest = 31,
            WindowMaxDays = 31,
            LagMinutes = 10,          // 官方称数据通常 5 分钟内到位，建议 1 次/分钟轮询
            InitialBackfillDays = 7,
        }),

        new("New API / One API 网关（用量日志）", "按 ps.air-outer.com 实测响应结构核实", () => new ProviderConfig
        {
            DisplayName = "New API 用量日志",
            BaseUrl = "https://ps.air-outer.com",
            Path = "/api/log/self",
            // New API 的控制台接口用裸令牌，不带 Bearer 前缀
            AuthHeader = "Authorization",
            AuthPrefix = "",
            ApiKeyEnvVar = "TOKEN_METER_API_KEY",
            // 必须把 CHANGE_ME 换成你的用户 ID（控制台右上角头像旁能看到）
            ExtraHeaders = "New-Api-User=CHANGE_ME",
            StartParam = "start_timestamp",
            EndParam = "end_timestamp",
            TimeFormat = "unix",
            ExtraQuery = "type=2",            // type=2 = 消耗日志
            ItemsPath = "data.items",
            TimeField = "created_at",
            ModelField = "model_name",
            ModelFallback = "未分组",
            InputField = "prompt_tokens",
            OutputField = "completion_tokens",
            // other 是「字符串里包的 JSON」，靠 TryNavigateDeep 钻进去
            CachedInputField = "other.cache_tokens",
            CacheWriteField = "other.cache_creation_tokens",
            // New API 惯例：prompt_tokens 已包含缓存命中部分，cache_tokens 是其子集。
            // ⚠ 这条在你的账号上还没跑到 cache_tokens>0 的样本，接上后请对一次账单再确认
            InputIncludesCached = true,
            CostField = "quota",
            CostUnitDivisor = 500000,          // New API 记账：500000 单位 = $1
            PageMode = "page",
            PageStart = 0,
            PageParam = "page",
            PageSizeParam = "per_page",
            PageSize = 50,
            TotalField = "data.total",
            WindowMaxDays = 1,                 // 日志是逐条的，按天分段更好翻页
            LagMinutes = 10,
            InitialBackfillDays = 7,
        }),

        new("OpenAI 兼容用量接口（通用模板）", "结构占位模板，字段名未核实，接前自己改", () => new ProviderConfig
        {
            DisplayName = "OpenAI 兼容用量",
            BaseUrl = "",
            Path = "/organization/usage/completions",
            AuthHeader = "Authorization",
            AuthPrefix = "Bearer ",
            ApiKeyEnvVar = "TOKEN_METER_API_KEY",
            StartParam = "start_date",
            EndParam = "end_date",
            TimeFormat = "date",
            ExtraQuery = "bucket_width=1d",
            ItemsPath = "data",
            TimeField = "period.start",
            ModelField = "model",
            InputField = "input_tokens",
            OutputField = "output_tokens",
            CachedInputField = "input_cached_tokens",
            InputIncludesCached = true,
            PageTokenField = "next_page",
            PageParam = "after",
            HasMoreField = "has_more",
            WindowMaxDays = 30,
            LagMinutes = 120,
        }),
    };
}

/// <summary>同步水位线等跨进程状态，存在插件目录 .cache 里，随卸载一起删除。</summary>
public sealed class ProviderState
{
    /// <summary>已经拿到服务端数据的最后一个时刻（UTC）。null = 还没成功同步过。</summary>
    public DateTime? CoveredUntilUtc { get; set; }

    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public string? LastError { get; set; }

    /// <summary>累计从服务端拿到的 token 数，仅用于日志与状态显示。</summary>
    public long ServerTotalTokens { get; set; }

    /// <summary>
    /// 云端账本本体（天 → 模型 → 计数）。**必须和水位线一起持久化**：
    /// 只存水位线不存数据的话，重启后水位线会说「这段时间已经拉过了」，
    /// 而那段时间的数据其实全丢了——云端块会永久归零。
    /// </summary>
    public Dictionary<string, Dictionary<string, long[]>> ServerDays { get; set; } = new();

    /// <summary>
    /// 余额/汇总型接口的最近一次读数（已按 CostUnitDivisor / CostScale 换算成币种单位）。
    /// null = 还没成功读到过。
    ///
    /// ⚠ 它**不进 ServerDays**：余额是「某一刻账户里还剩多少」，不是某一天用了多少。
    /// 混进按天账本会造出一根巨大的假柱子，并且同一条读数每天被再计一次。
    /// </summary>
    public double? LatestBalance { get; set; }

    /// <summary>读数时刻（UTC）。余额没有「覆盖到哪一天」的概念，只有「这是几点看的」。</summary>
    public DateTime? BalanceAtUtc { get; set; }

    public void SaveLedger(UsageLedger? ledger, int keepDays = 0)
    {
        var map = new Dictionary<string, Dictionary<string, long[]>>(StringComparer.Ordinal);
        if (ledger is not null)
        {
            foreach (var (day, byModel) in ledger.Days)
            {
                var m = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var (model, c) in byModel) m[model] = c.ToArray();
                map[day] = m;
            }
        }

        if (keepDays > 0)
        {
            // 键就是 yyyy-MM-dd，序数比字符串等于比日期，不用解析
            var cutoff = DateOnly.FromDateTime(DateTime.Now).AddDays(-keepDays).ToString("yyyy-MM-dd");
            foreach (var stale in map.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList())
                map.Remove(stale);
        }

        ServerDays = map;
    }

    public UsageLedger LoadLedger()
    {
        var l = new UsageLedger { RootDescription = "云端接口（已持久化）" };
        if (ServerDays is null) return l;
        foreach (var (day, byModel) in ServerDays)
        {
            foreach (var (model, arr) in byModel) l.Add(day, model, Counters.FromArray(arr));
        }
        return l;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasPersistedLedger => ServerDays is { Count: > 0 };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static ProviderState Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ProviderState();
        try { return JsonSerializer.Deserialize<ProviderState>(json, JsonOpts) ?? new ProviderState(); }
        catch (Exception) { return new ProviderState(); }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
}

/// <summary>
/// 接口来源列表，存在宿主设置的 provider 键里。
///
/// 读取兼容三种形态：单个对象（1.1.0 及更早）、数组（多接口时代）、空值。
/// **解析失败返回 null 而不是默认配置**：保存是整键覆盖写回，
/// 一旦把默认值当解析结果写回去，用户的真实配置就被抹掉了。
/// </summary>
public sealed class ProviderRegistry
{
    public List<ProviderConfig> Items { get; set; } = new();

    public static ProviderRegistry? Parse(string? json)
    {
        var raw = (json ?? string.Empty).Trim();
        if (raw.Length == 0) return new ProviderRegistry();

        try
        {
            // 不能靠首字符判断形态：带注释的配置文件开头可能就是 // 而不是 [。
            // 先按 JSONC 解析一次，再照真正的根类型分派。
            using var probe = JsonDocument.Parse(raw, JsonDocOpts);
            switch (probe.RootElement.ValueKind)
            {
                case JsonValueKind.Array:
                {
                    var list = JsonSerializer.Deserialize<List<ProviderConfig>>(raw, JsonOpts);
                    if (list is null) return null;
                    var reg = new ProviderRegistry { Items = list };
                    reg.EnsureIds();
                    return reg;
                }
                case JsonValueKind.Object:
                {
                    var one = JsonSerializer.Deserialize<ProviderConfig>(raw, JsonOpts);
                    if (one is null) return null;
                    var reg = new ProviderRegistry { FromLegacyObject = true };
                    reg.Items.Add(one);
                    reg.EnsureIds();
                    return reg;
                }
                default: return null;   // 数字、字符串、null：都不是接口配置
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static readonly JsonDocumentOptions JsonDocOpts = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>给缺 id 的条目补一个，并把重复 id 改掉——同 id 会让两份账本互相覆盖。</summary>
    /// <summary>单接口时代那份配置迁移过来时固定用这个 id，保证跨启动稳定。</summary>
    public const string LegacyId = "legacy-primary";

    /// <summary>本次是不是从「单个对象」读出来的（也就是老配置）。</summary>
    public bool FromLegacyObject { get; set; }

    /// <summary>给缺 id 的条目补一个。必须**确定性**生成：随机 id 会让每次启动换一把钥匙，
    /// 已经积累的云端账本就永远对不上键，等于每次重启丢一次云端历史。
    /// 只有撞车的条目才退回随机串 —— 那时身份通常已经落盘，不再依赖可推导性。</summary>
    public void EnsureIds()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(Items[i].Id)) continue;
            Items[i].Id = FromLegacyObject && i == 0 ? LegacyId : $"p{i}";
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Items)
        {
            while (!seen.Add(c.Id)) c.Id = NewId();
        }
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// 「生成示例配置」写出的模板：手写、带注释，让第一次用的人不必翻文档就知道每个字段干什么。
    /// 第一份对象（用量型）把 ProviderConfig 的**全部可写属性**都列出来了，第二份（余额型）只留用得上的——
    /// 有断言按「两份合起来覆盖所有可写属性」检查，加新字段时不补注释行就会红。
    /// </summary>
    public const string SampleTemplate = """
// providers.json —— 云端用量接口配置
// 这个文件按 JSONC 读：允许 // 注释和尾逗号。数组里想接几个接口就写几个 { }。
// 数组里的顺序就是岛面与聚光卡里的显示顺序：岛面高度只够几行，写在前面的先出现、排在后面的会被折起来。
// 保存即生效（插件每秒检查一次文件有没有变），不用重启宿主。
// 插件只在「文件还不存在」时写它一次，之后只读不写，所以你加的注释会留着。
//
// 密钥不要写在这里：只填环境变量名（ApiKeyEnvVar），插件自己去环境变量取值。
// 密钥值不进本文件、不进宿主设置、不出现在日志和错误提示里。
//
// 下面第一份是 New API / One API 网关的写法（实测可用）。换成别家时，
// 猜错字段名的表现是「数字偏低或拉不到数据」而不是报错，所以先小范围试一天。
//
// 第二份是「只读一个总额」的余额型接口（Kind=balance），默认关着当参考：
// 它不进按天账本、也不进近 7 天柱状图，只在岛面单独占一行。
[
  {
    "Enabled": true,                                  // false = 完全忽略这个接口，只用本地日志
    "Kind": "usage",                                  // usage = 逐条/逐天用量（默认）；balance = 只读一个总额，岛面单占一行
    "Id": "",                                         // 留空则插件按位置自动生成（p0、p1…）。云端账本按这个 id 分份，想改名字又不丢历史就手写一个固定的
    "DisplayName": "我的网关",                          // 岛面和设置页上显示的名字
    "BaseUrl": "https://ps.air-outer.com",             // 接口根地址，结尾不要带 /
    "Path": "/api/log/self",                           // 用量查询路径，接在 BaseUrl 后面
    "AuthHeader": "Authorization",                     // 鉴权头名字（有的家用 x-api-key）
    "AuthPrefix": "",                                  // 鉴权值前缀，例如 "Bearer "；New API 用裸令牌，留空
    "ApiKeyEnvVar": "TOKEN_METER_API_KEY",             // 密钥所在的【环境变量名】，不是密钥本身
    "ExtraHeaders": "New-Api-User=CHANGE_ME",          // 额外固定请求头，多个用分号或换行分隔；把 CHANGE_ME 换成你的用户 ID
    "StartParam": "start_timestamp",                   // 起始时间参数名
    "EndParam": "end_timestamp",                       // 结束时间参数名
    "TimeFormat": "unix",                              // 时间参数格式：date / unix / rfc3339
    "ExtraQuery": "type=2",                            // 额外固定查询串，原样拼上去（type=2 = 消耗日志）
    "ItemsPath": "data.items",                         // 条目数组在返回 JSON 里的位置；支持 data[].results 这种两层写法
    "TimeField": "created_at",                         // 每条记录的时间字段（找不到会去上层对象上找）
    "ModelField": "model_name",                        // 模型名字段；接口不按模型分组就留空，会归进 ModelFallback
    "ModelFallback": "未分组",                          // 上面留空时用的组名
    "InputField": "prompt_tokens",                     // 输入 token 字段
    "OutputField": "completion_tokens",                // 输出 token 字段
    "CachedInputField": "other.cache_tokens",          // 缓存命中的输入字段（other 是「字符串里包的 JSON」，能钻进去）
    "CacheWriteField": "other.cache_creation_tokens",  // 缓存写的字段；支持逗号分隔多个，会相加
    "RequestsField": "",                               // 请求次数字段；很多接口不给，留空就不显示服务端请求数
    "ReasoningField": "",                              // 思考 token 字段；只在「输入已含缓存」或它单独计费时才需要填
    "InputIncludesCached": true,                       // ⚠ 关键口径：上面的输入是否【已包含】缓存命中。true=输入减掉缓存；false=两者互斥直接相加。减错会整段重复计费，接上后拿一天账单对一次
    "ValueScale": 1,                                   // token 数值的统一乘数：接口返回的不是「个」而是「千/百万」时用它校正
    "CostField": "quota",                              // 服务端报回的金额字段；有它就优先用它，省掉按单价推算
    "CostUnitDivisor": 500000,                         // 金额换算率：金额字段的值 ÷ 这个数 = 元/美元（New API 是 50 万单位 = $1）
    "CostScale": 1,                                    // 金额字段的乘数（接口返回「分」就填 0.01）。先乘这个、再除上面那个
    "Currency": "$",                                   // 这个接口上报的币种。不同币种绝不与本地 ¥ 相加
    "ModelScope": "",                                  // 这个接口管哪些模型，逗号分隔子串（如 deepseek-, claude-）；留空 = 它报什么就归什么
    "PageMode": "page",                                // 分页方式：cursor（用返回里的游标）/ page（页码递增）
    "PageParam": "page",                               // 页码/游标要回填到哪个查询参数上
    "PageStart": 0,                                    // page 模式的起始页码（有的接口从 0 开始，有的从 1 开始）
    "PageSizeParam": "per_page",                       // page 模式下每页条数的参数名，留空则不传
    "PageSize": 50,                                    // 每页条数
    "TotalField": "data.total",                        // 判断翻完没有的总数字段，留空则靠「本页为空/不满一页」停
    "PageTokenField": "",                              // cursor 模式用：返回里的下一页游标字段名，留空表示不分页（page 模式下看不到它）
    "HasMoreField": "",                                // cursor 模式用：有没有下一页的布尔字段，false 就停
    "LimitParam": "",                                  // 每次请求最多要多少个时间桶的参数名；靠这个限制返回量的接口才需要填
    "LimitPerRequest": 0,                              // 上面那个参数的值，0 = 不传
    "WindowMaxDays": 1,                                // 单次请求最大时间跨度；逐条日志类接口按 1 天更好翻页
    "MaxWindowsPerRun": 12,                            // 一轮最多拉多少个时间窗口，防止首次同步把界面卡死
    "MaxPagesPerWindow": 50,                           // 单个窗口内最多翻多少页，超了就报错停下来
    "LagMinutes": 10,                                  // 滞后带：晚于「现在 - 该值」的时段不信服务端，继续用本地日志
    "InitialBackfillDays": 7,                          // 首次（没有水位线时）往回追多少天
    "TimeoutSeconds": 15,                              // 单次请求超时
    "ProviderBudgetSeconds": 45,                       // 一轮里这个接口最多花多少秒，超了就本轮先用本地日志，下轮继续
    "LedgerKeepDays": 730,                             // 云端账本在缓存里保留多少天，超出的整天丢掉。0 = 不清理
    "UndercountWarnPercent": 10,                       // 云端报的总量比本地日志少超过这个百分比时，界面标警告（多半是这个 Key 权限不全，看不见全部来源）
  },
  {
    "Enabled": false,                                  // 想看效果就改成 true；上面那个接口的密钥环境变量这里同样能用
    "Kind": "balance",                                 // ← 余额型接口靠这一行区分：它只在岛面单独占一行
    "DisplayName": "我的账户余额",                       // 岛面那一行开头显示的名字
    "BaseUrl": "https://api.example.com",              // 接口根地址，结尾不要带 /
    "Path": "/v1/wallet/balance",                      // 余额接口通常不带时间参数，所以 StartParam/EndParam 在这里不会被拼上去
    "AuthHeader": "Authorization",                     // 鉴权头名字（有的家用 x-api-key）
    "AuthPrefix": "Bearer ",                           // 鉴权值前缀，注意末尾空格；留空则整个头的值就是密钥本身
    "ApiKeyEnvVar": "TOKEN_METER_API_KEY",             // 密钥所在的【环境变量名】，不是密钥本身
    "BalanceField": "data[0].total_balance",           // 取值路径：支持点号、数组下标、钻进「字符串里包的 JSON」；逗号分隔多个会求和（分钱包余额）。值写成 "3.5" 这种字符串也认
    "CostUnitDivisor": 1,                              // 余额也是金额，所以复用同一个换算率（比如 50 万单位 = $1 的记账法就填 500000）
    "CostScale": 1,                                    // 金额字段的乘数（接口返回「分」就填 0.01）
    "Currency": "$",                                   // 这个数是什么币种
    "TimeoutSeconds": 15,                              // 单次请求超时
  }
]

""";

    public string ToJson() => JsonSerializer.Serialize(Items, JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // providers.json 是给人手改的，必须缩进、中文原样显示。
        // UnsafeRelaxedJsonEscaping 只放松 HTML 相关字符的转义，不影响 JSON 合法性；
        // 这个文件不喂给 HTML。
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,

        // 标准 JSON 没有注释，所以这里按 JSONC 读：容忍 // 注释与尾逗号，
        // 让用户能在配置里就地写一行说明。写出时不会带注释 —— 注释能不能留住，
        // 取决于插件承诺不重写已存在的文件（见 TokenMeterPlugin.LoadProviders）。
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

/// <summary>
/// 按接口 id 分份的同步状态，存在 .cache 的 provider-state-v2.json。
///
/// 不能沿用 v1 的原因：v1 的 CoveredUntilUtc 与 ServerDays 是**全局单份**的。
/// 多接口共用一个水位线时，A 接口拉过的区间会直接遮住 B 接口，让它永远不再被请求。
/// v1 只在首次启动时读一次用于迁移，之后只写 v2。
/// </summary>
public sealed class MultiProviderState
{
    public Dictionary<string, ProviderState> ByProvider { get; set; } = new(StringComparer.Ordinal);

    public ProviderState For(string id)
    {
        var key = string.IsNullOrWhiteSpace(id) ? "_" : id;
        if (!ByProvider.TryGetValue(key, out var st))
        {
            st = new ProviderState();
            ByProvider[key] = st;
        }
        return st;
    }

    /// <summary>把单接口时代的 v1 状态挂到迁移出来的那个 id 下。v2 里已有该 id 就不覆盖。</summary>
    public void AdoptLegacy(string id, ProviderState legacy)
    {
        if (legacy is null || string.IsNullOrWhiteSpace(id)) return;
        if (ByProvider.ContainsKey(id)) return;
        ByProvider[id] = legacy;
    }

    /// <summary>接口被删掉后把它的账本一起丢掉，否则状态文件里会留下没人读的孤儿条目。</summary>
    public void DropOrphans(IEnumerable<string> aliveIds)
    {
        var alive = new HashSet<string>(aliveIds, StringComparer.Ordinal);
        foreach (var stale in ByProvider.Keys.Where(k => !alive.Contains(k)).ToList())
            ByProvider.Remove(stale);
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static MultiProviderState Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new MultiProviderState();
        try { return JsonSerializer.Deserialize<MultiProviderState>(json, JsonOpts) ?? new MultiProviderState(); }
        catch (Exception) { return new MultiProviderState(); }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
}
