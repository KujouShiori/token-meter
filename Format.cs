using System.Globalization;

namespace TokenMeter;

/// <summary>显示用的数字格式化：token 用量、金额、百分比、字节量、相对时间。</summary>
public static class Format
{
    /// <summary>把 token 数压成短串：842 / 24.5k / 1.32M / 2.1B。</summary>
    public static string Tokens(long n)
    {
        if (n < 1000L) return n.ToString(CultureInfo.InvariantCulture);
        if (n < 1_000_000L) return (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        if (n < 1_000_000_000L) return (n / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        return (n / 1_000_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + "B";
    }

    /// <summary>金额：越大位数越少，小额多给几位（否则 0.0004 会显示成 0）。千位统一加分隔符。</summary>
    public static string Money(string currency, decimal v)
    {
        var negative = v < 0m;
        var a = Math.Abs(v);
        var body = a >= 10_000m ? a.ToString("#,0", CultureInfo.InvariantCulture)
                 : a >= 100m    ? a.ToString("#,0.0", CultureInfo.InvariantCulture)
                 : a >= 1m      ? a.ToString("#,0.00", CultureInfo.InvariantCulture)
                 : a == 0m      ? "0"
                                : a.ToString("#,0.0000", CultureInfo.InvariantCulture);
        return (negative ? "-" : string.Empty) + currency + body;
    }

    /// <summary>只给数字，不带货币符号（表格里对齐用）。</summary>
    public static string Amount(decimal v)
    {
        var a = Math.Abs(v);
        return a >= 100m ? a.ToString("0.0", CultureInfo.InvariantCulture)
             : a >= 1m    ? a.ToString("0.00", CultureInfo.InvariantCulture)
             : a == 0m    ? "0"
                          : a.ToString("0.0000", CultureInfo.InvariantCulture);
    }

    public static string Percent(decimal v) => v.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    /// <summary>字节量：12.3 MB / 480 KB。</summary>
    public static string Bytes(long n)
    {
        if (n < 1024) return n + " B";
        if (n < 1024L * 1024) return (n / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        if (n < 1024L * 1024 * 1024) return (n / 1048576.0).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
        return (n / 1073741824.0).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
    }

    /// <summary>相对时间：刚刚 / 37 秒前 / 5 分钟前 / 3 小时前 / 2 天前。</summary>
    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalSeconds < 0) return "刚刚";
        if (d.TotalSeconds < 5) return "刚刚";
        if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds} 秒前";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} 分钟前";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} 小时前";
        return $"{(int)d.TotalDays} 天前";
    }

    public static string Clock(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>日期标签：MM-dd。</summary>
    public static string DayLabel(DateOnly day)
        => day.ToString("MM-dd", CultureInfo.InvariantCulture);

    public static string DayKey(DateOnly day)
        => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
