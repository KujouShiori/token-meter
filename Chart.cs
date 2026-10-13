namespace TokenMeter;

/// <summary>
/// 明细表的列宽分配。列宽原来是一串写死的数（36/44/50/56/56/74），
/// 结果「输入」那列只有 60px，而 2.4 亿 token 格式化出来是 <c>244.75M</c> 七个字符 ≈ 63px，
/// 于是被省略号切成 <c>244.7…</c> —— 数字没错，是列宽按猜的写死、没跟着实际字符串走。
/// 所以这里改成按「这一列里最长的文本有多宽」算，并且做成纯函数：
/// 字体度量在测试工程里拿不到（WinUI 的 FormattedText 起不来），只能估，
/// 但"够不够宽"这个判断本身可以断言。
/// </summary>
public static class TableLayout
{
    /// <summary>六列（请求/输入/输出/缓存读/合计/费用）的**基准宽度下限**（未缩放）。
    /// 放在这里而不是视图里，是为了让断言和界面用同一组数 —— 测试里另抄一份就会漂。</summary>
    public static readonly double[] BaseMin = { 30, 34, 34, 34, 34, 40 };

    /// <summary>
    /// 上限（未缩放）。取数的依据是"这一列现实里最长的字符串有多宽"：
    /// 费用列 <c>¥1,701.3</c> 约 4.6em、token 列 <c>244.75M</c> 约 4.1em、请求列 <c>999,999</c> 约 3.9em。
    /// ⚠ 这组数必须满足：六列全撑到上限 + 六个列间距 + 首列下限 &lt;= 卡片可用宽，
    ///   否则最坏情况整张表放不下、首列被挤没。有断言直接盯着这条（曾经写松过一回）。
    /// </summary>
    public static readonly double[] BaseMax = { 48, 52, 52, 52, 52, 58 };

    /// <summary>首列（模型名）至少留出的基准宽度：deepseek-v4-flash 不能被挤成三个字。</summary>
    public const double BaseFirstColMin = 94;

    /// <summary>
    /// 单字宽度估算（单位 px）。分四类：中日韩全角 ≈ 1.0em、数字与常见符号 ≈ 0.58em、
    /// 窄标点 ≈ 0.32em、货币与百分号这类宽符号 ≈ 0.92em。
    /// 刻意估得偏宽一点：宁可留白，也不要又切掉数字。
    /// </summary>
    public static double GlyphWidth(char ch, double fontPx)
    {
        if (ch >= 0x2E80) return fontPx;                       // CJK 与全角标点（含破折号 —、括号）
        if (ch is >= '0' and <= '9') return fontPx * 0.60;      // 数字（Segoe 的数字是等宽的）
        if (ch is '.' or ',' or ':' or ';' or '\'' or '-' or ' ') return fontPx * 0.34;
        if (ch is '¥' or '￥' or '$' or '%' or '#' or '@') return fontPx * 0.92;
        if (char.IsUpper(ch)) return fontPx * 0.72;             // M / B 这类单位字母
        return fontPx * 0.56;                                   // 其余拉丁字母
    }

    public static double TextWidth(string? text, double fontPx)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var w = 0.0;
        foreach (var ch in text) w += GlyphWidth(ch, fontPx);
        return w;
    }

    /// <summary>
    /// 每列取「表头与所有数据行里最宽的那条」，再夹到 [min, max] 之间。
    /// max 存在的意义是防止某个异常长的模型名或金额把整张表撑破 —— 首列（模型名）是星型列，
    /// 数字列一宽，它就被挤成省略号，那是更糟的失败方式。
    /// </summary>
    public static double[] ColumnWidths(
        IReadOnlyList<string> header, IReadOnlyList<string[]> rows, double fontPx,
        IReadOnlyList<double> minPx, IReadOnlyList<double> maxPx)
    {
        var cols = header.Count;
        var widths = new double[cols];
        for (var c = 0; c < cols; c++)
        {
            var need = TextWidth(header[c], fontPx);
            foreach (var r in rows)
                if (c < r.Length) need = Math.Max(need, TextWidth(r[c], fontPx));

            widths[c] = Math.Clamp(need, minPx[c], maxPx[c]);
        }
        return widths;
    }

    /// <summary>数字列加起来（含列间距）是否还留得下首列。留给首列不足 minWidth 就是放不下。</summary>
    public static bool Fits(IReadOnlyList<double> numericWidths, double spacingPx, double usableWidth, double firstColMinWidth)
    {
        var sum = numericWidths.Sum() + spacingPx * numericWidths.Count;
        return usableWidth - sum >= firstColMinWidth;
    }
}

/// <summary>
/// 近 7 天柱状图的几何分配。两种画法：
///   · grouped（分组）= 一天里每个来源一根柱并排 —— 能直接比谁的形状，但两家量级差几十倍时，
///     小的那根几乎看不见；
///   · stacked（堆叠）= 一天一根柱、按来源分色叠起来 —— 总量一眼可读，小来源也占得到厚度。
///
/// 两种都用 **token 数**做刻度：token 是跨来源、跨币种可比的量，而 ¥ 和 $ 不能比大小也不能相加。
/// 金额只进标签文字，各来源各一行，绝不合成一个数。
///
/// 这段算术单独放一个文件，是因为它可测：卡片横向不滚动，所以"来源再多也不能挤出右缘"
/// 是条必须被断言守住的硬约束，而不是等部署上去用眼睛数。
/// </summary>
public static class ChartLayout
{
    public const string Grouped = "grouped";
    public const string Stacked = "stacked";

    /// <summary>模式归一：认大小写与首尾空格，填错一律回到 grouped（默认值），不猜别的。</summary>
    public static string NormalizeMode(string? raw)
        => string.Equals((raw ?? string.Empty).Trim(), Stacked, StringComparison.OrdinalIgnoreCase)
            ? Stacked : Grouped;

    /// <summary>一天可用宽度 = 图表可用宽 / 天数。星型列均分，所以这就是每列真实能占的宽。</summary>
    public static double PerDayWidth(double usableWidth, int days)
        => days <= 0 ? 0 : usableWidth / days;

    /// <summary>
    /// 分组模式的柱宽：N 根柱加 (N-1) 个间距正好占满一列，并夹在 [min, max] 之间。
    /// 写死柱宽的话第三个来源起就会挤出卡片右缘 —— 卡片宽度不能跟着接口数量长。
    /// </summary>
    public static double GroupedBarWidth(double perDayWidth, int sources, double spacing, double max, double min)
    {
        if (sources <= 0) return max;
        var room = (perDayWidth - spacing * (sources - 1)) / sources;
        return Math.Clamp(room, min, max);
    }

    /// <summary>堆叠模式只剩一根柱，可以比分组宽些，但仍给一列留出两侧空白。</summary>
    public static double StackWidth(double perDayWidth, double max, double min)
        => Math.Clamp(perDayWidth * 0.6, min, max);

    /// <summary>
    /// 堆叠模式各段的厚度，自下而上对应自早到晚的来源顺序（本地在最底）。
    ///
    /// <paramref name="maxDayTotal"/> 必须是**所有天里「各来源相加」的最大值**，不是单个来源的最大值：
    /// 用后者做刻度的话，一天里几家叠起来会顶破柱区高度。
    /// 某一家当天是 0 → 厚度 0，不占位。这里刻意不做「最小可见厚度」的兜底：
    /// 堆叠讲的就是比例，一家只占 0.3% 时画成一格粗反而是在编造信息。
    /// </summary>
    public static double[] StackHeights(IReadOnlyList<double> tokensPerSource, double maxDayTotal, double areaHeight)
    {
        var heights = new double[tokensPerSource.Count];
        if (maxDayTotal <= 0 || areaHeight <= 0) return heights;

        for (var i = 0; i < tokensPerSource.Count; i++)
        {
            var t = tokensPerSource[i];
            heights[i] = t > 0 ? Math.Clamp(t / maxDayTotal * areaHeight, 0, areaHeight) : 0;
        }
        return heights;
    }

    /// <summary>分组模式的刻度：用「单个来源单日」的最大值，柱高才可跨来源直接比。</summary>
    public static double GroupedHeight(double tokens, double maxSingleSource, double areaHeight, double stubHeight)
    {
        if (maxSingleSource <= 0 || areaHeight <= 0) return 0;
        return tokens > 0
            ? Math.Clamp(tokens / maxSingleSource * areaHeight, stubHeight, areaHeight)
            : 0;
    }
}
