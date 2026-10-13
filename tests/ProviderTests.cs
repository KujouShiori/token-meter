using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using TokenMeter;

namespace wlverify;

/// <summary>
/// 用本地假接口验证 ProviderClient / LedgerMerger / PlanWindows 的真实行为。
/// 目的不是演示，是把「分页翻没翻、缓存有没有被重复计、权限不足会不会降级、
/// 服务端少给数会不会告警」这四件事跑出来看。
/// </summary>
public static class ProviderTests
{
    private static int _failures;

    public static async Task<int> RunAsync()
    {
        _failures = 0;
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        var seen = Seen;
        seen.Clear();
        SeenHeaders.Clear();

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        var loop = Task.Run(() => ServeAsync(listener, seen));

        try
        {
            await CasePaginationAndMappingAsync(prefix);
            CaseUnauthorized(prefix);
            CaseBadItemsPath(prefix);
            await CaseAnthropicShapeAsync(prefix);
            await CaseNewCapabilitiesAsync(prefix);
            await CaseNewApiShapeAsync(prefix);
            await CaseBalanceAsync(prefix);
            CaseMovedRequestPath(prefix);
            CaseSplitter();
            CaseServerLedgerPersistence();
            CasePlanWindows();
            CaseMerge();
        }
        finally
        {
            listener.Stop();
            try { await loop; } catch (Exception) { }
        }

        Console.WriteLine(_failures == 0 ? "PROVIDER_TESTS=all passed" : $"PROVIDER_TESTS={_failures} FAILED");
        return _failures;
    }

    private static ProviderConfig BaseCfg(string prefix) => new()
    {
        Enabled = true,
        BaseUrl = prefix.TrimEnd('/'),
        Path = "/usage",
        ApiKeyEnvVar = "WL_FAKE_KEY",
        TimeoutSeconds = 10,
    };

    private static async Task CasePaginationAndMappingAsync(string prefix)
    {
        Environment.SetEnvironmentVariable("WL_FAKE_KEY", "testkey-should-not-appear-in-output");
        var cfg = BaseCfg(prefix);

        using var client = new ProviderClient(cfg.TimeoutSeconds);
        var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
        var r = await client.FetchAsync(cfg, from, to, CancellationToken.None);

        Check("翻了两页", r.Pages == 2, $"pages={r.Pages}");

        // 三条记录：10-01 两条（同模型，应累加）、10-02 一条
        var d1 = r.Ledger.Days.TryGetValue("2026-10-01", out var m1) ? m1 : null;
        Check("按本地日期归桶 10-01", d1 is not null, d1 == null ? "missing" : "ok");
        if (d1 is not null)
        {
            var c = d1["M-A"];
            // 每条 item：input 1000 含 cached 400 → uncached 600；output 200；两条累加
            Check("缓存从输入里减出来了（uncached=1200）", c.Input == 1200, $"Input={c.Input}");
            Check("缓存读单独计（=800）", c.CacheRead == 800, $"CacheRead={c.CacheRead}");
            Check("输出累加（=400）", c.Output == 400, $"Output={c.Output}");
            Check("合计不重复计缓存（=2400）", c.Total == 2400, $"Total={c.Total}");
        }

        var d2 = r.Ledger.Days.TryGetValue("2026-10-02", out var m2) ? m2 : null;
        Check("第二页的数据也进来了", d2 is not null && d2["M-B"].Total == 300,
            d2 is null ? "missing" : d2["M-B"].Total.ToString(CultureInfo.InvariantCulture));
    }

    private static void CaseUnauthorized(string prefix)
    {
        const string secret = "SECRET-DO-NOT-LEAK";
        Environment.SetEnvironmentVariable("WL_FAKE_KEY", secret);
        var cfg = BaseCfg(prefix);
        cfg.Path = "/denied";
        using var client = new ProviderClient(cfg.TimeoutSeconds);
        try
        {
            client.FetchAsync(cfg, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, CancellationToken.None)
                  .GetAwaiter().GetResult();
            Check("401 要抛 ProviderException", false, "没抛");
        }
        catch (ProviderException ex)
        {
            Check("401 被识别为权限类永久失败", ex.Permanent, $"permanent={ex.Permanent}");
            Check("错误文案提到 Key 覆盖范围", ex.Message.Contains("Key"), ex.Message);
            Check("错误文案不泄漏密钥值", !ex.Message.Contains(secret), "密钥出现在异常里");
        }
        catch (Exception ex)
        {
            Check("401 异常类型", false, ex.GetType().Name);
        }
    }

    private static void CaseBadItemsPath(string prefix)
    {
        Environment.SetEnvironmentVariable("WL_FAKE_KEY", "k");
        var cfg = BaseCfg(prefix);
        cfg.Path = "/badshape";
        cfg.ItemsPath = "nope";
        using var client = new ProviderClient(cfg.TimeoutSeconds);
        try
        {
            client.FetchAsync(cfg, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, CancellationToken.None)
                  .GetAwaiter().GetResult();
            Check("找不到数组要报错", false, "没抛");
        }
        catch (ProviderException ex)
        {
            Check("结构不匹配提示改 ItemsPath", ex.Message.Contains("ItemsPath"), ex.Message);
        }
        catch (Exception ex)
        {
            Check("结构异常类型", false, ex.GetType().Name);
        }
    }

    private static void CasePlanWindows()
    {
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var cfg = new ProviderConfig { WindowMaxDays = 3, LagMinutes = 0, InitialBackfillDays = 10, MaxWindowsPerRun = 50 };

        var plan = ProviderClient.PlanWindows(null, now, cfg);
        Check("首次回溯 10 天切成 4 段", plan.Count == 4, $"count={plan.Count}");
        Check("最后一段正好停在 trustUntil", plan.Count > 0 && plan[^1].To == now, "tail");
        Check("段与段首尾相接", plan.Count == 4 && plan[0].To == plan[1].From, "seam");

        cfg.MaxWindowsPerRun = 2;
        var capped = ProviderClient.PlanWindows(null, now, cfg);
        Check("MaxWindowsPerRun 限流生效", capped.Count == 2 && capped[^1].To == now.AddDays(-4),
            $"count={capped.Count} tail={capped[^1].To:MM-dd HH:mm}Z");

        cfg.MaxWindowsPerRun = 50;
        var resume = ProviderClient.PlanWindows(now.AddDays(-2), now, cfg);
        Check("有水位线时只追缺口", resume.Count == 1 && resume[0].From == now.AddDays(-2),
            $"count={resume.Count}");

        var ahead = ProviderClient.PlanWindows(now.AddDays(1), now, cfg);
        Check("水位线超前时不发请求", ahead.Count == 0, $"count={ahead.Count}");

        cfg.LagMinutes = 180;
        var lagged = ProviderClient.PlanWindows(null, now, cfg);
        Check("滞后带把右端点往前推", lagged.Count > 0 && lagged[^1].To == now.AddMinutes(-180),
            lagged.Count > 0 ? lagged[^1].To.ToString("HH:mm") + "Z" : "empty");
    }

    private static void CaseMerge()
    {
        var local = new UsageLedger();
        var server = new UsageLedger();
        for (var d = 1; d <= 5; d++)
        {
            var key = $"2026-10-0{d}";
            local.Add(key, "X", new Counters { Input = 1000, Total = 1000 });
            if (d <= 3) server.Add(key, "X", new Counters { Input = 900, Total = 900 });
        }

        var covered = new DateTime(2026, 10, 3, 4, 0, 0, DateTimeKind.Utc);   // 本地 10-03 12:00
        var (eff, info) = LedgerMerger.Merge(local, server, covered, 10);

        Check("覆盖内用服务端", eff.Days["2026-10-01"]["X"].Total == 900, eff.Days["2026-10-01"]["X"].Total.ToString());
        Check("覆盖外用本地", eff.Days["2026-10-05"]["X"].Total == 1000, eff.Days["2026-10-05"]["X"].Total.ToString());
        Check("混合口径被标出来", info.Mode.Contains("混合"), info.Mode);
        Check("服务端少给 10% 触发告警", info.Undercount, $"detail={info.UndercountDetail}");

        var (eff2, info2) = LedgerMerger.Merge(local, null, null, 10);
        Check("服务端为 null 时完全用本地", eff2.Days["2026-10-01"]["X"].Total == 1000 && info2.Mode.Contains("本地"), info2.Mode);
    }

    /// <summary>
    /// 拿官方文档里那段真实结构当样本：两层数组、时间戳在外层桶上、
    /// uncached/cached 分开、缓存写分 5m+1h 两档、鉴权走 x-api-key + anthropic-version。
    /// </summary>
    private static async Task CaseAnthropicShapeAsync(string prefix)
    {
        Environment.SetEnvironmentVariable("WL_FAKE_KEY", "sk-ant-admin01-fake");
        var cfg = new ProviderConfig
        {
            Enabled = true,
            BaseUrl = prefix.TrimEnd('/'),
            Path = "/usage_report",
            AuthHeader = "x-api-key",
            AuthPrefix = "",
            ExtraHeaders = "anthropic-version=2023-06-01",
            ApiKeyEnvVar = "WL_FAKE_KEY",
            StartParam = "starting_at",
            EndParam = "ending_at",
            TimeFormat = "rfc3339",
            ExtraQuery = "bucket_width=1d",
            ItemsPath = "data[].results",
            TimeField = "starting_at",
            ModelField = "model",
            InputField = "uncached_input_tokens",
            OutputField = "output_tokens",
            CachedInputField = "cache_read_input_tokens",
            CacheWriteField = "cache_creation.ephemeral_5m_input_tokens,cache_creation.ephemeral_1h_input_tokens",
            InputIncludesCached = false,
            PageTokenField = "next_page",
            PageParam = "page",
            HasMoreField = "has_more",
            TimeoutSeconds = 10,
        };

        using var client = new ProviderClient(cfg.TimeoutSeconds);
        var from = new DateTime(2025, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2025, 8, 3, 0, 0, 0, DateTimeKind.Utc);
        var r = await client.FetchAsync(cfg, from, to, CancellationToken.None);

        Check("两层数组结枛能解析", r.Ledger.Days.Count > 0, "empty");
        Check("时间戳从外层时间桶取到", r.Ledger.Days.TryGetValue("2025-08-01", out var m1),
            string.Join(",", r.Ledger.Days.Keys));
        if (m1 is not null)
        {
            var c = m1["claude-opus-5"];
            Check("InputIncludesCached=false 时输入不減缓存（=1500）", c.Input == 1500, $"Input={c.Input}");
            Check("缓存读单独计（=200）", c.CacheRead == 200, $"CacheRead={c.CacheRead}");
            Check("输出（=500）", c.Output == 500, $"Output={c.Output}");
            Check("合计不重不漏（=2200）", c.Total == 2200, $"Total={c.Total}");
        }
        Check("第二页用 page= 参数继续翻", r.Pages == 2, $"pages={r.Pages}");
        if (r.Ledger.Days.TryGetValue("2025-08-02", out var m2))
        {
            var c2 = m2["claude-opus-5"];
            Check("缓存写 = 5m(3) + 1h(7) 两档求和（=10）", c2.CacheWrite == 10, $"CacheWrite={c2.CacheWrite}");
            Check("第二页合计（10+20+5+10=45）", c2.Total == 45, $"Total={c2.Total}");
        }
        else
        {
            Check("第二页的桶进了账本", false, "missing 2025-08-02");
        }
        Check("带了 x-api-key 鉴权头", SeenHeaders.Any(h => h.Contains("x-api-key=sk-ant-admin01-fake")), "missing");
        Check("带了 anthropic-version 额外头", SeenHeaders.Any(h => h.Contains("anthropic-version=2023-06-01")), "missing");
        Check("时间参数是 RFC3339", Seen.Any(q => q.Contains("starting_at=2025-08-01T00%3A00%3A00Z"))
              || Seen.Any(q => q.Contains("starting_at=2025-08-01T00:00:00Z")),
            string.Join(" | ", Seen.Take(3)));
    }

    /// <summary>新能力的用例：数组下标路径、ValueScale、200 但业务报错、明文 http 卡控、{key} 写进 URL。</summary>
    private static async Task CaseNewCapabilitiesAsync(string prefix)
    {
        Environment.SetEnvironmentVariable("WL_FAKE_KEY", "URLKEY-abc123");
        var baseUrl = prefix.TrimEnd('/');

        using (var doc = JsonDocument.Parse(
                   "{\"balance_infos\":[{\"total_balance\":\"3.5\"},{\"x\":1}],\"a\":{\"b\":{\"c\":42}}}"))
        {
            var ok1 = ProviderClient.TryNavigate(doc.RootElement, "balance_infos[0].total_balance", out var e1);
            Check("数组下标路径 balance_infos[0].total_balance", ok1 && e1.GetString() == "3.5", "取值失败");
            var ok2 = ProviderClient.TryNavigate(doc.RootElement, "a.b.c", out var e2);
            Check("普通点号路径仍可用 a.b.c", ok2 && e2.GetInt32() == 42, "取值失败");
            Check("越界下标返回 false", !ProviderClient.TryNavigate(doc.RootElement, "balance_infos[9].x", out _), "不该命中");
            Check("穿过标量继续取子字段返回 false", !ProviderClient.TryNavigate(doc.RootElement, "a.b.c.x", out _), "不该命中");
        }

        var cfgS = BaseCfg(baseUrl);
        cfgS.Path = "/scaled";
        cfgS.ValueScale = 0.0001;
        cfgS.PageTokenField = "";
        using (var c = new ProviderClient(cfgS.TimeoutSeconds))
        {
            var r = await c.FetchAsync(cfgS,
                new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);
            var cc = r.Ledger.Days["2026-10-01"]["M-S"];
            Check("ValueScale=0.0001 生效（1000000→100）", cc.Input == 100, $"Input={cc.Input}");
            Check("输出同样缩放（500000→50）", cc.Output == 50, $"Output={cc.Output}");
        }

        var cfgB = BaseCfg(baseUrl);
        cfgB.Path = "/bizerror";
        using (var c = new ProviderClient(cfgB.TimeoutSeconds))
        {
            try
            {
                await c.FetchAsync(cfgB, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, CancellationToken.None);
                Check("200 但业务报错要被识别", false, "没抛异常");
            }
            catch (ProviderException ex)
            {
                Check("200 但业务报错被识别并带出 msg",
                    ex.Message.Contains("业务层报错") && ex.Message.Contains("coding plan"), ex.Message);
            }
        }

        var cfgH = BaseCfg("http://example.invalid");
        using (var c = new ProviderClient(cfgH.TimeoutSeconds))
        {
            try
            {
                await c.FetchAsync(cfgH, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, CancellationToken.None);
                Check("明文 http 要被拒绝", false, "没抛异常");
            }
            catch (ProviderException ex)
            {
                Check("明文 http 被拒绝且算永久失败", ex.Permanent && ex.Message.Contains("http"), ex.Message);
            }
        }

        var cfgQ = BaseCfg(baseUrl);
        cfgQ.Path = "/querykey?key=***";
        cfgQ.PageTokenField = "";
        using (var c = new ProviderClient(cfgQ.TimeoutSeconds))
        {
            await c.FetchAsync(cfgQ, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, CancellationToken.None);
            Check("{key} 被替换进 URL 且只出现一个问号",
                Seen.Any(q => q.Contains("querykey?key=***") && q.Contains("&start_date=")),
                string.Join(" | ", Seen.Take(4)));
        }
    }

    /// <summary>
    /// 拿 New API /api/log/self 的**真实响应结构**当样本（逐字段照抄实测到的形状）：
    /// 两层对象嵌套 data.items、页码分页 page/per_page/total、
    /// other 是字符串里包的 JSON、quota 要除以 500000 才是美元。
    /// </summary>
    private static async Task CaseNewApiShapeAsync(string prefix)
    {
        Environment.SetEnvironmentVariable("WL_FAKE_KEY", "sys-token-fake");
        var cfg = new ProviderConfig
        {
            Enabled = true,
            BaseUrl = prefix.TrimEnd('/'),
            Path = "/newapi",
            AuthHeader = "Authorization",
            AuthPrefix = "",
            ApiKeyEnvVar = "WL_FAKE_KEY",
            ExtraHeaders = "New-Api-User=12345",
            StartParam = "start_timestamp",
            EndParam = "end_timestamp",
            TimeFormat = "unix",
            ExtraQuery = "type=2",
            ItemsPath = "data.items",
            TimeField = "created_at",
            ModelField = "model_name",
            InputField = "prompt_tokens",
            OutputField = "completion_tokens",
            CachedInputField = "other.cache_tokens",
            CacheWriteField = "other.cache_creation_tokens",
            InputIncludesCached = true,
            CostField = "quota",
            CostUnitDivisor = 500000,
            PageMode = "page",
            PageStart = 0,
            PageParam = "page",
            PageSizeParam = "per_page",
            PageSize = 1,
            TotalField = "data.total",
            TimeoutSeconds = 10,
        };

        using var client = new ProviderClient(cfg.TimeoutSeconds);
        var r = await client.FetchAsync(cfg,
            new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        Check("页码分页翻了两页就按 total 停下", r.Pages == 2, $"pages={r.Pages}");

        long input = 0, cached = 0, cw = 0, outp = 0, reqs = 0;
        foreach (var byModel in r.Ledger.Days.Values)
            foreach (var c in byModel.Values)
            {
                input += c.Input; cached += c.CacheRead; cw += c.CacheWrite; outp += c.Output; reqs += c.Requests;
                Check("数据来被标成服务端来源", c.FromServer, $"Src={c.Src}");
            }

        // 第一页：prompt 23788 含 cache 1000 → uncached 22788；第二页：prompt 500 含 cache 0 → 500
        Check("钻进 other 字符串拿到缓存读（=1000）", cached == 1000, $"CacheRead={cached}");
        Check("钻进 other 字符串拿到缓存写（=20）", cw == 20, $"CacheWrite={cw}");
        Check("prompt_tokens 减掉缓存后不重复计（=23288）", input == 23288, $"Input={input}");
        Check("输出累加（=216）", outp == 216, $"Output={outp}");

        // quota 48572 + 1000 = 49572，除以 500000 = 0.099144
        var cost = Math.Round(r.ServerCost, 6);
        Check("服务端金额按 500000=1 换算", Math.Abs(cost - 0.099144) < 1e-6, $"cost={cost}");

        lock (Seen) { }
        Check("带了 New-Api-User 额外头", SeenHeaders.Any(x => x.Contains("new-api-user=12345")), "missing");
        Check("时间参数是 unix 秒", Seen.Any(q => q.Contains("start_timestamp=1791")), string.Join(" | ", Seen.Take(3)));
        Check("per_page 参数已传", Seen.Any(q => q.Contains("per_page=1")), "missing");
    }

    /// <summary>
    /// 余额/汇总型接口的真实取数。重点验证三件只有跑起来才知道的事：
    /// 它复用了同一套鉴权与错误处理、它**不带**时间/分页参数、
    /// 以及「余额是 0」和「字段名猜错了」这两种情况必须表现不同。
    /// </summary>
    private static async Task CaseBalanceAsync(string prefix)
    {
        const string secret = "BALSECRET-do-not-leak";
        Environment.SetEnvironmentVariable("WL_FAKE_KEY", secret);
        var baseUrl = prefix.TrimEnd('/');
        lock (Seen) { Seen.Clear(); }
        lock (SeenHeaders) { SeenHeaders.Clear(); }

        ProviderConfig Bal(string path, string field, double divisor = 1, double scale = 1) => new()
        {
            Enabled = true,
            BaseUrl = baseUrl,
            Path = path,
            AuthHeader = "Authorization",
            AuthPrefix = "",
            ApiKeyEnvVar = "WL_FAKE_KEY",
            Kind = "balance",
            BalanceField = field,
            CostUnitDivisor = divisor,
            CostScale = scale,
            TimeoutSeconds = 10,
            // 下面这些是用量型接口才用的参数，故意填上：如果它们漏进了余额请求，
            // 「不带时间参数」那条断言会立刻红
            StartParam = "start_timestamp",
            EndParam = "end_timestamp",
            TimeFormat = "unix",
            PageParam = "page",
            PageSizeParam = "per_page",
            PageSize = 50,
        };

        using var client = new ProviderClient(10);

        var r = await client.FetchBalanceAsync(Bal("/balance", "balance_infos[0].total_balance"), CancellationToken.None);
        Check("取到字符串形态的余额（249.90）", Math.Abs((double)r.Amount - 249.90) < 1e-9, $"Amount={r.Amount}");
        Check("接口原值也带回来（核对换算率用）", Math.Abs(r.RawValue - 249.90) < 1e-9, $"Raw={r.RawValue}");
        Check("余额响应体计了字节数", r.Bytes > 0, $"Bytes={r.Bytes}");
        Check("带了鉴权头", SeenHeaders.Any(h => h.Contains($"authorization={secret}")), "missing");
        Check("余额请求只发一次（一个标量没有第二页）", Seen.Count == 1, $"发了 {Seen.Count} 次：{string.Join(" | ", Seen)}");
        Check("余额请求不拼时间参数",
            !Seen.Any(q => q.Contains("start_timestamp") || q.Contains("end_timestamp") || q.Contains("start_date")),
            string.Join(" | ", Seen));
        Check("余额请求不拼分页参数",
            !Seen.Any(q => q.Contains("page=") || q.Contains("per_page=")), string.Join(" | ", Seen));

        var both = await client.FetchBalanceAsync(
            Bal("/balance", "balance_infos[0].total_balance,balance_infos[1].total_balance"), CancellationToken.None);
        Check("逗号分隔多路径求和（两个钱包 249.90+0.10）", Math.Abs((double)both.Amount - 250.0) < 1e-6, $"Amount={both.Amount}");

        var quota = await client.FetchBalanceAsync(Bal("/balancequota", "data.quota", divisor: 500000), CancellationToken.None);
        Check("整数记账按换算率折成金额（49572 ÷ 500000）", Math.Abs((double)quota.Amount - 0.099144) < 1e-9, $"Amount={quota.Amount}");

        var cents = await client.FetchBalanceAsync(Bal("/balancequota", "data.quota", scale: 0.01), CancellationToken.None);
        Check("CostScale 参与换算且没被取整抹平（49572 × 0.01 = 495.72）",
            Math.Abs((double)cents.Amount - 495.72) < 1e-9, $"Amount={cents.Amount}");

        var deep = await client.FetchBalanceAsync(Bal("/balancestr", "data.other.left"), CancellationToken.None);
        Check("钻进「字符串里包的 JSON」取余额（12.5）", Math.Abs((double)deep.Amount - 12.5) < 1e-9, $"Amount={deep.Amount}");

        var zero = await client.FetchBalanceAsync(Bal("/balancezero", "data.total_balance"), CancellationToken.None);
        Check("余额真的是 0 时是有效读数，不报错", zero.Amount == 0m, $"Amount={zero.Amount}");

        try
        {
            await client.FetchBalanceAsync(Bal("/balancemissing", "data.total_balance"), CancellationToken.None);
            Check("字段名猜错要报错，不能当余额 0 显示", false, "没抛");
        }
        catch (ProviderException ex)
        {
            Check("字段名猜错时报错并指向 BalanceField",
                ex.Permanent && ex.Message.Contains("BalanceField"), ex.Message);
            Check("取数失败的文案不泄漏密钥值", !ex.Message.Contains(secret), "密钥出现在异常里");
        }

        try
        {
            await client.FetchBalanceAsync(Bal("/balancehuge", "data.total_balance"), CancellationToken.None);
            Check("换算完不像金额的数字要报错", false, "没抛");
        }
        catch (ProviderException ex)
        {
            Check("换算完不像金额时直说，而不是甩一个 OverflowException 给用户",
                ex.Permanent && ex.Message.Contains("不像个金额") && ex.Message.Contains("CostUnitDivisor"), ex.Message);
        }

        try
        {
            await client.FetchBalanceAsync(Bal("/denied", "data.total_balance"), CancellationToken.None);
            Check("余额接口 401 要抛 ProviderException", false, "没抛");
        }
        catch (ProviderException ex)
        {
            Check("余额接口复用同一套 401 处理（含接口原话）",
                ex.Permanent && ex.Message.Contains("权限不足") && ex.Message.Contains("insufficient scope"), ex.Message);
            Check("余额接口的 401 文案不泄漏密钥值", !ex.Message.Contains(secret), "密钥出现在异常里");
        }
    }

    /// <summary>
    /// 抽 SendJsonAsync 时必须一起锁住的两条分支：404 与「200 但不是 JSON」。
    /// 这两条以前没有用例覆盖，重构后万一顺序写反了没人报警。
    /// </summary>
    private static void CaseMovedRequestPath(string prefix)
    {
        const string secret = "SECRET-DO-NOT-LEAK";
        Environment.SetEnvironmentVariable("WL_FAKE_KEY", secret);

        var cfg404 = BaseCfg(prefix);
        cfg404.Path = "/notfound";
        using (var client = new ProviderClient(cfg404.TimeoutSeconds))
        {
            try
            {
                client.FetchAsync(cfg404, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, CancellationToken.None)
                      .GetAwaiter().GetResult();
                Check("404 要抛 ProviderException", false, "没抛");
            }
            catch (ProviderException ex)
            {
                Check("404 提示路径不存在并带出接口原话",
                    ex.Permanent && ex.Message.Contains("404") && ex.Message.Contains("接口说：no such path"), ex.Message);
                Check("404 文案不泄漏密钥值", !ex.Message.Contains(secret), "密钥出现在异常里");
            }
        }

        var cfgBad = BaseCfg(prefix);
        cfgBad.Path = "/html";
        using (var client = new ProviderClient(cfgBad.TimeoutSeconds))
        {
            try
            {
                client.FetchAsync(cfgBad, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, CancellationToken.None)
                      .GetAwaiter().GetResult();
                Check("非 JSON 响应要抛 ProviderException", false, "没抛");
            }
            catch (ProviderException ex)
            {
                Check("非 JSON 响应报「返回不是合法 JSON」并算永久失败",
                    ex.Permanent && ex.Message.Contains("不是合法 JSON"), ex.Message);
            }
        }
    }

    /// <summary>
    /// 多来源分派用例：两个运营商的账不能互相覆盖（曾经的 bug：29 天本地账被压成 1 天），
    /// 重叠模型不能算两遍，币种不能混加。
    /// </summary>
    private static void CaseSplitter()
    {
        var local = new UsageLedger();
        for (var d = 1; d <= 29; d++)
        {
            var key = $"2026-09-{d:00}";
            local.Add(key, "SCNet-Max", new Counters { Input = 100, Output = 50, Total = 150 });
            local.Add(key, "GLM-5.2", new Counters { Input = 10, Output = 5, Total = 15 });
        }

        var server = new UsageLedger();
        server.Add("2026-09-29", "deepseek-v4-flash", new Counters { Input = 22788, CacheRead = 1000, Total = 23954, Src = 1 });

        // 1) 两边模型不重叠 → 本地 29 天一天都不能丢
        var (l1, s1, i1) = LedgerSplitter.Split(local, server, ModelScope.Empty, ModelScope.Parse("deepseek-"));
        Check("不重叠时本地 29 天全保留（旧 bug 会压成 1 天）", l1.Days.Count == 29, $"days={l1.Days.Count}");
        Check("云端块保留自己那一天", s1.Days.Count == 1, $"days={s1.Days.Count}");
        Check("不重叠时不该有模型被剔走", i1.MovedToServer.Count == 0, string.Join(",", i1.MovedToServer));

        // 2) 重叠模型 → 云端优先，本地不重复计入
        var localOverlap = local.Clone();
        localOverlap.Add("2026-09-29", "deepseek-v4-flash", new Counters { Input = 999, Total = 999 });
        var (l2, s2, i2) = LedgerSplitter.Split(localOverlap, server, ModelScope.Empty, ModelScope.Parse("deepseek-"));
        Check("重叠模型从本地剔走", !l2.Days.Values.Any(m => m.ContainsKey("deepseek-v4-flash")), "本地仍有");
        Check("重叠模型归云端", s2.Days["2026-09-29"]["deepseek-v4-flash"].Input == 22788, "云端值不对");
        Check("重叠情况有说明", i2.MovedToServer.Contains("deepseek-v4-flash"), string.Join(",", i2.MovedToServer));

        // 3) 云端范围为空 → 上报什么就归什么
        var (l3, s3, _) = LedgerSplitter.Split(localOverlap, server, ModelScope.Empty, ModelScope.Empty);
        Check("云端范围为空时全量归云端", s3.Days.Count == 1 && l3.Days.Count == 29, $"l={l3.Days.Count} s={s3.Days.Count}");

        // 4) 本地手填范围：范围外的两边都不算，并且必须告知用户
        var (l4, _, i4) = LedgerSplitter.Split(local, new UsageLedger(), ModelScope.Parse("SCNet-"), ModelScope.Empty);
        Check("本地范围外的模型不再计入", !l4.Days.Values.Any(m => m.ContainsKey("GLM-5.2")), "GLM-5.2 还在");
        Check("范围外丢掉的模型有说明", i4.DroppedByLocalScope.Contains("GLM-5.2"), string.Join(",", i4.DroppedByLocalScope));

        // 5) 币种不混：两个块各用自己的货币，主块费用不等于两者之和
        var pricing = PricingConfig.Parse("{\"Currency\":\"\u00a5\",\"PerTokens\":1000000,\"Models\":[{\"Model\":\"SCNet-Max\",\"Input\":21,\"Output\":10.5,\"CacheRead\":2.1,\"CacheWrite\":0}]}");
        var blockLocal = SnapshotBuilder.BuildBlock(l1, pricing, "本地日志");
        var serverPricing = PricingConfig.Parse(pricing.ToJson());
        serverPricing.Currency = "$";
        var blockServer = SnapshotBuilder.BuildBlock(s1, serverPricing, "示例网关");
        Check("本地块用 ¥", blockLocal.Currency == "\u00a5", blockLocal.Currency);
        Check("云端块用 $", blockServer.Currency == "$", blockServer.Currency);
        Check("两块的 token 量各自独立、未相加", blockLocal.All.C.Total == 29 * 165 && blockServer.All.C.Total == 23954,
            $"local={blockLocal.All.C.Total} server={blockServer.All.C.Total}");
    }

    /// <summary>云端账本必须和水位线一起持久化，否则重启后已拉过的数据会被水位线遮住而永久丢失。</summary>
    private static void CaseServerLedgerPersistence()
    {
        var l = new UsageLedger { RootDescription = "云端" };
        l.Add("2026-10-12", "deepseek-v4-flash", new Counters
        {
            Input = 22788, Output = 166, CacheRead = 1000, CacheWrite = 20,
            Total = 23954, Src = 1, CostUnits = 9914400,
        });

        var st = new ProviderState { CoveredUntilUtc = new DateTime(2026, 10, 12, 19, 0, 0, DateTimeKind.Utc) };
        st.SaveLedger(l);
        var back = ProviderState.Parse(st.ToJson());

        Check("云端账本序列化后能恢复", back.HasPersistedLedger, "恢复为空");
        var c = back.LoadLedger().Days["2026-10-12"]["deepseek-v4-flash"];
        Check("恢复后 token 不丢", c.Total == 23954 && c.Input == 22788, $"total={c.Total}");
        Check("恢复后来源标记不丢", c.FromServer, $"Src={c.Src}");
        Check("恢复后服务端金额不丢", Math.Abs(c.Cost - 0.099144) < 1e-6, $"cost={c.Cost}");
        Check("水位线一起恢复", back.CoveredUntilUtc == st.CoveredUntilUtc, "水位线丢了");
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (!ok) _failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok ? "" : "  →  " + detail)}");
    }

    private static readonly List<string> Seen = new();
    private static readonly List<string> SeenHeaders = new();

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task ServeAsync(HttpListener listener, List<string> seen)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch (Exception) { return; }

            var path = ctx.Request.Url?.AbsolutePath ?? "";
            var query = ctx.Request.Url?.Query ?? "";
            lock (seen) { seen.Add(path + query); }
            lock (SeenHeaders)
            {
                SeenHeaders.Add($"x-api-key={ctx.Request.Headers["x-api-key"]}");
                SeenHeaders.Add($"anthropic-version={ctx.Request.Headers["anthropic-version"]}");
                SeenHeaders.Add($"authorization={ctx.Request.Headers["Authorization"]}");
                SeenHeaders.Add($"new-api-user={ctx.Request.Headers["New-Api-User"]}");
            }

            string body;
            int status = 200;

            if (path == "/denied")
            {
                status = 401;
                body = "{\"error\":{\"message\":\"insufficient scope\"}}";
            }
            else if (path == "/notfound")
            {
                status = 404;
                body = "{\"error\":{\"message\":\"no such path\"}}";
            }
            else if (path == "/balance")
            {
                // DeepSeek 形态：分钱包、值是字符串（"249.90"）、藏在数组里
                body = "{\"success\":true,\"is_pro_user\":false,\"balance_infos\":["
                     + "{\"group\":\"general\",\"total_balance\":\"249.90\"},"
                     + "{\"group\":\"timing\",\"total_balance\":\"0.10\"}]}";
            }
            else if (path == "/balancequota")
            {
                // New API 形态：整数记账，要 ÷ 500000 才是美元
                body = "{\"code\":0,\"data\":{\"quota\":49572,\"used_quota\":100}}";
            }
            else if (path == "/balancestr")
            {
                // 值藏在「字符串里包的 JSON」里
                body = "{\"data\":{\"other\":\"{\\\"left\\\":12.5}\"}}";
            }
            else if (path == "/balancezero")
            {
                // 真·余额为零：这必须是有效读数，不能和「字段名猜错了」混成一回事
                body = "{\"data\":{\"total_balance\":0}}";
            }
            else if (path == "/balancemissing")
            {
                body = "{\"data\":{\"something\":1}}";
            }
            else if (path == "/balancehuge")
            {
                // 取错了字段时会拿到一个天文数字（例如把 token 数当成了余额）
                body = "{\"data\":{\"total_balance\":1e30}}";
            }
            else if (path == "/html")
            {
                // 200 + 一段根本不是 JSON 的 HTML（网关登录页、CDN 报错页都是这个形态）
                body = "<html><body>502 Bad Gateway</body></html>";
            }
            else if (path == "/badshape")
            {
                body = "{\"something\":{\"else\":1}}";
            }
            else if (path == "/scaled")
            {
                body = "{\"data\":[{\"period\":{\"start\":\"2026-10-01T12:00:00Z\"},\"model\":\"M-S\","
                     + "\"input_tokens\":1000000,\"output_tokens\":500000}],\"next_page\":null,\"has_more\":false}";
            }
            else if (path == "/bizerror")
            {
                // HTTP 200，但真正的错误在 body 里
                body = "{\"code\":401,\"msg\":\"当前用户不存在coding plan\",\"data\":[]}";
            }
            else if (path == "/querykey")
            {
                body = "{\"data\":[],\"next_page\":null,\"has_more\":false}";
            }
            else if (path == "/newapi")
            {
                // 逐字段照抄 ps.air-outer.com 实测到的响应形状：data.items + total + other 内嵌 JSON
                var page = ctx.Request.QueryString["page"];
                body = page == "1"
                    ? "{\"data\":{\"page\":2,\"page_size\":1,\"total\":2,\"items\":["
                      + "{\"created_at\":1791786300,\"type\":2,\"model_name\":\"deepseek-v4-flash\","
                      + "\"quota\":1000,\"prompt_tokens\":500,\"completion_tokens\":50,"
                      + "\"other\":\"{\\\"cache_tokens\\\":0,\\\"cache_creation_tokens\\\":0}\"}]},\"success\":true}"
                    : "{\"data\":{\"page\":1,\"page_size\":1,\"total\":2,\"items\":["
                      + "{\"created_at\":1791786255,\"type\":2,\"model_name\":\"deepseek-v4-flash\","
                      + "\"quota\":48572,\"prompt_tokens\":23788,\"completion_tokens\":166,"
                      + "\"other\":\"{\\\"cache_ratio\\\":0.5,\\\"cache_tokens\\\":1000,\\\"cache_creation_tokens\\\":20,"
                      + "\\\"model_ratio\\\":2,\\\"completion_ratio\\\":3}\"}]},\"success\":true}";
            }
            else if (path == "/usage_report")
            {
                // 结构照抄官方文档示例；第二页再给一个桶验证 page= 游标
                body = ctx.Request.QueryString["page"] is null
                    ? "{\"data\":[{\"starting_at\":\"2025-08-01T00:00:00Z\",\"ending_at\":\"2025-08-02T00:00:00Z\",\"results\":["
                      + "{\"model\":\"claude-opus-5\",\"uncached_input_tokens\":1500,\"cache_read_input_tokens\":200,"
                      + "\"cache_creation\":{\"ephemeral_1h_input_tokens\":0,\"ephemeral_5m_input_tokens\":0},\"output_tokens\":500}]}],"
                      + "\"has_more\":true,\"next_page\":\"page_MjAyNS04LTAy\"}"
                    : "{\"data\":[{\"starting_at\":\"2025-08-02T00:00:00Z\",\"ending_at\":\"2025-08-03T00:00:00Z\",\"results\":["
                      + "{\"model\":\"claude-opus-5\",\"uncached_input_tokens\":10,\"cache_read_input_tokens\":5,"
                      + "\"cache_creation\":{\"ephemeral_1h_input_tokens\":7,\"ephemeral_5m_input_tokens\":3},\"output_tokens\":20}]}],"
                      + "\"has_more\":false,\"next_page\":null}";
            }
            else
            {
                var after = ctx.Request.QueryString["after"];
                body = after == "P2"
                    ? "{\"data\":[{\"period\":{\"start\":\"2026-10-02T12:00:00Z\"},\"model\":\"M-B\",\"input_tokens\":300,\"input_cached_tokens\":0,\"output_tokens\":0}],\"next_page\":null,\"has_more\":false}"
                    : "{\"data\":["
                      + "{\"period\":{\"start\":\"2026-10-01T12:00:00Z\"},\"model\":\"M-A\",\"input_tokens\":1000,\"input_cached_tokens\":400,\"output_tokens\":200},"
                      + "{\"period\":{\"start\":\"2026-10-01T13:00:00Z\"},\"model\":\"M-A\",\"input_tokens\":1000,\"input_cached_tokens\":400,\"output_tokens\":200}"
                      + "],\"next_page\":\"P2\",\"has_more\":true}";
            }
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }
}
