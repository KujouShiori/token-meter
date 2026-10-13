using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TokenMeter;

/// <summary>接口侧的可预期失败：调用方据此降级到本地日志，不当成崩溃。</summary>
public sealed class ProviderException : Exception
{
    /// <summary>true = 权限/配置类问题，重试无意义，本轮直接降级并停手。</summary>
    public bool Permanent { get; }

    public ProviderException(string message, bool permanent = false) : base(message)
    {
        Permanent = permanent;
    }
}

public sealed class ProviderBalanceResult
{
    /// <summary>换算成币种单位后的读数（已 ÷ CostUnitDivisor、× CostScale）。</summary>
    public decimal Amount { get; set; }

    /// <summary>接口**原样**报回的数值，没换算过。只进日志，用来核对换算率填错了没有
    /// ——「余额看着是 249 但实际 $0.0005」这种，看原值一眼就能定位。</summary>
    public double RawValue { get; set; }

    public long Bytes { get; set; }
}

public sealed class ProviderFetchResult
{
    public UsageLedger Ledger { get; } = new();
    /// <summary>服务端数据可信到哪一刻（已扣掉滞后带）。</summary>
    public DateTime CoveredUntilUtc { get; set; }
    public int Windows { get; set; }
    public int Pages { get; set; }
    public long BytesIn { get; set; }
    /// <summary>服务端报回的金额合计（元）。</summary>
    public double ServerCost { get; set; }
    /// <summary>本次拉取里实际计入的条目数，用于页码分页判断是否翻完。</summary>
    public int ItemsInWindow { get; set; }

    /// <summary>接口是否给了请求次数字段（没给的话界面不能把服务端的 0 当成真实请求数）。</summary>
    public bool HadRequestsField { get; set; }
}

/// <summary>
/// 通用服务商接口客户端，两种形态：
///   · FetchAsync —— 逐条/逐天的用量明细，走时间窗口 + 分页；
///   · FetchBalanceAsync —— 只读一个总额（余额/剩余额度），不分页、不带时间参数。
///
/// 用量侧三件事按用户要求处理：
///   1. 分页：PageTokenField 非空就一直翻，受 MaxPagesPerWindow 上限保护；
///   2. 时间窗口：从水位线滚到「现在 - 滞后带」，跨度超过 WindowMaxDays 就切段，
///      一轮最多 MaxWindowsPerRun 段，避免首次同步把界面卡死；
///   3. 字段口径：InputIncludesCached 决定缓存部分是从输入里减出来还是直接相加。
///
/// 所有字段名与限制都来自 ProviderConfig，代码里不写死任何厂商数值。
/// 两种形态共用同一个 SendJsonAsync，鉴权与错误处理只有一份。
/// </summary>
public sealed class ProviderClient : IDisposable
{
    private readonly HttpClient _http;

    public ProviderClient(int timeoutSeconds)
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 2, 120)),
        };
    }

    /// <summary>
    /// 拉取 (from, to] 区间的服务端数据。调用方负责把 to 限制在「现在 - 滞后带」内。
    /// </summary>
    public async Task<ProviderFetchResult> FetchAsync(
        ProviderConfig cfg, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var key = ResolveKey(cfg);

        var result = new ProviderFetchResult { CoveredUntilUtc = toUtc };
        var pageMode = string.Equals(cfg.PageMode, "page", StringComparison.OrdinalIgnoreCase);
        var pageNo = cfg.PageStart;
        var cursor = pageMode ? pageNo.ToString(CultureInfo.InvariantCulture) : string.Empty;
        var pages = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var url = ResolveUrl(cfg, BuildUrl(cfg, fromUtc, toUtc, cursor), key);
            var (doc, bytes) = await SendJsonAsync(cfg, url, key, ct).ConfigureAwait(false);
            result.BytesIn += bytes;

            using (doc)
            {
                var root = doc.RootElement;

                var (resolved, pairs) = EnumerateItems(root, cfg.ItemsPath);
                if (!resolved)
                    throw new ProviderException($"在返回里找不到条目数组「{cfg.ItemsPath}」——请改 ItemsPath", permanent: true);

                foreach (var (owner, item) in pairs)
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    AbsorbItem(cfg, owner, item, result);
                }

                // 分页：page 模式按页码递增，用 total / 本页数量判断翻完；
                // cursor 模式看响应里的游标字段，has_more 为 false 也结束
                if (pageMode)
                {
                    result.ItemsInWindow += pairs.Count;
                    pages++;
                    result.Pages = pages;

                    var wantMore = pairs.Count > 0;

                    if (!string.IsNullOrWhiteSpace(cfg.TotalField)
                        && TryNavigate(root, cfg.TotalField, out var totalEl)
                        && totalEl.ValueKind == JsonValueKind.Number)
                    {
                        wantMore = result.ItemsInWindow < (long)totalEl.GetDouble();
                    }
                    else if (cfg.PageSize > 0)
                    {
                        wantMore = pairs.Count >= cfg.PageSize;
                    }

                    if (!wantMore) break;
                    if (pages >= cfg.MaxPagesPerWindow)
                        throw new ProviderException($"分页超过上限 {cfg.MaxPagesPerWindow} 页仍未结束——请检查 TotalField/PageSize 是否配对");

                    pageNo++;
                    cursor = pageNo.ToString(CultureInfo.InvariantCulture);
                    continue;
                }

                cursor = string.Empty;
                if (!string.IsNullOrWhiteSpace(cfg.PageTokenField)
                    && TryNavigate(root, cfg.PageTokenField, out var tokenEl))
                {
                    cursor = tokenEl.ValueKind switch
                    {
                        JsonValueKind.String => tokenEl.GetString() ?? string.Empty,
                        JsonValueKind.Number => tokenEl.ToString(),
                        _ => string.Empty,
                    };
                }

                if (!string.IsNullOrWhiteSpace(cfg.HasMoreField)
                    && TryNavigate(root, cfg.HasMoreField, out var hasMore))
                {
                    if (hasMore.ValueKind == JsonValueKind.False) cursor = string.Empty;
                }

                result.Pages = ++pages;
                if (string.IsNullOrEmpty(cursor)) break;
                if (pages >= cfg.MaxPagesPerWindow)
                    throw new ProviderException($"分页超过上限 {cfg.MaxPagesPerWindow} 页仍未结束——请检查 PageTokenField/PageParam 是否配对");
            }
        }

        return result;
    }

    /// <summary>
    /// 读一个「某一刻的总额」：账户余额、剩余额度、已用总额。
    ///
    /// 与 FetchAsync 的三点区别，都是被接口现实逼出来的：
    ///   1. 只要一个标量，不认 ItemsPath，也不产生按天账本；
    ///   2. 不翻页 —— 一个数没有第二页；
    ///   3. **不拼时间参数**。余额是快照，多带一个 start_date 常常直接被网关拒掉。
    /// 发请求与错误处理共用 SendJsonAsync，不复制第二份。
    /// </summary>
    public async Task<ProviderBalanceResult> FetchBalanceAsync(ProviderConfig cfg, CancellationToken ct)
    {
        var key = ResolveKey(cfg);

        var url = ResolveUrl(cfg, BuildBalanceUrl(cfg), key);
        var (doc, bytes) = await SendJsonAsync(cfg, url, key, ct).ConfigureAwait(false);

        using (doc)
        {
            var (found, raw) = ReadScalar(doc.RootElement, cfg.BalanceField);
            if (!found)
                throw new ProviderException(
                    $"在返回里取不到数值「{cfg.BalanceField}」——请改 BalanceField"
                    + "（支持 a.b、数组下标 a[0].b、钻进「字符串里包的 JSON」，逗号分隔多个会求和）",
                    permanent: true);

            var divisor = cfg.CostUnitDivisor > 0 && !double.IsNaN(cfg.CostUnitDivisor) && !double.IsInfinity(cfg.CostUnitDivisor)
                ? cfg.CostUnitDivisor : 1;
            var scale = cfg.CostScale > 0 && !double.IsNaN(cfg.CostScale) && !double.IsInfinity(cfg.CostScale)
                ? cfg.CostScale : 1;

            // decimal 装不下 1e30 这种数，硬转会在业务代码里抛 OverflowException，
            // 用户看到的是一句天书；接口给的东西不像金额就该直接这么说
            var converted = raw / divisor * scale;
            if (!double.IsFinite(converted) || Math.Abs(converted) > 1e15)
                throw new ProviderException(
                    $"「{cfg.BalanceField}」取到 {raw.ToString(CultureInfo.InvariantCulture)}，换算后 {converted.ToString(CultureInfo.InvariantCulture)} —— 这不像个金额，"
                    + "请检查 BalanceField 取对字段没有、CostUnitDivisor/CostScale 填对没有",
                    permanent: true);

            return new ProviderBalanceResult
            {
                Amount = (decimal)converted,
                RawValue = raw,
                Bytes = bytes,
            };
        }
    }

    /// <summary>校验配置并取出密钥。密钥只从环境变量来，取到的值只在这段内存里待着：
    /// 不写缓存、不写日志、也绝不进任何异常文案（有断言盯着这条）。</summary>
    private static string ResolveKey(ProviderConfig cfg)
    {
        if (!cfg.IsUsable(out var why)) throw new ProviderException(why, permanent: true);
        var key = Environment.GetEnvironmentVariable(cfg.ApiKeyEnvVar.Trim()) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            throw new ProviderException($"环境变量 {cfg.ApiKeyEnvVar.Trim()} 没有值", permanent: true);
        return key;
    }

    /// <summary>余额/汇总型接口不认时间窗口，所以这里只拼 ExtraQuery。</summary>
    private static string BuildBalanceUrl(ProviderConfig cfg)
    {
        var q = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(cfg.ExtraQuery))
        {
            foreach (var pair in cfg.ExtraQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2) AppendRaw(q, kv[0].Trim(), kv[1].Trim());
            }
        }

        var joiner = (cfg.Path ?? string.Empty).Contains('?') ? "&" : "?";
        return cfg.BaseUrl.TrimEnd('/') + cfg.Path + (q.Length > 0 ? joiner + q : string.Empty);
    }

    /// <summary>
    /// 取一个标量金额。三种现实形态都要覆盖：
    ///   · 值是数字，也可能是 "3.5" 这种字符串（DeepSeek 的 total_balance 就是字符串）；
    ///   · 值可能藏在「字符串里包的 JSON」中，靠 TryNavigateDeep 钻进去；
    ///   · 分钱包/多档余额要写成逗号分隔的多个路径求和。
    /// <c>Found=false</c> 与「取到 0」是两回事：后者是真实余额，前者是字段名猜错了。
    /// </summary>
    internal static (bool Found, double Sum) ReadScalar(JsonElement root, string paths)
    {
        if (string.IsNullOrWhiteSpace(paths)) return (false, 0);

        var any = false;
        double sum = 0;
        foreach (var raw in paths.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var docs = new List<JsonDocument>();
            try
            {
                if (!TryNavigateDeep(root, raw.Trim(), out var el, docs)) continue;
                var v = el.ValueKind switch
                {
                    JsonValueKind.Number => el.GetDouble(),
                    JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
                        ? p : (double?)null,
                    _ => null,
                };
                if (v is null) continue;
                any = true;
                sum += v.Value;
            }
            finally
            {
                foreach (var d in docs) d.Dispose();
            }
        }

        return (any, sum);
    }

    /// <summary>
    /// 发一次 GET 并把响应体读成 JsonDocument。**所有接口形态共用这一段**：
    /// 按天分页的用量与只要一个标量的余额都必须走这里，写成两份以后只会修其中一份。
    ///
    /// 负责的事情按顺序是：地址安全卡控 → 鉴权头 → 额外固定头 → 发送 →
    /// 超时/连不上 → 4xx 提炼业务错误 → 读 body → 解析 JSON → 200 里的业务层错误。
    /// 返回的 Doc 由调用方释放。
    /// </summary>
    private async Task<(JsonDocument Doc, long Bytes)> SendJsonAsync(
        ProviderConfig cfg, string url, string key, CancellationToken ct)
    {
        AssertSafeUrl(url);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        // 只加一个鉴权头；头名和前缀都可配，不假设任何厂商写法
        var headerValue = string.IsNullOrEmpty(cfg.AuthPrefix) ? key : cfg.AuthPrefix + key;
        if (!TryAddHeader(req, cfg.AuthHeader, headerValue, out var headerError))
            throw new ProviderException(headerError, permanent: true);

        // 部分接口除了鉴权头还强制要求版本头（例如 anthropic-version）
        foreach (var (hk, hv) in ParseExtraHeaders(cfg.ExtraHeaders))
            req.Headers.TryAddWithoutValidation(hk, hv);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException($"请求超时（{cfg.TimeoutSeconds}s）");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException($"连不上接口：{ex.Message}");
        }

        using (resp)
        {
            // 非 2xx 也先读一次 body：401/403/404 的响应体里常常正好写着那句能定位问题的话
            var detail = resp.IsSuccessStatusCode
                ? string.Empty
                : await ErrorDetailAsync(resp, ct).ConfigureAwait(false);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProviderException($"权限不足或密钥无效（HTTP {(int)resp.StatusCode}）{(detail.Length > 0 ? "，接口说：" + detail : "")}——"
                    + "注意：受限 Key 常常只能看到自己创建的 Key 的用量，这类接口不会报错，只会少给数",
                    permanent: true);

            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new ProviderException($"接口路径不存在（HTTP 404）：{cfg.Path}{(detail.Length > 0 ? "，接口说：" + detail : "")}",
                    permanent: true);

            if (!resp.IsSuccessStatusCode)
                throw new ProviderException($"HTTP {(int)resp.StatusCode}{(detail.Length > 0 ? "：" + detail : "")}");

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var bytes = Encoding.UTF8.GetByteCount(json);

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new ProviderException($"返回不是合法 JSON：{ex.Message}", permanent: true);
            }

            // 有些接口 key 不对也返回 HTTP 200，真正的问题在 body 的 code/msg：
            // 不抱出来就会被当成「空数据」静默吃掉（这个思路抄自 DeepSeek-Balance-Whale-Widget）
            var bizError = ApiBusinessError(doc.RootElement);
            if (!string.IsNullOrEmpty(bizError))
            {
                doc.Dispose();
                throw new ProviderException($"接口返回 200 但业务层报错：{bizError}");
            }

            return (doc, bytes);
        }
    }

    /// <summary>把 URL 模板里的 {base} / {key} 换成真值。
    /// 少数接口把密钥放在查询参数里（例如 Gemini 的 ?key=…）才用得到 {key}；
    /// 替换一定发生在安全校验之前，保证校验的是真正要发出去的那个地址。</summary>
    internal static string ResolveUrl(ProviderConfig cfg, string url, string key)
        => url.Replace("{base}", cfg.BaseUrl, StringComparison.Ordinal)
              .Replace("{key}", Uri.EscapeDataString(key), StringComparison.Ordinal);

    private static bool TryAddHeader(HttpRequestMessage req, string name, string value, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(name)) { error = "鉴权头名字没填"; return false; }
        try
        {
            // HttpClient 对 Authorization 有专门处理，走通用 TryAddWithoutValidation 最省事也最不容易踩格式校验
            req.Headers.TryAddWithoutValidation(name.Trim(), value);
            return req.Headers.Contains(name.Trim());
        }
        catch (Exception ex)
        {
            error = $"鉴权头「{name}」加不上：{ex.Message}";
            return false;
        }
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            body = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return body.Length > 200 ? body[..200] + "…" : body;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>解析额外固定请求头：多个用分号或换行分隔，每项 k=v。</summary>
    internal static List<(string Name, string Value)> ParseExtraHeaders(string raw)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        foreach (var pair in raw.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2) continue;
            var name = kv[0].Trim();
            if (name.Length == 0) continue;
            list.Add((name, kv[1].Trim()));
        }
        return list;
    }

    /// <summary>
    /// 按 ItemsPath 展开条目，支持 data（扁平）与 data[].results（两层）。
    /// 每项连同「拥有它的上层对象」一起返回，因为时间戳可能写在外层时间桶上。
    /// found=false 表示路径没解析到数组；路径对但数组为空时 found=true 且列表为空（那是真的没用量）。
    /// </summary>
    internal static (bool Found, List<(JsonElement Owner, JsonElement Item)> Items) EnumerateItems(JsonElement root, string itemsPath)
    {
        var empty = new List<(JsonElement, JsonElement)>();
        var segs = (itemsPath ?? string.Empty).Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0) return (false, empty);

        var current = new List<(JsonElement Owner, JsonElement El)> { (default, root) };
        var lastWasArray = false;

        for (var i = 0; i < segs.Length; i++)
        {
            lastWasArray = false;
            var seg = segs[i];
            if (seg.EndsWith("[]", StringComparison.Ordinal)) seg = seg[..^2];

            var next = new List<(JsonElement, JsonElement)>();
            foreach (var (_, el) in current)
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!el.TryGetProperty(seg, out var child)) continue;
                if (child.ValueKind == JsonValueKind.Array)
                {
                    lastWasArray = true;
                    foreach (var g in child.EnumerateArray()) next.Add((el, g));
                }
                else
                {
                    next.Add((el, child));
                }
            }
            current = next;
            if (current.Count == 0 && i < segs.Length - 1) return (false, empty);
        }

        if (!lastWasArray) return (false, empty);
        return (true, current);
    }

    private static void AbsorbItem(ProviderConfig cfg, JsonElement owner, JsonElement item, ProviderFetchResult result)
    {
        // 时间：先在条目里找，找不到再到上层时间桶上找；取不到就整条跳过——归错日期比漏一条更糟
        if (string.IsNullOrWhiteSpace(cfg.TimeField)) return;
        var gotTime = TryNavigate(item, cfg.TimeField, out var timeEl);
        if (!gotTime && owner.ValueKind == JsonValueKind.Object)
            gotTime = TryNavigate(owner, cfg.TimeField, out timeEl);
        if (!gotTime) return;

        DateTime utc;
        if (timeEl.ValueKind == JsonValueKind.String)
        {
            var s = timeEl.GetString() ?? string.Empty;
            if (!DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc))
                return;
        }
        else if (timeEl.ValueKind == JsonValueKind.Number)
        {
            // 秒或毫秒都见过，按量级判一下
            var v = timeEl.GetDouble();
            utc = v > 1e11
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)v).UtcDateTime
                : DateTimeOffset.FromUnixTimeSeconds((long)v).UtcDateTime;
        }
        else
        {
            return;
        }

        var dayKey = Format.DayKey(DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local)));

        var model = string.IsNullOrEmpty(cfg.ModelField) ? cfg.ModelFallback : null;
        if (model is null && TryNavigate(item, cfg.ModelField, out var modelEl))
            model = modelEl.ValueKind == JsonValueKind.String ? modelEl.GetString() : modelEl.ToString();
        if (string.IsNullOrWhiteSpace(model)) model = cfg.ModelFallback;

        var rawIn = ReadNumber(item, cfg.InputField, cfg.ValueScale);
        var outp = ReadNumber(item, cfg.OutputField, cfg.ValueScale);
        var cached = ReadNumber(item, cfg.CachedInputField, cfg.ValueScale);
        var cacheWrite = ReadNumber(item, cfg.CacheWriteField, cfg.ValueScale);
        var requests = ReadNumber(item, cfg.RequestsField, 1);   // 请求数是计数，不跟着 token 缩放
        if (requests > 0) result.HadRequestsField = true;

        long uncached;
        if (cfg.InputIncludesCached)
        {
            // 输入里已含缓存 → 减出来。减成负数说明口径判断反了，夹到 0 并让上层有机会发现。
            uncached = Math.Max(0, rawIn - cached);
        }
        else
        {
            uncached = rawIn;
        }

        var c = new Counters
        {
            Input = uncached,
            Output = outp,
            CacheRead = cached,
            CacheWrite = cacheWrite,
            Requests = requests,
            // 服务端拉回来的数据一律打来源标记，上游才能区分「服务端金额只盖了部分天」
            Src = 1,
        };
        c.Total = c.Input + c.Output + c.CacheRead + c.CacheWrite;
        if (c.Total <= 0) return;

        if (!string.IsNullOrWhiteSpace(cfg.CostField))
        {
            var divisor = cfg.CostUnitDivisor > 0 && !double.IsNaN(cfg.CostUnitDivisor) && !double.IsInfinity(cfg.CostUnitDivisor)
                ? cfg.CostUnitDivisor : 1;
            var scale = cfg.CostScale > 0 && !double.IsNaN(cfg.CostScale) && !double.IsInfinity(cfg.CostScale)
                ? cfg.CostScale : 1;
            // 换算全程留在 double 里，最后才进 1e-8 定点：
            // 借 ReadNumber 的 scale 参数会先把「分」取整，小数金额直接被抹平
            var rawCost = ReadNumber(item, cfg.CostField, 1);
            if (rawCost > 0) c.AddCost(rawCost / divisor * scale);
        }

        result.ServerCost += c.Cost;
        result.Ledger.Add(dayKey, model!, c);
        if (utc > result.CoveredUntilUtc) result.CoveredUntilUtc = utc;
    }

    /// <summary>读数值字段。支持逗号分隔的多个路径并求和（例如缓存写分 5m 与 1h 两档）。</summary>
    private static long ReadNumber(JsonElement item, string paths, double scale)
    {
        if (string.IsNullOrWhiteSpace(paths)) return 0;
        double sum = 0;
        foreach (var raw in paths.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            // 解析出的内嵌文档必须比取到的 JsonElement 活得更久，所以在本轮循环里持有再释放
            var docs = new List<JsonDocument>();
            try
            {
                if (!TryNavigateDeep(item, raw.Trim(), out var el, docs)) continue;
                sum += el.ValueKind switch
                {
                    JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
                    JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
                        ? p : 0,
                    _ => 0,
                };
            }
            finally
            {
                foreach (var d in docs) d.Dispose();
            }
        }
        if (sum == 0) return 0;
        var s = scale > 0 && !double.IsNaN(scale) && !double.IsInfinity(scale) ? scale : 1.0;
        return (long)Math.Round(sum * s, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// 与 TryNavigate 同构，但遇到「值本身是一段 JSON 文本」时会自动解开再继续往下走——
    /// New API 的 other 字段就是字符串里包的 JSON（cache_tokens / cache_ratio 都在里面）。
    /// 解出来的文档放进调用方持有的 keep 里：JsonElement 不能比它的 JsonDocument 活得更久。
    /// </summary>
    internal static bool TryNavigateDeep(JsonElement root, string path, out JsonElement found, List<JsonDocument> keep)
    {
        found = default;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var cur = root;
        var i = 0;
        while (i < path.Length)
        {
            if (cur.ValueKind == JsonValueKind.String)
            {
                var s = cur.GetString();
                if (string.IsNullOrWhiteSpace(s) || (s[0] != '{' && s[0] != '[')) return false;
                JsonDocument parsed;
                try { parsed = JsonDocument.Parse(s); }
                catch (JsonException) { return false; }
                keep.Add(parsed);
                cur = parsed.RootElement;
            }

            var start = i;
            while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
            if (i > start)
            {
                if (cur.ValueKind != JsonValueKind.Object) return false;
                if (!cur.TryGetProperty(path.Substring(start, i - start), out cur)) return false;
            }

            while (i < path.Length && path[i] == '[')
            {
                var close = path.IndexOf(']', i);
                if (close < 0) return false;
                if (!int.TryParse(path.AsSpan(i + 1, close - i - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                    return false;
                if (cur.ValueKind != JsonValueKind.Array) return false;
                JsonElement? hit = null;
                var pos = -1;
                foreach (var el in cur.EnumerateArray())
                {
                    pos++;
                    if (pos != idx) continue;
                    hit = el;
                    break;
                }
                if (hit is null || idx < 0) return false;
                cur = hit.Value;
                i = close + 1;
            }

            if (i < path.Length && path[i] == '.') { i++; continue; }
            if (i >= path.Length) break;
            if (path[i] != '[') return false;
        }

        found = cur;
        return true;
    }

    /// <summary>
    /// 发请求前的地址卡控：必须是 http/https 绝对地址，且带密钥时禁止走明文。
    /// 这一条是真实风险：BaseUrl 填错就等于把环境变量里的密钥发给别人。
    /// </summary>
    internal static void AssertSafeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
            throw new ProviderException($"接口地址不是合法 URL：{url}", permanent: true);
        var https = string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        var http = string.Equals(u.Scheme, "http", StringComparison.OrdinalIgnoreCase);
        if (!https && !http)
            throw new ProviderException($"只允许 http/https，当前是「{u.Scheme}」", permanent: true);
        if (!https && !IsLoopback(u))
            throw new ProviderException("BaseUrl 是明文 http，会把密钥连带送出；请改用 https（本地调试可用 127.0.0.1）", permanent: true);
    }

    private static bool IsLoopback(Uri u)
        => u.IsLoopback
           || string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase)
           || u.Host.StartsWith("127.", StringComparison.Ordinal);

    /// <summary>
    /// 从 2xx 响应体里提炼业务层错误：success/ok 为 false、code 非 0/200 且带 msg、
    /// 或 error 字段。返回空串表示没发现问题。
    /// </summary>
    internal static string ApiBusinessError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return string.Empty;

        if (root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False)
            return FirstMessage(root) ?? "success=false";
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            return FirstMessage(root) ?? "ok=false";

        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number)
        {
            var c = code.GetDecimal();
            if (c != 0m && c != 200m)
            {
                var msg = FirstMessage(root);
                if (msg is not null) return $"code={c}：{msg}";
            }
        }

        if (root.TryGetProperty("error", out var err))
        {
            if (err.ValueKind == JsonValueKind.String && err.GetString() is { Length: > 0 } es)
                return Trunc(es);
            if (err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("message", out var em)
                && em.ValueKind == JsonValueKind.String
                && em.GetString() is { Length: > 0 } ees)
                return Trunc(ees);
        }

        return string.Empty;
    }

    /// <summary>非 2xx 时从响应体里挑出最有用的那句话：能按 JSON 解析就走业务层提炼，
    /// 解析不了或提炼不出东西才退回截断的原文。把整坨 JSON 塞进提示里用户读不动。</summary>
    private static async Task<string> ErrorDetailAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await SafeReadBodyAsync(resp, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var biz = ApiBusinessError(doc.RootElement);
            if (!string.IsNullOrEmpty(biz)) return biz;
        }
        catch (JsonException) { }

        return Trunc(body);
    }

    private static string? FirstMessage(JsonElement root)
    {
        foreach (var name in new[] { "msg", "message", "error_msg", "errMsg" })
        {
            if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                && el.GetString() is { Length: > 0 } s) return Trunc(s);
        }
        if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String)
            return Trunc(em.GetString() ?? string.Empty);
        return null;
    }

    private static string Trunc(string s)
    {
        var flat = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length > 160 ? flat[..160] + "…" : flat;
    }

    /// <summary>
    /// 按路径取值，支持点号与数组下标：{@code a.b[0].c}。
    /// 下标支持是必要的：DeepSeek 余额就在 balance_infos[0].total_balance 这种位置。
    /// </summary>
    internal static bool TryNavigate(JsonElement root, string path, out JsonElement found)
    {
        found = root;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var cur = root;
        var i = 0;
        while (i < path.Length)
        {
            var start = i;
            while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
            if (i > start)
            {
                if (cur.ValueKind != JsonValueKind.Object) return false;
                if (!cur.TryGetProperty(path.Substring(start, i - start), out cur)) return false;
            }

            while (i < path.Length && path[i] == '[')
            {
                var close = path.IndexOf(']', i);
                if (close < 0) return false;
                if (!int.TryParse(path.AsSpan(i + 1, close - i - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                    return false;
                if (cur.ValueKind != JsonValueKind.Array) return false;

                var pos = -1;
                JsonElement? hit = null;
                foreach (var el in cur.EnumerateArray())
                {
                    pos++;
                    if (pos != idx) continue;
                    hit = el;
                    break;
                }
                if (hit is null || idx < 0) return false;
                cur = hit.Value;
                i = close + 1;
            }

            if (i < path.Length && path[i] == '.') { i++; continue; }
            if (i >= path.Length) break;
            if (path[i] != '[') return false;
        }

        found = cur;
        return true;
    }

    /// <summary>
    /// 规划本轮要拉的时间窗口：从水位线滚到「现在 - 滞后带」，跨度超过 WindowMaxDays 就切段，
    /// 最多 MaxWindowsPerRun 段（防止首次同步把界面卡死）。抽成纯函数是为了能单独测。
    /// </summary>
    public static List<(DateTime From, DateTime To)> PlanWindows(DateTime? watermarkUtc, DateTime nowUtc, ProviderConfig cfg)
    {
        var trustUntil = nowUtc.AddMinutes(-cfg.LagMinutes);
        var from = watermarkUtc ?? trustUntil.AddDays(-cfg.InitialBackfillDays);
        if (from > trustUntil) from = trustUntil;

        var plan = new List<(DateTime, DateTime)>();
        while (from < trustUntil && plan.Count < cfg.MaxWindowsPerRun)
        {
            var to = from.AddDays(cfg.WindowMaxDays);
            if (to > trustUntil) to = trustUntil;
            plan.Add((from, to));
            from = to;
        }
        return plan;
    }

    private static string BuildUrl(ProviderConfig cfg, DateTime fromUtc, DateTime toUtc, string cursor)
    {
        var q = new StringBuilder();
        Append(q, cfg.StartParam, FormatTime(cfg, fromUtc));
        Append(q, cfg.EndParam, FormatTime(cfg, toUtc));
        if (!string.IsNullOrWhiteSpace(cfg.ExtraQuery))
        {
            foreach (var pair in cfg.ExtraQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2) AppendRaw(q, kv[0].Trim(), kv[1].Trim());
            }
        }
        if (!string.IsNullOrEmpty(cursor) && !string.IsNullOrWhiteSpace(cfg.PageParam))
            Append(q, cfg.PageParam, cursor);
        if (!string.IsNullOrWhiteSpace(cfg.PageSizeParam) && cfg.PageSize > 0)
            AppendRaw(q, cfg.PageSizeParam.Trim(), cfg.PageSize.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(cfg.LimitParam) && cfg.LimitPerRequest > 0)
            AppendRaw(q, cfg.LimitParam.Trim(), cfg.LimitPerRequest.ToString(CultureInfo.InvariantCulture));

        // Path 里已经自带 ?（如 Gemini 的 /x?key=*** 形态）时用 & 接，否则会拼出两个问号
        var joiner = (cfg.Path ?? string.Empty).Contains('?') ? "&" : "?";
        return cfg.BaseUrl.TrimEnd('/') + cfg.Path + (q.Length > 0 ? joiner + q : string.Empty);
    }

    private static string FormatTime(ProviderConfig cfg, DateTime utc)
    {
        var kind = (cfg.TimeFormat ?? string.Empty).Trim().ToLowerInvariant();
        if (kind == "unix")
            return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        if (kind == "rfc3339")
            return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static void Append(StringBuilder q, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        AppendRaw(q, name, Uri.EscapeDataString(value));
    }

    private static void AppendRaw(StringBuilder q, string name, string encodedValue)
    {
        if (q.Length > 0) q.Append('&');
        q.Append(name).Append('=').Append(encodedValue);
    }

    public void Dispose() => _http.Dispose();
}
