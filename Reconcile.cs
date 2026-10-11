using System.Globalization;

namespace TokenMeter;

public sealed class MergeInfo
{
    public string Mode { get; set; } = "本地日志";
    public bool Undercount { get; set; }
    public string UndercountDetail { get; set; } = "";
    public DateTime? CoveredUntilUtc { get; set; }
    public long ServerTokens { get; set; }
    public long LocalTokensInCoveredRange { get; set; }
    public int ServerDays { get; set; }
}

/// <summary>
/// 服务端账本与本地账本的对账。
///
/// 规则（对应用户要求的第一步）：
///   1. 服务端没启用 / 拉取失败 → 完全用本地，行为跟 1.0.0 一致；
///   2. 落在服务端覆盖范围内（已扣滞后带）的日子 → **以服务端为准**，包括服务端给 0 的情况；
///   3. 覆盖范围之外（最近滞后带内、以及服务端还没追到的历史）→ 继续用本地日志；
///   4. 对账：覆盖范围内服务端总量明显低于本地 → 置 Undercount。
///      受限 Key 常常只能看到自己创建的 Key 的用量，这类接口不会报错、只会少给数，
///      所以宁可标警告，也不要静默把真实用量抹小。
/// </summary>
public static class LedgerMerger
{
    public static (UsageLedger Effective, MergeInfo Info) Merge(
        UsageLedger local, UsageLedger? server, DateTime? coveredUntilUtc, int undercountWarnPercent)
    {
        var info = new MergeInfo();

        if (server is null || server.Days.Count == 0 || coveredUntilUtc is null)
        {
            info.Mode = server is null ? "本地日志" : "本地日志（服务端无数据）";
            return (local, info);
        }

        var boundary = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(coveredUntilUtc.Value, DateTimeKind.Utc), TimeZoneInfo.Local));
        info.CoveredUntilUtc = coveredUntilUtc;
        info.ServerDays = server.Days.Count;

        var effective = new UsageLedger
        {
            RootDescription = local.RootDescription,
            Files = local.Files,
            ScannedBytes = local.ScannedBytes,
            Note = local.Note,
        };

        var serverTotal = 0L;
        var localInCovered = 0L;
        var usedServer = 0;
        var usedLocal = 0;

        foreach (var (dayKey, byModel) in server.Days)
        {
            foreach (var (model, c) in byModel)
            {
                effective.Add(dayKey, model, c);
                serverTotal += c.Total;
            }
        }

        foreach (var (dayKey, byModel) in local.Days)
        {
            var day = ParseKey(dayKey);
            if (day is null)
            {
                continue;
            }

            if (day.Value.CompareTo(boundary) <= 0)
            {
                // 服务端覆盖范围内：本地只用来算差异，不进最终账本
                foreach (var c in byModel.Values) localInCovered += c.Total;
                usedServer++;
                continue;
            }

            foreach (var (model, c) in byModel) effective.Add(dayKey, model, c);
            usedLocal++;
        }

        info.ServerTokens = serverTotal;
        info.LocalTokensInCoveredRange = localInCovered;
        info.Mode = localInCovered > 0
            ? (usedLocal > 0 ? "混合" : "服务端")
            : (usedLocal > 0 ? "服务端 + 本地补充" : "服务端");

        if (localInCovered > 0 && serverTotal < localInCovered)
        {
            var missing = localInCovered - serverTotal;
            var percent = (int)Math.Round(missing * 100.0 / localInCovered);
            if (percent >= Math.Max(1, undercountWarnPercent))
            {
                info.Undercount = true;
                info.UndercountDetail =
                    $"服务端在已覆盖的 {usedServer} 天里只报了 {Format.Tokens(serverTotal)} token，"
                    + $"本地日志同期有 {Format.Tokens(localInCovered)}（差 {percent}%）。"
                    + "常见原因是这个 Key 的权限看不到账户下全部来源——服务端数据仍在用，但请把它当低估值。";
            }
        }

        return (effective, info);
    }

    private static DateOnly? ParseKey(string dayKey)
        => DateOnly.TryParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
}
