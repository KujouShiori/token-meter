using System.Globalization;
using System.Text.Json;

namespace TokenMeter;

/// <summary>
/// 单个模型的单价。四个价格都按「每 PerTokens 个 token」计，
/// 跟服务商页面上写的「元 / 百万 tokens」一致，用户照抄即可。
/// </summary>
public sealed class ModelPrice
{
    public string Model { get; set; } = "";

    /// <summary>输入（不含缓存）。</summary>
    public decimal Input { get; set; }

    /// <summary>输出（含思考 token，日志里的 reasoning 已计在 output 内）。</summary>
    public decimal Output { get; set; }

    /// <summary>缓存读（命中）。</summary>
    public decimal CacheRead { get; set; }

    /// <summary>缓存写。</summary>
    public decimal CacheWrite { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsZero => Input == 0m && Output == 0m && CacheRead == 0m && CacheWrite == 0m;

    public string Summary(string currency, long per)
        => $"入 {Format.Money(currency, Input)}/{ShortPer(per)} · 出 {Format.Money(currency, Output)}"
         + $" · 缓存读 {Format.Money(currency, CacheRead)} · 缓存写 {Format.Money(currency, CacheWrite)}";

    private static string ShortPer(long per)
        => per >= 1_000_000 ? (per / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M"
         : per >= 1_000 ? (per / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k"
         : per.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 自定义模型单价配置。存在插件设置键 pricing 里（一段 JSON）。
/// 日志里的 cost 字段全是 0，所以费用一律由这里的单价算出来。
/// </summary>
public sealed class PricingConfig
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>货币符号，直接显示在数字前面。</summary>
    public string Currency { get; set; } = "¥";

    /// <summary>单价的计量基数，默认每 100 万 token。</summary>
    public long PerTokens { get; set; } = 1_000_000;

    public List<ModelPrice> Models { get; set; } = new();

    public static PricingConfig Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new PricingConfig();
        try
        {
            var cfg = JsonSerializer.Deserialize<PricingConfig>(json, JsonOpts);
            if (cfg is null) return new PricingConfig();
            cfg.Models ??= new List<ModelPrice>();
            cfg.Models.RemoveAll(m => string.IsNullOrWhiteSpace(m.Model));
            if (string.IsNullOrWhiteSpace(cfg.Currency)) cfg.Currency = "¥";
            if (cfg.PerTokens <= 0) cfg.PerTokens = 1_000_000;
            return cfg;
        }
        catch (Exception)
        {
            // 配置写坏了也不能让整个插件起不来：退回默认值，调用方会记日志
            return new PricingConfig();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public ModelPrice? Find(string model)
        => Models.FirstOrDefault(m => string.Equals(m.Model, model, StringComparison.OrdinalIgnoreCase));

    public bool TryFind(string model, out ModelPrice price)
    {
        var found = Find(model);
        price = found ?? new ModelPrice { Model = model };
        return found is not null;
    }

    /// <summary>
    /// 按单价算一组计数的费用。返回 false 表示这个模型没配单价
    /// —— 此时费用按 0 计，但界面要标成「未定价」而不是「免费」。
    /// </summary>
    public bool TryCost(string model, Counters c, out decimal cost)
    {
        if (!TryFind(model, out var p))
        {
            cost = 0m;
            return false;
        }
        var per = (decimal)PerTokens;
        cost = (c.Input * p.Input + c.Output * p.Output + c.CacheRead * p.CacheRead + c.CacheWrite * p.CacheWrite) / per;
        return true;
    }

    /// <summary>新增或覆盖一个模型的单价（按名字大小写无关）。</summary>
    public void Upsert(ModelPrice price)
    {
        var old = Find(price.Model);
        if (old is null) Models.Add(price);
        else Models[Models.IndexOf(old)] = price;
    }

    public bool Remove(string model)
    {
        var old = Find(model);
        return old is not null && Models.Remove(old);
    }

    /// <summary>把界面输入的字符串解析成金额；空串当 0，解析失败当 null（调用方提示用户）。</summary>
    public static bool TryParseAmount(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var cleaned = text.Replace(",", string.Empty).Replace("￥", string.Empty).Replace("¥", string.Empty).Trim();
        return decimal.TryParse(cleaned, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value)
            || decimal.TryParse(cleaned, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value);
    }
}
