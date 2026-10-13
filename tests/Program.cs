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
        RegistryTests();
        AlertTests();
        DayPointTests();
        IslandLayoutTests();
        BalanceTests();

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

        // 两个接口各报不同模型：互不干扰，本地把两个都让出去
        var local2 = Ledger(("2026-10-01", "glm-4", 1000), ("2026-10-01", "kimi-k2", 2000));
        var (l2, a2, i2) = LedgerSplitter.SplitMany(local2,
            new[]
            {
                ("A", "网关A", Ledger(("2026-10-01", "glm-4", 1100)), ModelScope.Empty, (DateTime?)covered, 0),
                ("B", "网关B", Ledger(("2026-10-01", "kimi-k2", 2100)), ModelScope.Empty, (DateTime?)covered, 0),
            },
            ModelScope.Empty);
        Check("A 拿到自己报的模型", a2[0].Ledger.Days["2026-10-01"]["glm-4"].Total == 1100, "A");
        Check("B 拿到自己报的模型", a2[1].Ledger.Days["2026-10-01"]["kimi-k2"].Total == 2100, "B");
        Check("两个模型都从本地剔走", l2.Days.Count == 0, $"local days={l2.Days.Count}");
        Check("无冲突时不写冲突说明", i2.Conflicts.Count == 0 && string.IsNullOrEmpty(i2.ConflictNote), i2.ConflictNote);

        // 同一模型被两个接口都报：禁止重叠 —— 谁都不给，回落到本地，并点名报出冲突
        var (_, a3, i3) = LedgerSplitter.SplitMany(Ledger(("2026-10-01", "glm-4", 1000)),
            new[]
            {
                ("A", "网关A", Ledger(("2026-10-01", "glm-4", 1100)), ModelScope.Empty, (DateTime?)covered, 0),
                ("B", "网关B", Ledger(("2026-10-01", "glm-4", 9999)), ModelScope.Empty, (DateTime?)covered, 0),
            },
            ModelScope.Empty);
        var sum3 = a3.Sum(a => a.Ledger.TotalTokens());
        Check("重叠模型两个接口都不拿", sum3 == 0, $"sum={sum3}");
        Check("重叠被记进冲突清单", i3.Conflicts.Contains("glm-4"), string.Join(",", i3.Conflicts));
        Check("冲突说明点名了模型", i3.ConflictNote.Contains("glm-4"), i3.ConflictNote);
        Check("冲突说明点名了涉及的两个接口",
            i3.ConflictNote.Contains("网关A") && i3.ConflictNote.Contains("网关B"), i3.ConflictNote);
        Check("冲突模型回落到本地块：数字不会凭空消失",
            i3.MovedToServer.Count == 0, string.Join(",", i3.MovedToServer));

        var (l3b, _, _) = LedgerSplitter.SplitMany(Ledger(("2026-10-01", "glm-4", 1000)),
            new[]
            {
                ("A", "网关A", Ledger(("2026-10-01", "glm-4", 1100)), ModelScope.Empty, (DateTime?)covered, 0),
                ("B", "网关B", Ledger(("2026-10-01", "glm-4", 9999)), ModelScope.Empty, (DateTime?)covered, 0),
            },
            ModelScope.Empty);
        Check("冲突模型确实留在本地块里", l3b.Days["2026-10-01"]["glm-4"].Total == 1000, "没留本地");

        // 三接口里只有一对冲突，第三个不受牵连
        var (_, a3c, i3c) = LedgerSplitter.SplitMany(Ledger(("2026-10-01", "glm-4", 100)),
            new[]
            {
                ("A", "网关A", Ledger(("2026-10-01", "glm-4", 1100)), ModelScope.Empty, (DateTime?)covered, 0),
                ("B", "网关B", Ledger(("2026-10-01", "glm-4", 9999)), ModelScope.Empty, (DateTime?)covered, 0),
                ("C", "网关C", Ledger(("2026-10-01", "deepseek-v4", 500)), ModelScope.Empty, (DateTime?)covered, 0),
            },
            ModelScope.Empty);
        Check("不冲突的第三个接口照常拿到自己的模型",
            a3c[2].Ledger.Days["2026-10-01"]["deepseek-v4"].Total == 500, "C");
        Check("只把真正重叠的那个模型标成冲突",
            i3c.Conflicts.Count == 1 && i3c.Conflicts[0] == "glm-4", string.Join(",", i3c.Conflicts));

        // 同一接口跨天报同一模型，不能被误判成冲突
        var multiDay = Ledger(("2026-10-01", "glm-4", 100), ("2026-10-02", "glm-4", 200));
        var (_, a4, i4) = LedgerSplitter.SplitMany(Ledger(),
            new[] { ("A", "网关A", multiDay, ModelScope.Empty, (DateTime?)null, 0) }, ModelScope.Empty);
        Check("同一接口跨天报同一模型不算冲突", i4.Conflicts.Count == 0, string.Join(",", i4.Conflicts));
        Check("跨天的两天都进了该接口账本", a4[0].Ledger.Days.Count == 2, $"days={a4[0].Ledger.Days.Count}");

        // 各接口的模型范围独立生效；范围不匹配的不算进「想要」，因此也不构成冲突
        var (_, a5, i5) = LedgerSplitter.SplitMany(Ledger(),
            new[]
            {
                ("A", "网关A", Ledger(("2026-10-01", "glm-4", 100)), ModelScope.Parse("glm-"), (DateTime?)covered, 0),
                ("B", "网关B", Ledger(("2026-10-01", "glm-4", 700)), ModelScope.Parse("kimi"), (DateTime?)covered, 0),
            },
            ModelScope.Empty);
        Check("范围不匹配的接口拿不到该模型", !a5[1].Ledger.Days.ContainsKey("2026-10-01"), "B scope");
        Check("范围匹配的接口照常拿到", a5[0].Ledger.Days["2026-10-01"]["glm-4"].Total == 100, "A scope");
        Check("被范围挡掉的不算重叠", i5.Conflicts.Count == 0, string.Join(",", i5.Conflicts));

        // 低估值逐接口判定
        var uLocal = Ledger(("2026-10-01", "glm-4", 1000), ("2026-10-01", "kimi-k2", 1000));
        var (_, a6, i6) = LedgerSplitter.SplitMany(uLocal,
            new[]
            {
                ("A", "网关A", Ledger(("2026-10-01", "glm-4", 300)), ModelScope.Empty, (DateTime?)covered, 10),
                ("B", "网关B", Ledger(("2026-10-01", "kimi-k2", 1000)), ModelScope.Empty, (DateTime?)covered, 10),
            },
            ModelScope.Empty);
        Check("报少的接口被单独标低估值", a6[0].Undercount, "A");
        Check("报足的接口不被连带误标", !a6[1].Undercount, "B");
        Check("整体告警位为真且带出差值", i6.Undercount && i6.UndercountDetail.Contains("少 70%"), i6.UndercountDetail);
    }

    // ---------------- 存储层迁移 ----------------

    private static void RegistryTests()
    {
        Console.WriteLine("== 接口配置存储 ==");

        var empty = ProviderRegistry.Parse(null);
        Check("空配置是空列表，不算解析失败", empty is not null && empty.Items.Count == 0, "empty");

        var legacy = "{\"Enabled\":true,\"DisplayName\":\"我的网关\",\"BaseUrl\":\"https://x.example\","
            + "\"Path\":\"/api/log/self\",\"ModelScope\":\"deepseek-\",\"Currency\":\"$\"}";
        var l1 = ProviderRegistry.Parse(legacy);
        Check("旧的单对象形态被包成一条", l1 is not null && l1.Items.Count == 1, "count");
        if (l1 is not null)
        {
            var one = l1.Items[0];
            Check("迁移不会静默关掉用户已启用的接口", one.Enabled, "Enabled 丢了");
            Check("迁移保留显示名", one.DisplayName == "我的网关", one.DisplayName);
            Check("迁移保留路径与模型范围", one.Path == "/api/log/self" && one.ModelScope == "deepseek-", one.Path);
            Check("包进来的一条拿到 id", !string.IsNullOrWhiteSpace(one.Id), "没拿到 id");
        }

        var arr = "[{\"DisplayName\":\"A\",\"Enabled\":true},{\"DisplayName\":\"B\"}]";
        var a1 = ProviderRegistry.Parse(arr);
        Check("数组形态读出两条", a1 is not null && a1.Items.Count == 2, "count");
        if (a1 is not null)
        {
            Check("两条各自拿到不同 id", a1.Items[0].Id != a1.Items[1].Id, "id 撞了");
            var round = ProviderRegistry.Parse(a1.ToJson());
            Check("序列化再读回仍是两条", round is not null && round.Items.Count == 2, "roundtrip");
            Check("往返后 id 不变",
                round is not null && round.Items[0].Id == a1.Items[0].Id && round.Items[1].Id == a1.Items[1].Id, "id 变了");
        }

        // 坏值必须返回 null：调用方据此拒绝写回。整键覆盖写回时把默认值当解析结果，就等于抹掉真配置。
        Check("语法错的 JSON 判为不可读", ProviderRegistry.Parse("{ 这不是 JSON") is null, "不该算成功");
        Check("既非数组也非对象的值判为不可读", ProviderRegistry.Parse("123") is null, "不该算成功");
        Check("空数组是合法空列表而不是失败", ProviderRegistry.Parse("[]") is { Items.Count: 0 }, "[] 应当成功");

        var dup = "[{\"Id\":\"same\",\"DisplayName\":\"A\"},{\"Id\":\"same\",\"DisplayName\":\"B\"}]";
        var d1 = ProviderRegistry.Parse(dup);
        Check("重复 id 被自动改掉（同 id 的两份账本会互相覆盖）",
            d1 is not null && d1.Items[0].Id != d1.Items[1].Id, "dup 没解决");

        // 这条锁住那个坑本身：ProviderConfig.Parse 遇到数组会静默回默认值（Enabled=false）。
        // 所以设置页里任何还直接调它的写点都必须改走 ProviderRegistry，
        // 否则用户碰一下「完整配置 JSON」框就把其它接口全抹了。
        var trap = ProviderConfig.Parse(arr);
        Check("ProviderConfig.Parse 读数组会静默回默认值（已知陷阱，调用点必须走 Registry）",
            !trap.Enabled && string.IsNullOrEmpty(trap.BaseUrl), "行为变了，重新评估所有调用点");

        // id 必须跨启动稳定：随机 id 会让云端账本每次重启都挂在一把对不上的钥匙下
        var again = ProviderRegistry.Parse(legacy);
        Check("同一份老配置两次解析得到同一个 id",
            l1 is not null && again is not null && l1.Items[0].Id == again.Items[0].Id,
            $"{l1?.Items[0].Id} vs {again?.Items[0].Id}");
        Check("老配置固定用 legacy-primary 这个 id",
            again is not null && again.Items[0].Id == ProviderRegistry.LegacyId, again?.Items[0].Id ?? "null");
        Check("标记出这是从单个对象迁移来的", again is { FromLegacyObject: true }, "没标记");

        var noIdArr = "[{\"DisplayName\":\"A\"},{\"DisplayName\":\"B\"}]";
        var s1 = ProviderRegistry.Parse(noIdArr);
        var s2 = ProviderRegistry.Parse(noIdArr);
        Check("数组里缺 id 的条目按位置得到稳定 id",
            s1 is not null && s2 is not null
            && s1.Items[0].Id == s2.Items[0].Id && s1.Items[1].Id == s2.Items[1].Id
            && s1.Items[0].Id != s1.Items[1].Id,
            s1 is null ? "null" : $"{s1.Items[0].Id}/{s1.Items[1].Id}");

        var explicitId = "[{\"Id\":\"mine-1\",\"DisplayName\":\"A\"}]";
        var e1 = ProviderRegistry.Parse(explicitId);
        Check("已有显式 id 的不被改动", e1 is not null && e1.Items[0].Id == "mine-1", e1?.Items[0].Id ?? "null");
        Check("显式 id 的条目不算老配置迁移", e1 is { FromLegacyObject: false }, "不该标 legacy");

        // ---- 注释容忍与示例模板 ----
        var jsonc = "[\n  // 行注释\n  {\"DisplayName\":\"A\",\"ModelScope\":\"x\",},\n]";
        var jc = ProviderRegistry.Parse(jsonc);
        Check("带 // 注释与尾逗号的配置能读进来",
            jc is not null && jc.Items.Count == 1 && jc.Items[0].DisplayName == "A" && jc.Items[0].ModelScope == "x",
            jc is null ? "被判成不可读了" : "count=" + jc.Items.Count);

        var tpl = ProviderRegistry.Parse(ProviderRegistry.SampleTemplate);
        Check("示例模板能被插件自己读通（别写个人家解析不了的文件）",
            tpl is not null && tpl.Items.Count == 2, $"模板读不通或条目数变了：count={tpl?.Items.Count}");
        if (tpl is { Items.Count: 2 })
        {
            Check("模板里的接口有地址与路径",
                !string.IsNullOrWhiteSpace(tpl.Items[0].BaseUrl) && !string.IsNullOrWhiteSpace(tpl.Items[0].Path),
                tpl.Items[0].BaseUrl);
            Check("模板自己说明了密钥只填环境变量名",
                ProviderRegistry.SampleTemplate.Contains("环境变量名"), "缺密钥说明");
            Check("模板第二份是余额型示范，且默认关着（不能一生成就去联网）",
                tpl.Items[1].IsBalance && !tpl.Items[1].Enabled, $"kind={tpl.Items[1].Kind} enabled={tpl.Items[1].Enabled}");
            Check("模板第一份是按天用量型",
                !tpl.Items[0].IsBalance && tpl.Items[0].Enabled, tpl.Items[0].Kind);
            Check("两个示例接口拿到不同的自动 id（同 id 会让两份账本互相覆盖）",
                tpl.Items[0].Id != tpl.Items[1].Id && !string.IsNullOrWhiteSpace(tpl.Items[0].Id),
                $"{tpl.Items[0].Id}/{tpl.Items[1].Id}");

            var props = typeof(ProviderConfig).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            using var doc = System.Text.Json.JsonDocument.Parse(ProviderRegistry.SampleTemplate,
                new System.Text.Json.JsonDocumentOptions
                {
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
            var written = new List<string>();
            foreach (var obj in doc.RootElement.EnumerateArray())
                foreach (var o in obj.EnumerateObject()) written.Add(o.Name);

            var bogus = written.Where(n => !props.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Check("模板里没有 ProviderConfig 不认识的字段", bogus.Count == 0, string.Join(",", bogus));

            // 反方向也查：以前只能查出「写了不存在的字段」，查不出「加了字段忘了写进示例」，
            // 而后者才是真会发生的（配置文件是用户唯一的接口入口，看不见的字段等于没有）
            var writable = typeof(ProviderConfig).GetProperties()
                .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
                .Select(p => p.Name).ToList();
            var missing = writable.Where(n => !written.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            Check("ProviderConfig 的每个可写字段都在示例里出现过（加字段必须同步补模板）",
                missing.Count == 0, "示例里看不到：" + string.Join(",", missing));

            // 注释才是这个文件存在的理由：只有字段名没有说明，第一次用的人照样配不出来。
            // 判据用 " // "（前后都有空格）而不是 "//"：BaseUrl 的值里就带 https://，
            // 用裸 "//" 会把每个接口地址都误判成"已有注释"。
            var fieldLines = ProviderRegistry.SampleTemplate.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("\"") && l.Contains(':'))
                .ToList();
            var uncommented = fieldLines.Where(l => !l.Contains(" // ")).ToList();
            Check("示例里每一行配置都跟着说明（这是用户唯一的接口入口）",
                fieldLines.Count > 0 && uncommented.Count == 0,
                $"共 {fieldLines.Count} 行，没说明的：" + string.Join(" | ", uncommented));
        }
    }

    // ---------------- 阈值告警（今日预算 / 余额预警） ----------------

    /// <summary>
    /// 告警会咬人的地方全在"什么时候不该响"：预算线手抖填成 0 或负数不能变成"任何用量都超"，
    /// 未定价时金额是 0 不该报超预算，余额刚好等于预警线按口径不该响。
    /// </summary>
    private static void AlertTests()
    {
        var (offTag, offDetail) = SourceAlert.ForBudget(999m, 0, "我的网关", "$");
        Check("预算线为 0 = 不启用（不能变成任何用量都超）", offTag.Length == 0 && offDetail.Length == 0, offTag);

        Check("填成负数、NaN、无穷都当关闭，不是「一直告警」",
            SourceAlert.ForBudget(1m, -50, "x", "$").Tag.Length == 0
            && SourceAlert.ForBudget(1m, double.NaN, "x", "$").Tag.Length == 0
            && SourceAlert.ForBudget(1m, double.PositiveInfinity, "x", "$").Tag.Length == 0
            && SourceAlert.ForBalance(1m, -1, "x", "$").Tag.Length == 0, "有一个还在响");

        var under = SourceAlert.ForBudget(199.9m, 200, "本地日志", "¥");
        Check("没到预算不响", under.Tag.Length == 0 && under.Detail.Length == 0, under.Tag);

        var hit = SourceAlert.ForBudget(200m, 200, "本地日志", "¥");
        Check("达到预算就响（等到「超过」才提示就太晚了）", hit.Tag == "超预算", $"tag={hit.Tag}");
        Check("告警句子带上来源、当前金额与预算线",
            hit.Detail.Contains("本地日志") && hit.Detail.Contains("¥200") && hit.Detail.Contains("200"),
            hit.Detail);

        Check("未定价时金额是 0，不该报超预算",
            SourceAlert.ForBudget(0m, 100, "我的网关", "$").Tag.Length == 0, "没配单价就天天告警");

        var cur = SourceAlert.ForBudget(0.5m, 0.4, "我的网关", "$");
        Check("云端来源用它自己的币种，不是本地那个 ¥",
            cur.Detail.Contains("$") && !cur.Detail.Contains("¥"), cur.Detail);

        var eq = SourceAlert.ForBalance(5m, 5, "钱包", "$");
        Check("余额等于预警线不算「低于」，不响", eq.Tag.Length == 0, eq.Tag);
        var low = SourceAlert.ForBalance(4.99m, 5, "钱包", "$");
        Check("余额低于预警线才响", low.Tag == "余额偏低" && low.Detail.Contains("低于预警线"), $"tag={low.Tag} {low.Detail}");
        Check("余额为 0 当然要响（清零是最该提示的一刻）",
            SourceAlert.ForBalance(0m, 5, "钱包", "$").Tag == "余额偏低", "被当成没数据跳过了");

        // 视图读的是快照顶层字段，主块的告警必须被 CopyBlock 带上去
        var block = SnapshotBuilder.BuildBlock(new UsageLedger(),
            PricingConfig.Parse("{\"Currency\":\"¥\",\"PerTokens\":1000000,\"Models\":[]}"), "本地日志");
        block.AlertTag = "超预算";
        var snap = new Snapshot();
        SnapshotBuilder.CopyBlock(snap, block);
        Check("主块的告警标签会拷到快照顶层（紧凑态读的就是顶层）", snap.AlertTag == "超预算", snap.AlertTag);
        Check("没有告警时标签是空串而不是 null（视图直接拼字符串）",
            new SourceBlock().AlertTag.Length == 0 && new Snapshot().AlertTag.Length == 0, "默认值不对");
    }

    // ---------------- 近 7 天单日汇总 ----------------

    /// <summary>
    /// DayPoint 是把「一天里所有模型」压成一个点给柱状图用的，所以跨模型的金额必须是累加。
    /// 这条用例是本地假接口实机撞出来的：那个多模型来源一天报了 0.012 + 0.004，
    /// 图上的金额却只有 0.004 —— 写成赋值时最后一个模型把前面全覆盖了，
    /// 而单模型账号（只有一个 deepseek 的场合）完全看不出来。
    /// </summary>
    private static void DayPointTests()
    {
        var today = Format.DayKey(DateOnly.FromDateTime(DateTime.Now));
        var l = new UsageLedger();
        l.Add(today, "m-a", new Counters { Input = 1200, Output = 300, Total = 1500, Src = 1, CostUnits = 1200000 });
        l.Add(today, "m-b", new Counters { Input = 400, Output = 80, Total = 480, Src = 1, CostUnits = 400000 });

        var pricing = PricingConfig.Parse("{\"Currency\":\"\u00a5\",\"PerTokens\":1000000,\"Models\":[]}");
        var p = SnapshotBuilder.BuildBlock(l, pricing, "测试").Last7[^1];

        Check("同一天多个模型的服务端金额必须累加（赋值会被最后一个覆盖）",
            Math.Abs((double)p.ServerCost - 0.016) < 1e-9, $"ServerCost={p.ServerCost}");
        Check("同一天多个模型的 token 也累加", p.Total == 1980, $"Total={p.Total}");
        Check("带服务端金额的多模型不算「未定价」", !p.HasUnpriced, "被标成未定价了");

        var one = new UsageLedger();
        one.Add(today, "m-only", new Counters { Input = 100, Total = 100, Src = 1, CostUnits = 700000 });
        var p1 = SnapshotBuilder.BuildBlock(one, pricing, "测试").Last7[^1];
        Check("单模型时行为和改之前一致（这条保证没有回归）",
            Math.Abs((double)p1.ServerCost - 0.007) < 1e-9, $"ServerCost={p1.ServerCost}");
    }

    // ---------------- 岛面展开态的行数分配 ----------------

    /// <summary>
    /// 岛面展开高度是个硬预算，而来源数由用户随便加。这段算术从视图里抽出来就是为了能这样查：
    /// 「接到第 8 个接口时岛面会不会溢出一截」不能靠人眼在展开态上一格一格数。
    /// </summary>
    private static void IslandLayoutTests()
    {
        var noSource = IslandLayout.Plan(0);
        Check("没有云端来源时高度就是基线值",
            noSource.DetailHeight == IslandLayout.BaseHeight
            && noSource.ModelRows == IslandLayout.MaxModelRows && noSource.OtherRowsToDraw == 0,
            $"h={noSource.DetailHeight} m={noSource.ModelRows} o={noSource.OtherRowsToDraw}");

        var one = IslandLayout.Plan(1);
        Check("一个来源：三行模型明细一行来源（和不接云端时同高的算法一致）",
            one.ModelRows == 3 && one.OtherRowsToDraw == 1 && one.OtherHidden == 0,
            $"m={one.ModelRows} o={one.OtherRowsToDraw}");

        var three = IslandLayout.Plan(3);
        Check("三个来源：模型明细让到只剩一行，来源各占一行",
            three.ModelRows == 1 && three.OtherRowsToDraw == 3 && three.OtherHidden == 0,
            $"m={three.ModelRows} o={three.OtherRowsToDraw} h={three.DetailHeight}");

        var six = IslandLayout.Plan(6);
        Check("六个来源：折成一句提示，而不是把岛撑高",
            six.OtherRowsToDraw == 2 && six.OtherHidden == 4
            && six.DetailHeight == three.DetailHeight,
            $"drawn={six.OtherRowsToDraw} hidden={six.OtherHidden} h={six.DetailHeight} vs {three.DetailHeight}");

        // 核心不变式：来源再多，展开高度与行数都不能涨
        var maxHeight = 0d;
        var maxOtherLines = 0;
        var minModelRows = 99;
        for (var n = 0; n <= 40; n++)
        {
            var p = IslandLayout.Plan(n);
            if (p.DetailHeight > maxHeight) maxHeight = p.DetailHeight;
            if (p.OtherRowsToDraw + (p.OtherHidden > 0 ? 1 : 0) > maxOtherLines)
                maxOtherLines = p.OtherRowsToDraw + (p.OtherHidden > 0 ? 1 : 0);
            if (n > 0 && p.ModelRows < minModelRows) minModelRows = p.ModelRows;
            if (p.OtherRowsToDraw + p.OtherHidden != n)
            {
                Check($"来源数对得上（n={n}）", false, $"{p.OtherRowsToDraw}+{p.OtherHidden}≠{n}");
                return;
            }
        }
        Check("40 个来源里最大展开高度也不超过基线 + 一行",
            maxHeight <= IslandLayout.BaseHeight + IslandLayout.RowHeight + 0.001,
            $"max={maxHeight} vs {IslandLayout.BaseHeight + IslandLayout.RowHeight}");
        Check("来源区最多占 MaxOtherLines 行（含那句提示）",
            maxOtherLines <= IslandLayout.MaxOtherLines, $"实际 {maxOtherLines} 行");
        Check("逐模型明细永远至少留一行（不能全被云端挤没）",
            minModelRows >= 1, $"最少时 {minModelRows} 行");

        var negative = IslandLayout.Plan(-5);
        Check("来源数为负当 0 处理（视图传错了不该算出负高度）",
            negative.DetailHeight == IslandLayout.BaseHeight && negative.OtherHidden == 0,
            $"h={negative.DetailHeight} hidden={negative.OtherHidden}");

        // ---- 谁占那几行：按接口文件里的书写顺序 ----
        // byId 故意用和顺序表不同的插入顺序建，这样"结果按谁排"才测得出来
        var local = new SourceBlock { Name = "本地日志" };
        var byId = new Dictionary<string, SourceBlock>(StringComparer.Ordinal)
        {
            ["c-balance"] = new SourceBlock { Name = "余额", SummaryValue = 1m },
            ["c-usage2"] = new SourceBlock { Name = "第二家用量" },
            ["c-usage1"] = new SourceBlock { Name = "第一家用量" },
        };
        var ordered = SnapshotBuilder.OrderBlocks(
            new[] { "c-usage1", "c-balance", "c-usage2" }, local, byId);

        Check("本地块永远在第一位（视图拿 Blocks[0] 当主块显示岛面标题）",
            ordered.Count == 4 && ordered[0].Name == "本地日志",
            string.Join(",", ordered.Select(x => x.Name)));
        Check("其余按配置文件里的书写顺序排，余额不再被挤到最后",
            ordered[1].Name == "第一家用量" && ordered[2].Name == "余额" && ordered[3].Name == "第二家用量",
            string.Join(",", ordered.Select(x => x.Name)));

        var withDead = SnapshotBuilder.OrderBlocks(
            new[] { "c-off", "c-balance", "c-usage1" }, local, byId);
        Check("没拿到数据的接口跳过：不报错、也不画空块占位",
            withDead.Count == 3 && withDead[1].Name == "余额" && withDead[2].Name == "第一家用量",
            string.Join(",", withDead.Select(x => x.Name)));

        Check("顺序表为空时只剩本地块（所有接口都关掉的那一轮）",
            SnapshotBuilder.OrderBlocks(Array.Empty<string>(), local, byId).Count == 1, "多了东西");
    }

    // ---------------- 余额/汇总型接口 ----------------

    /// <summary>
    /// Kind=balance 的配置层口径。视图与取数的用例在 ProviderTests 那边跑真接口，
    /// 这里锁的是「什么算配好了」和「余额绝不进按天账本」这两条规则。
    /// </summary>
    private static void BalanceTests()
    {
        var usage = new ProviderConfig { Enabled = true, BaseUrl = "https://a", Path = "/x", BalanceField = "b" };
        Environment.SetEnvironmentVariable("WL_BAL_KEY", "v");
        usage.ApiKeyEnvVar = "WL_BAL_KEY";
        Check("Kind 默认就是按天用量型", !usage.IsBalance, usage.Kind);

        var typed = new ProviderConfig
        {
            Enabled = true, BaseUrl = "https://a", Path = "/x",
            ApiKeyEnvVar = "WL_BAL_KEY", BalanceField = "data.balance",
        };
        typed.Kind = "  BALANCE  ";
        Check("Kind 认大小写与首尾空格", typed.IsBalance, $"「{typed.Kind}」没被认成 balance");
        Check("余额型接口的就绪提示里没有「未就绪」", typed.IsUsable(out _), "配齐了却报未就绪");

        var noField = new ProviderConfig
        {
            Enabled = true, BaseUrl = "https://a", Path = "/x", ApiKeyEnvVar = "WL_BAL_KEY",
            Kind = "balance", BalanceField = "",
        };
        Check("余额型没填取值字段要说 BalanceField（不然只能看到「拉不到数」）",
            !noField.IsUsable(out var why1) && why1.Contains("BalanceField"), why1);

        var typo = new ProviderConfig
        {
            Enabled = true, BaseUrl = "https://a", Path = "/x", ApiKeyEnvVar = "WL_BAL_KEY",
            Kind = "banlance", BalanceField = "data.balance",
        };
        Check("Kind 拼错必须报错，不能悄悄当 usage 跑",
            !typo.IsUsable(out var why2) && why2.Contains("不认识"), why2);
        Check("Kind 拼错的提示里给出两个合法值",
            why2.Contains("usage") && why2.Contains("balance"), why2);

        Check("摘要里标明这是余额接口、用量接口仍标用量",
            typed.SafeSummary().Contains("｜余额｜") && usage.SafeSummary().Contains("｜用量｜"),
            typed.SafeSummary() + " || " + usage.SafeSummary());

        // 余额的存放位置本身就是设计决定：它不是任何一天的用量
        var st = new ProviderState
        {
            LatestBalance = 249.9,
            BalanceAtUtc = new DateTime(2026, 10, 14, 3, 4, 5, DateTimeKind.Utc),
        };
        var back = ProviderState.Parse(st.ToJson());
        Check("余额读数能存能取回来", back.LatestBalance is > 249.89 and < 249.91, $"{back.LatestBalance}");
        Check("余额带着它是几点看的", back.BalanceAtUtc == st.BalanceAtUtc, $"{back.BalanceAtUtc}");
        Check("余额没有混进按天账本", !back.HasPersistedLedger, "ServerDays 里冒出东西了");

        var fresh = ProviderState.Parse("{}");
        Check("旧状态文件没有这两个字段时读成 null，不是 0",
            fresh.LatestBalance is null && fresh.BalanceAtUtc is null, $"{fresh.LatestBalance}");

        var zero = new SourceBlock { Currency = "$", SummaryValue = 0m };
        Check("余额 0 也是有效读数（区别于「没取到」）", zero.IsSummary, "0 被当成没有余额了");
        Check("没有余额读数的来源不算汇总型", !new SourceBlock().IsSummary, "默认值就被认成余额了");
    }
}
