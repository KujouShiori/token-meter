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

    /// <summary>金额换算率：字段值 ÷ 该值 = 元/美元。
    /// New API 这类用「500000 单位 = $1」的记账法就填 500000。</summary>
    public double CostUnitDivisor { get; set; } = 1;

    /// <summary>所有 token 数值的统一乘数。接口返回的不是「个」而是「千/百万」时用它校正，默认 1。</summary>
    public double ValueScale { get; set; } = 1;

    /// <summary>金额字段的乘数（例如接口返回「分」时填 0.01）。默认 1。</summary>
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
            if (!string.IsNullOrEmpty(cfg.Path) && !cfg.Path.StartsWith('/')) cfg.Path = "/" + cfg.Path;
            cfg.WindowMaxDays = Math.Clamp(cfg.WindowMaxDays, 1, 366);
            cfg.LagMinutes = Math.Clamp(cfg.LagMinutes, 0, 2880);
            cfg.TimeoutSeconds = Math.Clamp(cfg.TimeoutSeconds, 2, 120);
            cfg.ProviderBudgetSeconds = Math.Clamp(cfg.ProviderBudgetSeconds, 5, 300);
            cfg.MaxPagesPerWindow = Math.Clamp(cfg.MaxPagesPerWindow, 1, 500);
            cfg.MaxWindowsPerRun = Math.Clamp(cfg.MaxWindowsPerRun, 1, 200);
            cfg.InitialBackfillDays = Math.Clamp(cfg.InitialBackfillDays, 1, 366);
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
        => $"{DisplayName}｜{BaseUrl}{Path}｜密钥来自环境变量 {ApiKeyEnvVar}｜窗口≤{WindowMaxDays}天｜滞后{LagMinutes}分钟｜"
         + $"输入{(InputIncludesCached ? "含" : "不含")}缓存";
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

    public void SaveLedger(UsageLedger? ledger)
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
