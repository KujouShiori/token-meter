using System.Text.Json;

namespace TokenMeter;

/// <summary>一组 token 计数。单位都是「个」，不是钱。</summary>
public sealed class Counters
{
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public long Total { get; set; }
    public long Requests { get; set; }

    public Counters Clone() => new()
    {
        Input = this.Input,
        Output = this.Output,
        CacheRead = this.CacheRead,
        CacheWrite = this.CacheWrite,
        Total = this.Total,
        Requests = this.Requests,
    };

    public void Add(Counters o)
    {
        Input += o.Input;
        Output += o.Output;
        CacheRead += o.CacheRead;
        CacheWrite += o.CacheWrite;
        Total += o.Total;
        Requests += o.Requests;
    }

    /// <summary>缓存命中率：读缓存占「输入侧总量」的比例。</summary>
    public decimal CacheHitRate
    {
        get
        {
            var inbound = Input + CacheRead;
            if (inbound <= 0) return 0m;
            return Math.Clamp((decimal)CacheRead / inbound * 100m, 0m, 100m);
        }
    }

    /// <summary>缓存区间的 6 个值，顺序固定：输入/输出/缓存读/缓存写/合计/请求数。</summary>
    public long[] ToArray() => new[] { Input, Output, CacheRead, CacheWrite, Total, Requests };

    public static Counters FromArray(long[]? a)
    {
        if (a is null || a.Length < 6) return new Counters();
        return new Counters
        {
            Input = a[0],
            Output = a[1],
            CacheRead = a[2],
            CacheWrite = a[3],
            Total = a[4],
            Requests = a[5],
        };
    }
}

/// <summary>某个模型在某个统计窗口里的用量与估算费用。</summary>
public sealed class ModelUsage
{
    public string Model { get; set; } = "";
    public Counters C { get; set; } = new();
    public decimal Cost { get; set; }
    public bool Priced { get; set; }
}

/// <summary>一个统计窗口（今日/本周/本月/累计）的汇总。</summary>
public sealed class WindowAgg
{
    public Counters C { get; set; } = new();
    public decimal Cost { get; set; }
    public int Models { get; set; }
    public int UnpricedModels { get; set; }
    public bool HasUnpriced => UnpricedModels > 0;
}

/// <summary>近 N 天里某一天的用量点。</summary>
public sealed class DayPoint
{
    public DateOnly Day { get; set; }
    public long Total { get; set; }
    public decimal Cost { get; set; }
    public bool HasUnpriced { get; set; }
}

/// <summary>一次快照：视图只认这个，不再碰原始账本。</summary>
public sealed class Snapshot
{
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
    public bool HasData { get; set; }
    public string Currency { get; set; } = "¥";

    public WindowAgg Today { get; set; } = new();
    public WindowAgg Week { get; set; } = new();
    public WindowAgg Month { get; set; } = new();
    public WindowAgg All { get; set; } = new();

    public List<ModelUsage> TodayByModel { get; set; } = new();
    public List<DayPoint> Last7 { get; set; } = new();
    public List<string> UnpricedNames { get; set; } = new();

    public string RootDescription { get; set; } = "";

    /// <summary>当前数字的来源：本地日志 / 服务端 / 混合。口径变了必须让用户看得见。</summary>
    public string SourceMode { get; set; } = "本地日志";
    public string ProviderSummary { get; set; } = "";
    public string ProviderNote { get; set; } = "";
    /// <summary>服务端数据可信到哪一刻（已扣滞后带），之后的时间继续用本地日志。</summary>
    public DateTime? CoveredUntilUtc { get; set; }
    /// <summary>服务端比本地少超阈值：多半是 Key 权限覆盖不全，界面要标警告。</summary>
    public bool ServerUndercount { get; set; }
    public string UndercountDetail { get; set; } = "";

    public int Files { get; set; }
    public long ScannedBytes { get; set; }
    public int LedgerDays { get; set; }
    public string? Note { get; set; }
    public string? Error { get; set; }
}

/// <summary>按「本地日期 -> 模型 -> 计数」组织的账本。</summary>
public sealed class UsageLedger
{
    public Dictionary<string, Dictionary<string, Counters>> Days { get; } = new();
    public long ScannedBytes { get; set; }
    public int Files { get; set; }
    public string RootDescription { get; set; } = "";
    public string? Note { get; set; }

    public void Add(string dayKey, string model, Counters c)
    {
        if (!Days.TryGetValue(dayKey, out var byModel))
        {
            byModel = new Dictionary<string, Counters>(StringComparer.OrdinalIgnoreCase);
            Days[dayKey] = byModel;
        }
        if (!byModel.TryGetValue(model, out var existing))
        {
            byModel[model] = c.Clone();
            return;
        }
        existing.Add(c);
    }

    /// <summary>
    /// 用 incoming 的整天数据直接覆盖同一天。服务端接口按天分桶返回，
    /// 重复拉同一天时「覆盖」才是对的，「累加」会把同一天的用量翻倍。
    /// </summary>
    public void MergeOverwrite(UsageLedger incoming)
    {
        foreach (var (dayKey, byModel) in incoming.Days)
        {
            var clone = new Dictionary<string, Counters>(StringComparer.OrdinalIgnoreCase);
            foreach (var (model, c) in byModel) clone[model] = c.Clone();
            Days[dayKey] = clone;
        }
    }

    public long TotalTokens()
    {
        long acc = 0;
        foreach (var byModel in Days.Values)
            foreach (var c in byModel.Values)
                acc += c.Total;
        return acc;
    }

    public UsageLedger Clone()
    {
        var copy = new UsageLedger
        {
            ScannedBytes = ScannedBytes,
            Files = Files,
            RootDescription = RootDescription,
            Note = Note,
        };
        foreach (var (dayKey, byModel) in Days)
            foreach (var (model, c) in byModel)
                copy.Add(dayKey, model, c);
        return copy;
    }

    public IEnumerable<string> AllModels()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var byModel in Days.Values)
            foreach (var m in byModel.Keys)
                set.Add(m);
        return set;
    }

    public bool IsEmpty => Days.Count == 0;
}

/// <summary>一个统计窗口 + 它的逐模型明细。</summary>
internal sealed class Bucket
{
    public WindowAgg Agg { get; } = new();
    public Dictionary<string, ModelUsage> ByModel { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string model, Counters c, decimal cost, bool priced)
    {
        Agg.C.Add(c);
        Agg.Cost += cost;

        if (!ByModel.TryGetValue(model, out var mu))
        {
            mu = new ModelUsage { Model = model, Priced = priced };
            ByModel[model] = mu;
        }
        mu.C.Add(c);
        mu.Cost += cost;
        // 同一模型在不同天里价格配置不会变，但旧配置缺失时保持「未定价」更诚实
        if (!priced) mu.Priced = false;
    }

    public void Finish()
    {
        Agg.Models = ByModel.Count;
        Agg.UnpricedModels = ByModel.Values.Count(v => !v.Priced);
    }
}

/// <summary>把账本 + 单价配置算成视图能直接画的快照。</summary>
public static class SnapshotBuilder
{
    public static Snapshot Build(UsageLedger ledger, PricingConfig pricing)
    {
        var snap = new Snapshot
        {
            Currency = pricing.Currency,
            RootDescription = ledger.RootDescription,
            Files = ledger.Files,
            ScannedBytes = ledger.ScannedBytes,
            LedgerDays = ledger.Days.Count,
            Note = ledger.Note,
        };

        var today = DateOnly.FromDateTime(DateTime.Now);
        var todayKey = Format.DayKey(today);
        var weekStartKey = Format.DayKey(StartOfWeek(today));
        var monthStartKey = Format.DayKey(new DateOnly(today.Year, today.Month, 1));

        var todayB = new Bucket();
        var weekB = new Bucket();
        var monthB = new Bucket();
        var allB = new Bucket();

        foreach (var (key, byModel) in ledger.Days)
        {
            var isToday = string.Equals(key, todayKey, StringComparison.Ordinal);
            var isWeek = string.CompareOrdinal(key, weekStartKey) >= 0;
            var isMonth = string.CompareOrdinal(key, monthStartKey) >= 0;

            foreach (var (model, c) in byModel)
            {
                var priced = pricing.TryCost(model, c, out var cost);

                allB.Add(model, c, cost, priced);
                if (isWeek) weekB.Add(model, c, cost, priced);
                if (isMonth) monthB.Add(model, c, cost, priced);
                if (isToday) todayB.Add(model, c, cost, priced);
            }
        }

        todayB.Finish();
        weekB.Finish();
        monthB.Finish();
        allB.Finish();

        snap.Today = todayB.Agg;
        snap.Week = weekB.Agg;
        snap.Month = monthB.Agg;
        snap.All = allB.Agg;

        snap.TodayByModel = todayB.ByModel.Values
            .OrderByDescending(m => m.Cost)
            .ThenByDescending(m => m.C.Total)
            .ToList();

        snap.UnpricedNames = allB.ByModel.Values
            .Where(m => !m.Priced)
            .Select(m => m.Model)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 近 7 天（含今天）
        for (var i = 6; i >= 0; i--)
        {
            var day = today.AddDays(-i);
            var point = new DayPoint { Day = day };
            if (ledger.Days.TryGetValue(Format.DayKey(day), out var byModel))
            {
                foreach (var (model, c) in byModel)
                {
                    point.Total += c.Total;
                    var priced = pricing.TryCost(model, c, out var cost);
                    point.Cost += cost;
                    if (!priced) point.HasUnpriced = true;
                }
            }
            snap.Last7.Add(point);
        }

        snap.HasData = snap.All.C.Total > 0 || snap.All.C.Requests > 0;
        return snap;
    }

    /// <summary>周一为一周的开始（国内习惯）。</summary>
    private static DateOnly StartOfWeek(DateOnly today)
    {
        var diff = ((int)today.DayOfWeek + 6) % 7;   // Monday = 0
        return today.AddDays(-diff);
    }

}
