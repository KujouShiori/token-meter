using System.Globalization;
using TokenMeter;

namespace wlverify;

/// <summary>
/// 本地日志解析 + 多来源低估值告警的用例。
///
/// 原先这份叫 %TEMP%\wlverify\Program.cs，连着入口一起被临时目录回收吃掉了，
/// 工程因此处于「csproj 还引用着它、实际编译不过」的状态。现在重建在工作区内，
/// ProviderTests.cs 一并搬进来。
/// </summary>
internal static class Program
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; return; }
        _fail++;
        Console.WriteLine("  FAIL  " + name + (string.IsNullOrEmpty(detail) ? "" : "  ->  " + detail));
    }

    private static async Task<int> Main()
    {
        ScannerTests();
        UndercountTests();
        SplitManyTests();

        Console.WriteLine($"[Program] {_pass} 过 / {_fail} 败");
        var providerFailures = await ProviderTests.RunAsync();
        Console.WriteLine($"[ProviderTests] 失败 {providerFailures} 条");

        var total = _fail + providerFailures;
        Console.WriteLine(total == 0 ? "RESULT: 全部通过" : $"RESULT: 共 {total} 条失败");
        return total == 0 ? 0 : 1;
    }

    // ---------------- 解析用例 ----------------

    private static string FreshDir(string tag)
    {
        var p = Path.Combine(Path.GetTempPath(), "tmt-" + tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(p);
        return p;
    }

    private static string Line(string? model, string usage)
        => "{\"timestamp\":\"2026-10-14T04:00:00Z\",\"role\":\"assistant\","
         + (model is null ? "" : "\"model\":\"" + model + "\",")
         + "\"usage\":" + usage + "}";

    private static UsageLedger ScanOf(params string[] lines)
    {
        var logs = FreshDir("logs");
        File.WriteAllText(Path.Combine(logs, "a.jsonl"), string.Join("\n", lines) + "\n");
        return new UsageScanner(FreshDir("cache")).Scan(new[] { logs }, CancellationToken.None);
    }

    private static Counters Only(UsageLedger l)
    {
        foreach (var byModel in l.Days.Values)
            foreach (var c in byModel.Values)
                return c;
        return new Counters();
    }

    private static string OnlyModel(UsageLedger l)
    {
        foreach (var byModel in l.Days.Values)
            foreach (var k in byModel.Keys)
                return k;
        return "(没有桶)";
    }

    private static void ScannerTests()
    {
        Console.WriteLine("== 本地日志解析 ==");

        var a = Only(ScanOf(Line("vendor/SCNet-Max",
            "{\"input_tokens\":1000,\"cache_creation_input_tokens\":50,\"cache_read_input_tokens\":200,"
            + "\"output_tokens\":30,\"server_tool_use\":{\"web_search_requests\":0}}")));
        Check("Anthropic: input_tokens -> Input", a.Input == 1000, $"Input={a.Input}");
        Check("Anthropic: output_tokens -> Output", a.Output == 30, $"Output={a.Output}");
        Check("Anthropic: cache_read_input_tokens -> CacheRead", a.CacheRead == 200, $"CacheRead={a.CacheRead}");
        Check("Anthropic: cache_creation -> CacheWrite", a.CacheWrite == 50, $"CacheWrite={a.CacheWrite}");
        Check("Anthropic: 没给 total 时四项相加", a.Total == 1280, $"Total={a.Total}");

        var o = Only(ScanOf(Line("gpt-x",
            "{\"prompt_tokens\":500,\"completion_tokens\":50,\"total_tokens\":550,"
            + "\"prompt_tokens_details\":{\"cached_tokens\":300}}")));
        Check("OpenAI: prompt_tokens -> Input", o.Input == 500, $"Input={o.Input}");
        Check("OpenAI: completion_tokens -> Output", o.Output == 50, $"Output={o.Output}");
        Check("OpenAI: 嵌套 details 不钻进去取值", o.CacheRead == 0, $"CacheRead={o.CacheRead}");
        Check("OpenAI: 显式 total_tokens 优先于求和", o.Total == 550, $"Total={o.Total}");

        var c = Only(ScanOf(Line("SCNet-Max",
            "{\"input\":10,\"output\":5,\"cacheRead\":3,\"cacheWrite\":1,\"totalTokens\":19}")));
        Check("camelCase 老口径没退化", c.Total == 19 && c.CacheRead == 3, $"Total={c.Total} CacheRead={c.CacheRead}");

        var p = Only(ScanOf(Line("SCNet-Max",
            "{\"input\":10,\"output\":5,\"cost\":{\"input\":9999,\"output\":8888},\"totalTokens\":15}")));
        Check("嵌套 cost 的同名键不会被当成 token", p.Input == 10 && p.Output == 5, $"Input={p.Input} Output={p.Output}");

        Check("模型名剥掉供应商前缀",
            OnlyModel(ScanOf(Line("gw-custom-1a2b3c4d/SCNet-Max", "{\"input_tokens\":10}"))) == "SCNet-Max");
        Check("无斜杠的模型名保持原样",
            OnlyModel(ScanOf(Line("SCNet-Max", "{\"input_tokens\":10}"))) == "SCNet-Max");
        Check("多级前缀只留最后一段",
            OnlyModel(ScanOf(Line("a/b/deepseek-v4-flash", "{\"input_tokens\":10}"))) == "deepseek-v4-flash");
        Check("没有 model 字段归进未知",
            OnlyModel(ScanOf(Line(null, "{\"input_tokens\":10}"))) == "(未知模型)");

        var notAssistant = ScanOf("{\"timestamp\":\"2026-10-14T04:00:00Z\",\"role\":\"user\","
            + "\"model\":\"SCNet-Max\",\"usage\":{\"input_tokens\":10}}");
        Check("role=user 的行不统计", notAssistant.Days.Count == 0, $"days={notAssistant.Days.Count}");
    }

    // ---------------- 低估值告警用例 ----------------

    private static DateTime EndOfDayUtc(string dayKey)
    {
        var d = DateOnly.ParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return DateTime.SpecifyKind(d.ToDateTime(new TimeOnly(23, 59)), DateTimeKind.Unspecified).ToUniversalTime();
    }

    private static UsageLedger Ledger(params (string Day, string Model, long Total)[] rows)
    {
        var l = new UsageLedger();
        foreach (var r in rows)
            l.Add(r.Day, r.Model, new Counters { Input = r.Total, Total = r.Total, Requests = 1 });
        return l;
    }

    private static void UndercountTests()
    {
        Console.WriteLine("== 低估值告警 ==");

        var local = Ledger(("2026-10-01", "glm-4", 1000));
        var covered = EndOfDayUtc("2026-10-01");

        var (_, _, i1) = LedgerSplitter.Split(local, Ledger(("2026-10-01", "glm-4", 700)),
            ModelScope.Empty, ModelScope.Empty, covered, 10);
        Check("云端少报 30% 触发告警", i1.Undercount, i1.UndercountDetail);
        Check("告警正文带得出差值", i1.UndercountDetail.Contains("少 30%"), i1.UndercountDetail);

        var (_, _, i2) = LedgerSplitter.Split(local, Ledger(("2026-10-01", "glm-4", 1200)),
            ModelScope.Empty, ModelScope.Empty, covered, 10);
        Check("云端 >= 本地 不告警（它覆盖账户下所有设备）", !i2.Undercount, i2.UndercountDetail);

        var (_, _, i3) = LedgerSplitter.Split(local, Ledger(("2026-10-01", "glm-4", 950)),
            ModelScope.Empty, ModelScope.Empty, covered, 10);
        Check("差值没到阈值时不告警", !i3.Undercount, i3.UndercountDetail);

        var wide = Ledger(("2026-10-01", "glm-4", 1000), ("2026-10-05", "glm-4", 99000));
        var (_, _, i4) = LedgerSplitter.Split(wide, Ledger(("2026-10-01", "glm-4", 1000)),
            ModelScope.Empty, ModelScope.Empty, covered, 10);
        Check("未覆盖的日子不参与比对，否则误报", !i4.Undercount, i4.UndercountDetail);

        var (_, _, i5) = LedgerSplitter.Split(Ledger(("2026-10-01", "kimi-k2", 5000)),
            Ledger(("2026-10-01", "glm-4", 100)),
            ModelScope.Empty, ModelScope.Parse("glm-"), covered, 10);
        Check("云端没认领的模型不参与比对", !i5.Undercount, i5.UndercountDetail);

        var (_, _, i6) = LedgerSplitter.Split(local, Ledger(("2026-10-01", "glm-4", 700)),
            ModelScope.Empty, ModelScope.Empty, null, 10);
        Check("没有水位线时不比对", !i6.Undercount, "coveredUntilUtc=null");

        var (_, _, i7) = LedgerSplitter.Split(local, Ledger(("2026-10-01", "glm-4", 700)),
            ModelScope.Empty, ModelScope.Empty, covered, 0);
        Check("阈值 0 = 关掉检测", !i7.Undercount, "warnPercent=0");

        var (_, serverOut, _) = LedgerSplitter.Split(local, Ledger(("2026-10-01", "glm-4", 700)),
            ModelScope.Empty, ModelScope.Empty, covered, 10);
        Check("告警不改变分派结果：云端块仍是云端报的数",
            serverOut.Days["2026-10-01"]["glm-4"].Total == 700, "server block changed");
    }

    // ---------------- N 路归属 ----------------

    private static void SplitManyTests()
    {
        Console.WriteLine("== 多接口归属分派 ==");

        var covered = EndOfDayUtc("2026-10-01");

        var local2 = Ledger(("2026-10-01", "glm-4", 1000), ("2026-10-01", "kimi-k2", 2000));
        var (l2, a2, i2) = LedgerSplitter.SplitMany(local2,
            new[]
            {
                ("A", Ledger(("2026-10-01", "glm-4", 1100)), ModelScope.Empty, (DateTime?)covered, 0),
                ("B", Ledger(("2026-10-01", "kimi-k2", 2100)), ModelScope.Empty, (DateTime?)covered, 0),
            },
            ModelScope.Empty);
        Check("A 拿到自己报的模型", a2[0].Ledger.Days["2026-10-01"]["glm-4"].Total == 1100, "A");
        Check("B 拿到自己报的模型", a2[1].Ledger.Days["2026-10-01"]["kimi-k2"].Total == 2100, "B");
        Check("两个模型都从本地剔走", l2.Days.Count == 0, $"local days={l2.Days.Count}");
        Check("无冲突时不写冲突说明", i2.Conflicts.Count == 0 && string.IsNullOrEmpty(i2.ConflictNote), i2.ConflictNote);

        var (_, a3, i3) = LedgerSplitter.SplitMany(Ledger(("2026-10-01", "glm-4", 1000)),
            new[]
            {
                ("A", Ledger(("2026-10-01", "glm-4", 1100)), ModelScope.Empty, (DateTime?)covered, 0),
                ("B", Ledger(("2026-10-01", "glm-4", 9999)), ModelScope.Empty, (DateTime?)covered, 0),
            },
            ModelScope.Empty);
        Check("重叠模型只归第一个接口", a3[0].Ledger.Days["2026-10-01"]["glm-4"].Total == 1100, "A");
        Check("第二个接口没拿到重叠模型", !a3[1].Ledger.Days.ContainsKey("2026-10-01"), "B 不该有数");
        Check("重叠被记进冲突清单", i3.Conflicts.Contains("glm-4"), string.Join(",", i3.Conflicts));
        Check("冲突说明非空：不能静默少一块数字", !string.IsNullOrEmpty(i3.ConflictNote), "empty");
        var sum3 = a3.Sum(a => a.Ledger.TotalTokens());
        Check("重叠模型不会被算两遍", sum3 == 1100, $"sum={sum3}");

        var multiDay = Ledger(("2026-10-01", "glm-4", 100), ("2026-10-02", "glm-4", 200));
        var (_, a4, i4) = LedgerSplitter.SplitMany(Ledger(),
            new[] { ("A", multiDay, ModelScope.Empty, (DateTime?)null, 0) }, ModelScope.Empty);
        Check("同一接口跨天报同一模型不算冲突", i4.Conflicts.Count == 0, string.Join(",", i4.Conflicts));
        Check("跨天的两天都进了该接口账本", a4[0].Ledger.Days.Count == 2, $"days={a4[0].Ledger.Days.Count}");

        var (_, a5, _) = LedgerSplitter.SplitMany(Ledger(),
            new[]
            {
                ("A", Ledger(("2026-10-01", "glm-4", 100)), ModelScope.Parse("glm-"), (DateTime?)covered, 0),
                ("B", Ledger(("2026-10-01", "glm-4", 700)), ModelScope.Parse("kimi"), (DateTime?)covered, 0),
            },
            ModelScope.Empty);
        Check("范围不匹配的接口拿不到该模型", !a5[1].Ledger.Days.ContainsKey("2026-10-01"), "B scope");
        Check("范围匹配的接口照常拿到", a5[0].Ledger.Days["2026-10-01"]["glm-4"].Total == 100, "A scope");

        var uLocal = Ledger(("2026-10-01", "glm-4", 1000), ("2026-10-01", "kimi-k2", 1000));
        var (_, a6, i6) = LedgerSplitter.SplitMany(uLocal,
            new[]
            {
                ("A", Ledger(("2026-10-01", "glm-4", 300)), ModelScope.Empty, (DateTime?)covered, 10),
                ("B", Ledger(("2026-10-01", "kimi-k2", 1000)), ModelScope.Empty, (DateTime?)covered, 10),
            },
            ModelScope.Empty);
        Check("报少的接口被单独标低估值", a6[0].Undercount, "A");
        Check("报足的接口不被连带误标", !a6[1].Undercount, "B");
        Check("整体告警位为真且带出差值", i6.Undercount && i6.UndercountDetail.Contains("少 70%"), i6.UndercountDetail);
    }
}
