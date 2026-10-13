using System.Globalization;

namespace TokenMeter;

/// <summary>
/// 模型归属范围：一组大小写无关的子串模式，{@code *} 表示全部。
///
/// 为什么需要它：本地会话日志与云端用量接口可能来自**两个不同的运营商**，
/// 统计的是两份互不相交的用量。之前按「服务端覆盖本地整天」合并，
/// 实测把 29 天本地账本压成了 1 天。所以改成按模型归属分派，各算各的再分列展示。
/// </summary>
public sealed class ModelScope
{
    private readonly string[] _patterns;

    private ModelScope(string[] patterns) => _patterns = patterns;

    public static readonly ModelScope Empty = new(System.Array.Empty<string>());

    public bool IsEmpty => _patterns.Length == 0;

    public static ModelScope Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Empty;
        var list = new List<string>();
        foreach (var part in raw.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            if (p.Length > 0) list.Add(p);
        }
        return new ModelScope(list.ToArray());
    }

    /// <summary>是否声明了这个模型。</summary>
    public bool Matches(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || _patterns.Length == 0) return false;
        foreach (var p in _patterns)
        {
            if (p == "*") return true;
            if (model.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public string Describe() => _patterns.Length == 0 ? "（未填）" : string.Join(", ", _patterns);
}

/// <summary>一个来源在界面上的整块数据。</summary>
public sealed class SourceBlock
{
    public string Name { get; set; } = "";
    public string Currency { get; set; } = "¥";

    public WindowAgg Today { get; set; } = new();
    public WindowAgg Week { get; set; } = new();
    public WindowAgg Month { get; set; } = new();
    public WindowAgg All { get; set; } = new();
    public List<ModelUsage> TodayByModel { get; set; } = new();
    public List<DayPoint> Last7 { get; set; } = new();
    public List<string> UnpricedNames { get; set; } = new();

    public bool HasData { get; set; }
    public int LedgerDays { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>分派结果说明，用于在界面上交代「哪些模型被划给了云端」。</summary>
public sealed class SplitInfo
{
    public List<string> MovedToServer { get; } = new();
    public List<string> LocalOnly { get; } = new();
    public List<string> ServerOnly { get; } = new();
    /// <summary>本地范围填了、但模型不在范围内 → 两边都不算。必须告知用户，否则数字会“凭空变少”。</summary>
    public List<string> DroppedByLocalScope { get; } = new();
    /// <summary>卡片上展示的说明：只讲「哪些模型被云端接管」。</summary>
    public string Note { get; set; } = "";
    /// <summary>范围外丢掉的模型：不放卡片（太长），只放到设置页状态行。</summary>
    public string DroppedNote { get; set; } = "";
    /// <summary>云端在它已认领的模型上报得比本地少、且超过阈值 → 多半是这个 Key 权限不全。</summary>
    public bool Undercount { get; set; }
    /// <summary>低估值告警正文。显示走 ProviderNote 通道（状态行与聚光卡都读它）。</summary>
    public string UndercountDetail { get; set; } = "";
}

/// <summary>
/// 把本地账本与云端账本按模型归属拆成两块，**不做跨币种相加**。
///
/// 规则：
///   1. 云端声明了的模型 → 归云端；本地同名的那份**不再重复计入**（否则同一模型算两遍）
///   2. 本地声明了、云端也声明的模型 → 以云端为准（云端是权威扣费方），并在说明里标出来
///   3. 云端没声明的模型 → 留在本地块
///   4. 云端范围为空时，视为「云端上报了什么就归什么」，但仍然不覆盖本地其他模型
///   5. 分派完之后再做低估值检测：只比「云端已认领的模型」且只比「云端已覆盖的日子」，
///      方向是单向的（见 DetectUndercount）
/// </summary>
public static class LedgerSplitter
{
    /// <summary>后两个参数是低估值检测用的，给默认值是为了不打断已有的 4 参数调用点
    /// （回归工程里有四处按老签名调）；默认 0 = 不检测。</summary>
    public static (UsageLedger Local, UsageLedger Server, SplitInfo Info) Split(
        UsageLedger local, UsageLedger server, ModelScope localScope, ModelScope providerScope,
        DateTime? coveredUntilUtc = null, int undercountWarnPercent = 0)
    {
        var info = new SplitInfo();
        var localOut = new UsageLedger
        {
            RootDescription = local.RootDescription,
            Files = local.Files,
            ScannedBytes = local.ScannedBytes,
            Note = local.Note,
        };
        var serverOut = new UsageLedger { RootDescription = server.RootDescription };

        var serverModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var byModel in server.Days.Values)
            foreach (var m in byModel.Keys)
                serverModels.Add(m);

        // 云端块：按 providerScope 取；scope 为空则全取
        foreach (var (dayKey, byModel) in server.Days)
        {
            foreach (var (model, c) in byModel)
            {
                if (providerScope.IsEmpty || providerScope.Matches(model))
                    serverOut.Add(dayKey, model, c);
            }
        }

        var claimedByServer = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in serverModels)
            if (providerScope.IsEmpty || providerScope.Matches(m))
                claimedByServer.Add(m);

        // 本地块：用户填了范围就只算范围内的；被云端认领的模型一律剔掉，避免同一模型两边各算一次
        foreach (var (dayKey, byModel) in local.Days)
        {
            foreach (var (model, c) in byModel)
            {
                if (claimedByServer.Contains(model))
                {
                    if (!info.MovedToServer.Contains(model, StringComparer.OrdinalIgnoreCase))
                        info.MovedToServer.Add(model);
                    continue;
                }
                if (!localScope.IsEmpty && !localScope.Matches(model))
                {
                    if (!info.DroppedByLocalScope.Contains(model, StringComparer.OrdinalIgnoreCase))
                        info.DroppedByLocalScope.Add(model);
                    continue;
                }
                localOut.Add(dayKey, model, c);
            }
        }

        foreach (var m in localOut.AllModels())
            if (!info.LocalOnly.Contains(m, StringComparer.OrdinalIgnoreCase)) info.LocalOnly.Add(m);
        foreach (var m in serverOut.AllModels())
            if (!info.ServerOnly.Contains(m, StringComparer.OrdinalIgnoreCase)) info.ServerOnly.Add(m);

        if (info.MovedToServer.Count > 0)
            info.Note = $"以下模型已由云端接口接管，本地日志不再重复计入：{string.Join("、", info.MovedToServer)}";

        if (info.DroppedByLocalScope.Count > 0)
            info.DroppedNote = $"本地范围未包含这些模型，两边都没统计：{string.Join("、", info.DroppedByLocalScope)}";

        DetectUndercount(info, local, server, claimedByServer, coveredUntilUtc, undercountWarnPercent);

        return (localOut, serverOut, info);
    }

    /// <summary>
    /// 低估值检测。方向是**单向**的：本地日志只覆盖这台机器，云端接口覆盖账户下所有设备，
    /// 所以云端 &gt;= 本地是正常的；反过来云端明显少于本地，多半是这个 Key 的权限看不到
    /// 全部来源（受限 Key 常常只看得见自己创建的 Key 的用量）。这类接口不会报错、只会少给数，
    /// 所以宁可标警告，也不要把它当准确值。
    /// 注意：这里**只出告警，不改分派规则**——不是当年那个"用服务端覆盖本地整天"的实现。
    /// </summary>
    private static void DetectUndercount(SplitInfo info, UsageLedger local, UsageLedger server,
        HashSet<string> claimedByServer, DateTime? coveredUntilUtc, int undercountWarnPercent)
    {
        if (coveredUntilUtc is null || undercountWarnPercent <= 0 || claimedByServer.Count == 0) return;

        var boundary = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(coveredUntilUtc.Value, DateTimeKind.Utc), TimeZoneInfo.Local));

        long SumCovered(UsageLedger l)
        {
            var t = 0L;
            foreach (var (dayKey, byModel) in l.Days)
            {
                if (!DateOnly.TryParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var d) || d.CompareTo(boundary) > 0) continue;
                foreach (var (model, c) in byModel)
                    if (claimedByServer.Contains(model)) t += c.Total;
            }
            return t;
        }

        var serverTotal = SumCovered(server);
        var localTotal = SumCovered(local);
        if (localTotal <= 0 || serverTotal >= localTotal) return;

        var percent = (int)Math.Round((localTotal - serverTotal) * 100.0 / localTotal);
        if (percent < Math.Max(1, undercountWarnPercent)) return;

        info.Undercount = true;
        info.UndercountDetail =
            $"云端在已覆盖的区间里只报了 {Format.Tokens(serverTotal)} token，"
            + $"本地日志同一批模型有 {Format.Tokens(localTotal)}（少 {percent}%）。"
            + "常见原因是这个 Key 的权限看不到账户下全部来源——云端数据仍在用，但请把它当低估值。";
    }
}
