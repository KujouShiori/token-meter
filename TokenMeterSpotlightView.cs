using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace TokenMeter;

/// <summary>
/// 点岛体弹出的聚光卡：岛上装不下的东西全放这里（近 7 天趋势、逐模型完整明细、缓存命中）。
///
/// 三条要点（技能 §16 / §18）：
///   1. 它是独立于岛视图的另一棵可视树，不能复用岛上的那个 UIElement（会白屏）；
///   2. 不需要实现 IMorphView，飞入飞回、遮罩、圆角、层级都由宿主负责；
///   3. 配色同样跟着岛体明暗走 —— 卡片也是浅/深两套都要读得清。
/// </summary>
public sealed class TokenMeterSpotlightView : UserControl
{
    /// <summary>几何缩放：0.7 = 卡片比原来小 30%。只管内边距、间距、柱高这些不随字号变的东西。</summary>
    private const double LayoutScale = 0.7;

    /// <summary>文字缩放：0.9 = 标题与 KPI 在原始字号上略微收一点。</summary>
    private const double TextScale = 0.9;

    /// <summary>图表与明细表单独再放大一档（用户反馈这两块偏小）。</summary>
    private const double DataTextScale = 1.15;

    private static double S(double v) => v * LayoutScale;
    private static double T(double v) => v * TextScale;
    private static double D(double v) => v * DataTextScale;

    private const double BarAreaHeight = 112 * DataTextScale;

    /// <summary>除首列（星型，模型名）外六列的基准宽：请求/输入/输出/缓存读/合计/费用。
    /// 等宽会把模型名挤死，改成按每列内容实际长度分配。</summary>
    private static readonly double[] TableColWidths = { 36, 44, 50, 56, 56, 74 };

    private readonly IIslandTheme _theme;
    private readonly TextBlock _kpiTodayCost;
    private readonly TextBlock _kpiTodayTokens;
    private readonly TextBlock _kpiWeekCost;
    private readonly TextBlock _kpiMonthCost;
    private readonly TextBlock _kpiAllCost;
    private readonly TextBlock _kpiHit;
    private readonly StackPanel _bars;
    private readonly StackPanel _table;
    private readonly StackPanel _extra;
    private readonly TextBlock _source;
    private readonly Button _refresh;

    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _dividerBrush = new();
    private readonly SolidColorBrush _accentBrush = new();
    private readonly SolidColorBrush _barBrush = new();
    private readonly SolidColorBrush _barTrackBrush = new();

    private Snapshot? _last;
    public event Action? RefreshRequested;

    public TokenMeterSpotlightView(PluginManifest manifest, IIslandTheme theme)
    {
        _theme = theme;
        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;

        var title = new TextBlock
        {
            Text = manifest.Name,
            FontSize = T(24),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _refresh = new Button
        {
            Content = "立即刷新",
            VerticalAlignment = VerticalAlignment.Center,
        };
        _refresh.Click += (_, _) => RefreshRequested?.Invoke();

        var titleRow = new Grid { ColumnSpacing = S(12) };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 0);
        Grid.SetColumn(_refresh, 1);
        titleRow.Children.Add(title);
        titleRow.Children.Add(_refresh);

        (_kpiTodayCost, _kpiTodayTokens) = KpiPair("今日", out var todayBox);
        _kpiWeekCost = Kpi("本周", out var weekBox);
        _kpiMonthCost = Kpi("本月", out var monthBox);
        _kpiAllCost = Kpi("累计", out var allBox);
        _kpiHit = Kpi("今日缓存命中", out var hitBox);

        var kpiRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = S(28) };
        kpiRow.Children.Add(todayBox);
        kpiRow.Children.Add(weekBox);
        kpiRow.Children.Add(monthBox);
        kpiRow.Children.Add(allBox);
        kpiRow.Children.Add(hitBox);

        // 柱状图整排居中
        _bars = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = S(10),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var chartLabel = new TextBlock { Text = "近 7 天用量", FontSize = D(13), Foreground = _mutedBrush };
        _table = new StackPanel { Spacing = S(2) };
        // 第二个及以后的来源分列在这里：不同运营商、不同币种，绝不合并相加
        _extra = new StackPanel { Spacing = S(10) };
        var tableLabel = new TextBlock { Text = "今日逐模型明细", FontSize = D(13), Foreground = _mutedBrush };

        _source = new TextBlock { FontSize = T(11), Foreground = _faintBrush, TextWrapping = TextWrapping.Wrap };

        var root = new StackPanel
        {
            Spacing = S(14),
            Padding = new Thickness(S(30), S(24), S(30), S(24)),
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        root.Children.Add(titleRow);
        root.Children.Add(_source);
        root.Children.Add(Divider());
        root.Children.Add(kpiRow);
        root.Children.Add(Divider());
        root.Children.Add(chartLabel);
        root.Children.Add(_bars);
        root.Children.Add(Divider());
        root.Children.Add(tableLabel);
        root.Children.Add(_table);
        root.Children.Add(_extra);

        Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0),
        };
    }

    /// <summary>宿主收起卡片时的收尾。这里没有定时器和网络请求，只需丢掉引用。</summary>
    public void OnHostClosed() => _last = null;

    public void Apply(Snapshot s)
    {
        _last = s;
        Rebuild();
    }

    private Border Divider() => new()
    {
        Height = 1,
        Background = _dividerBrush,
        Margin = new Thickness(0, S(2), 0, S(2)),
    };

    private (TextBlock value, TextBlock sub) KpiPair(string label, out StackPanel container)
    {
        var stack = new StackPanel { Spacing = S(2) };
        var value = new TextBlock
        {
            Text = "—",
            FontSize = T(22),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = _textBrush,
        };
        var sub = new TextBlock { Text = "", FontSize = T(11), Foreground = _faintBrush };
        stack.Children.Add(value);
        stack.Children.Add(new TextBlock { Text = label, FontSize = T(11), Foreground = _mutedBrush });
        stack.Children.Add(sub);
        container = stack;
        return (value, sub);
    }

    private TextBlock Kpi(string label, out StackPanel container)
    {
        var stack = new StackPanel { Spacing = S(2) };
        var value = new TextBlock
        {
            Text = "—",
            FontSize = T(22),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = _textBrush,
        };
        stack.Children.Add(value);
        stack.Children.Add(new TextBlock { Text = label, FontSize = T(11), Foreground = _mutedBrush });
        container = stack;
        return value;
    }

    private void Rebuild()
    {
        var s = _last;
        if (s is null) return;

        var cur = s.Currency;
        var todayPriced = s.Today.Models - s.Today.UnpricedModels;

        // 口径变了必须看得出来：接口挂了在后台静默降级比报错更难发现
        _source.Text = (s.Blocks.Count > 1
                ? string.Join(" ＋ ", s.Blocks.Select(x => x.Name))
                : s.SourceMode)
            + $" ｜ 主块费用口径：{s.Today.CostBasis}" + (s.CoveredUntilUtc is null
            ? string.Empty
            : $"（云端覆盖到 {Format.Ago(s.CoveredUntilUtc.Value)}）")
            + (string.IsNullOrEmpty(s.SplitNote) ? string.Empty : " · " + s.SplitNote)
            + (string.IsNullOrEmpty(s.ProviderNote) ? string.Empty : " · " + s.ProviderNote);

        _kpiTodayCost.Text = s.HasData
            ? (todayPriced > 0 ? Format.Money(cur, s.Today.Cost) : "未定价")
            : "—";
        _kpiTodayTokens.Text = $"{Format.Tokens(s.Today.C.Total)} tok · {s.Today.C.Requests} 次请求";
        _kpiWeekCost.Text = s.HasData ? Format.Money(cur, s.Week.Cost) : "—";
        _kpiMonthCost.Text = s.HasData ? Format.Money(cur, s.Month.Cost) : "—";
        _kpiAllCost.Text = s.HasData ? Format.Money(cur, s.All.Cost) : "—";
        _kpiHit.Text = $"{Format.Percent(s.Today.C.CacheHitRate)}";

        RebuildBars(s, cur);
        RebuildTable(s);
        RebuildExtraBlocks(s);
    }

    /// <summary>把除主来源以外的每个源单独成块展示，各自带币种与口径。</summary>
    private void RebuildExtraBlocks(Snapshot s)
    {
        _extra.Children.Clear();
        if (s.Blocks.Count <= 1) return;

        for (var i = 1; i < s.Blocks.Count; i++)
        {
            var b = s.Blocks[i];
            _extra.Children.Add(Divider());

            var priced = b.Today.Models - b.Today.UnpricedModels;
            var todayCost = !b.HasData
                ? "—"
                : (b.Today.CostBasis == "本地估算" && priced <= 0 ? "未定价" : Format.Money(b.Currency, b.Today.Cost));

            _extra.Children.Add(new TextBlock
            {
                Text = b.Name,
                FontSize = D(13),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = _textBrush,
            });
            _extra.Children.Add(new TextBlock
            {
                Text = $"今日 {todayCost} · {Format.Tokens(b.Today.C.Total)} tok ｜ 本周 {Format.Money(b.Currency, b.Week.Cost)} ｜ 本月 {Format.Money(b.Currency, b.Month.Cost)} ｜ 累计 {Format.Money(b.Currency, b.All.Cost)} ｜ 费用口径：{b.Today.CostBasis} ｜ 共 {b.LedgerDays} 天",
                FontSize = D(11.5),
                TextWrapping = TextWrapping.Wrap,
                Foreground = _mutedBrush,
            });
        }
    }

    private void RebuildBars(Snapshot s, string cur)
    {
        _bars.Children.Clear();
        if (s.Last7.Count == 0) return;

        var maxTokens = s.Last7.Select(p => p.Total).DefaultIfEmpty(0).Max();
        var maxCost = s.Last7.Select(p => p.Cost).DefaultIfEmpty(0m).Max();
        var useCost = maxCost > 0m;
        decimal maxV = useCost ? maxCost : Math.Max(1, maxTokens);

        var barWidth = D(46);
        var colWidth = D(62);
        var corner = new CornerRadius(S(4));

        foreach (var p in s.Last7)
        {
            var v = useCost ? p.Cost : p.Total;
            var h = maxV <= 0 ? S(2)
                 : Math.Clamp((double)(v / maxV) * BarAreaHeight, p.Total > 0 ? S(6) : S(2), BarAreaHeight);

            var track = new Border
            {
                Height = BarAreaHeight,
                Width = barWidth,
                Background = _barTrackBrush,
                CornerRadius = corner,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = new Border
                {
                    Height = Math.Max(S(2), h),
                    Width = barWidth,
                    Background = _barBrush,
                    CornerRadius = corner,
                    VerticalAlignment = VerticalAlignment.Bottom,
                },
            };

            var col = new StackPanel { Spacing = S(3), Width = colWidth };
            col.Children.Add(new TextBlock
            {
                // 金额写全，靠加粗柱子带来的列宽容纳
                Text = useCost ? Format.Money(cur, p.Cost) : Format.Tokens(p.Total),
                FontSize = D(10.5),
                Foreground = _mutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
            });
            col.Children.Add(new Grid { Height = BarAreaHeight, Children = { track } });
            col.Children.Add(new TextBlock
            {
                Text = Format.DayLabel(p.Day),
                FontSize = D(10.5),
                Foreground = p.HasUnpriced ? _faintBrush : _mutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
            });
            _bars.Children.Add(col);
        }
    }

    private void RebuildTable(Snapshot s)
    {
        _table.Children.Clear();

        var blocks = s.Blocks;
        var multi = blocks.Count > 1;

        if (blocks.Count == 0 || blocks.All(b => b.TodayByModel.Count == 0))
        {
            _table.Children.Add(new TextBlock
            {
                Text = "今天还没有可统计的模型记录。",
                FontSize = D(12),
                Foreground = _faintBrush,
            });
            return;
        }

        _table.Children.Add(TableRow("模型", "请求", "输入", "输出", "缓存读", "合计", "费用", header: true));

        var shown = 0;
        foreach (var b in blocks)
        {
            // 多来源时在每组前插一行归属标签：两个运营商的钱包在一张表里，必须能看出哪行是谁的
            if (multi)
            {
                _table.Children.Add(new TextBlock
                {
                    Text = $"{b.Name} ｜ {b.Currency} ｜ {b.Today.CostBasis}",
                    FontSize = D(11),
                    Foreground = _accentBrush,
                    Margin = new Thickness(0, S(6), 0, S(2)),
                });
            }

            if (b.TodayByModel.Count == 0)
            {
                _table.Children.Add(TableRow(
                    "（今日无用量）", "—", "—", "—", "—", "—",
                    $"累计 {Format.Money(b.Currency, b.All.Cost)}",
                    header: false));
                continue;
            }

            foreach (var m in b.TodayByModel)
            {
                if (shown >= 12)
                {
                    _table.Children.Add(new TextBlock
                    {
                        Text = $"…另有 {b.TodayByModel.Count - shown} 个模型未列出",
                        FontSize = D(11),
                        Foreground = _faintBrush,
                        Margin = new Thickness(0, S(4), 0, 0),
                    });
                    break;
                }
                // 服务端报了金额就优先用它，否则才是本地单价估算；用各自来源的币种
                var serverCost = m.C.FromServer && m.C.Cost > 0;
                _table.Children.Add(TableRow(
                    m.Model,
                    m.C.Requests.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Format.Tokens(m.C.Input),
                    Format.Tokens(m.C.Output),
                    Format.Tokens(m.C.CacheRead),
                    Format.Tokens(m.C.Total),
                    serverCost
                        ? Format.Money(b.Currency, (decimal)m.C.Cost)
                        : (m.Priced ? Format.Money(b.Currency, m.Cost) : "未定价"),
                    header: false));
                shown++;
            }
        }
    }

    private UIElement TableRow(string c0, string c1, string c2, string c3, string c4, string c5, string c6, bool header)
    {
        var grid = new Grid { ColumnSpacing = S(8), Padding = new Thickness(0, S(2), 0, S(2)) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var w in TableColWidths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(D(w)) });

        var brush = header ? _faintBrush : _textBrush;
        var texts = new[] { c0, c1, c2, c3, c4, c5, c6 };
        for (var i = 0; i < texts.Length; i++)
        {
            var tb = new TextBlock
            {
                Text = texts[i],
                FontSize = header ? D(11) : D(12),
                FontWeight = header ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = i == 0 ? brush : (header ? _faintBrush : (i == 6 ? _accentBrush : _mutedBrush)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                TextAlignment = i == 0 ? TextAlignment.Left : TextAlignment.Right,
            };
            Grid.SetColumn(tb, i);
            grid.Children.Add(tb);
        }
        return grid;
    }

    private void ApplyThemeColors()
    {
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(195);
        _faintBrush.Color = Neutral(140);
        _dividerBrush.Color = Neutral(34);
        _barTrackBrush.Color = Neutral(26);
        _accentBrush.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(255, 0, 96, 192)
            : Windows.UI.Color.FromArgb(255, 108, 200, 255);
        _barBrush.Color = _accentBrush.Color;
    }

    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);
}
