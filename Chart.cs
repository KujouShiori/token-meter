namespace TokenMeter;

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
