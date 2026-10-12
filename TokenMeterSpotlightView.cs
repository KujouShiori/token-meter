using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace TokenMeter;

/// <summary>
/// 点岛体弹出的聚光卡。
///
/// 三条要点（技能 §16 / §18）：
///   1. 它是独立于岛视图的另一棵可视树，不能复用岛上的那个 UIElement（会白屏）；
///   2. 不需要实现 IMorphView，飞入飞回、遮罩、圆角、层级都由宿主负责；
///   3. 配色跟着岛体明暗走，浅/深两套都要读得清。
///
/// 多来源约定：本地日志与云端接口是**两个运营商、两种币种**，
/// 所以 KPI 按来源各一栏、柱状图按来源画双柱（柱高用 token 数这个跨币种可比的量，
/// 金额只进标签文字），明细表按来源分组。绝不把 ¥ 和 $ 加在同一个数上。
/// </summary>
public sealed class TokenMeterSpotlightView : UserControl
{
    /// <summary>整体缩放：1.2 = 卡片与内部排版一起比原来大 20%（只改这一个数就能调大小）。</summary>
    public const double Zoom = 1.2;

    /// <summary>聚光卡尺寸，供插件侧 OpenSpotlight 使用，保证与内部缩放同源。</summary>
    public const double CardWidth = 616 * Zoom;
    public const double CardHeight = 476 * Zoom;

    /// <summary>几何缩放：0.7 × Zoom。只管内边距、间距、柱高这些不随字号变的东西。</summary>
    private const double LayoutScale = 0.7 * Zoom;

    /// <summary>文字缩放：0.9 × Zoom。</summary>
    private const double TextScale = 0.9 * Zoom;

    /// <summary>图表与明细表再放大一档：1.15 × Zoom。</summary>
    private const double DataTextScale = 1.15 * Zoom;

    private static double S(double v) => v * LayoutScale;
    private static double T(double v) => v * TextScale;
    private static double D(double v) => v * DataTextScale;

    private const double BarAreaHeight = 112 * DataTextScale;

    /// <summary>除首列（星型，模型名）外六列的基准宽：请求/输入/输出/缓存读/合计/费用。</summary>
    private static readonly double[] TableColWidths = { 36, 44, 50, 56, 56, 74 };

    private readonly IIslandTheme _theme;
    private readonly StackPanel _kpiHost;
    private readonly StackPanel _bars;
    private readonly StackPanel _table;
    private readonly TextBlock _source;
    private readonly Button _refresh;

    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _dividerBrush = new();
    private readonly SolidColorBrush _accentBrush = new();
    private readonly SolidColorBrush _barBrush = new();
    private readonly SolidColorBrush _bar2Brush = new();
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

        _refresh = new Button { Content = "立即刷新", VerticalAlignment = VerticalAlignment.Center };
        _refresh.Click += (_, _) => RefreshRequested?.Invoke();

        var titleRow = new Grid { ColumnSpacing = S(12) };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 0);
        Grid.SetColumn(_refresh, 1);
        titleRow.Children.Add(title);
        titleRow.Children.Add(_refresh);

        _source = new TextBlock { FontSize = T(11), Foreground = _faintBrush, TextWrapping = TextWrapping.Wrap };

        _kpiHost = new StackPanel { Spacing = S(12) };
        _bars = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = S(10),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _table = new StackPanel { Spacing = S(2) };

        var chartLabel = new TextBlock { Text = "近 7 天用量（柱高 = token 数，金额见每柱上方）", FontSize = D(13), Foreground = _mutedBrush };
        var tableLabel = new TextBlock { Text = "今日逐模型明细", FontSize = D(13), Foreground = _mutedBrush };

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
        root.Children.Add(_kpiHost);
        root.Children.Add(Divider());
        root.Children.Add(chartLabel);
        root.Children.Add(_bars);
        root.Children.Add(Divider());
        root.Children.Add(tableLabel);
        root.Children.Add(_table);

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

    /// <summary>宿主收起卡片时的收尾。</summary>
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

    private void Rebuild()
    {
        var s = _last;
        if (s is null) return;

        _source.Text = (s.Blocks.Count > 1
                ? string.Join(" ＋ ", s.Blocks.Select(x => x.Name))
                : s.SourceMode)
            + $" ｜ 主块费用口径：{s.Today.CostBasis}"
            + (s.CoveredUntilUtc is null ? string.Empty : $"（云端覆盖到 {Format.Ago(s.CoveredUntilUtc.Value)}）")
            + (string.IsNullOrEmpty(s.SplitNote) ? string.Empty : " · " + s.SplitNote)
            + (string.IsNullOrEmpty(s.ProviderNote) ? string.Empty : " · " + s.ProviderNote);

        RebuildKpis(s);
        RebuildBars(s);
        RebuildTable(s);
    }

    // ---------------- KPI：每个来源一栏 ----------------

    private void RebuildKpis(Snapshot s)
    {
        _kpiHost.Children.Clear();
        // 两栏 KPI 必须上下对齐。WinUI 3 没有 WPF 的 SharedSizeGroup，
        // 所以改用固定权重比的星型列：两行用同一套列定义，天然对齐。
        // 权重给「今日」多留位（它下面还有一行 token · 请求数）。
        var kpiWeights = new double[] { 1.45, 1.15, 1.15, 1.15, 1.0 };

        var blocks = s.Blocks.Count > 0 ? s.Blocks : new List<SourceBlock>();

        foreach (var b in blocks)
        {
            var wrap = new StackPanel { Spacing = S(3) };

            wrap.Children.Add(new TextBlock
            {
                Text = $"{b.Name} ｜ {b.Currency} ｜ {b.Today.CostBasis}",
                FontSize = T(11),
                Foreground = _faintBrush,
            });

            var priced = b.Today.Models - b.Today.UnpricedModels;
            var todayCost = !b.HasData
                ? "—"
                : (b.Today.CostBasis == "本地估算" && priced <= 0 ? "未定价" : Format.Money(b.Currency, b.Today.Cost));

            var cells = new[]
            {
                Kpi(todayCost, "今日", $"{Format.Tokens(b.Today.C.Total)} tok · {b.Today.C.Requests} 次请求"),
                Kpi(b.HasData ? Format.Money(b.Currency, b.Week.Cost) : "—", "本周", null),
                Kpi(b.HasData ? Format.Money(b.Currency, b.Month.Cost) : "—", "本月", null),
                Kpi(b.HasData ? Format.Money(b.Currency, b.All.Cost) : "—", "累计", $"{b.LedgerDays} 天"),
                Kpi(b.HasData ? $"{Format.Percent(b.Today.C.CacheHitRate)}" : "—", "今日缓存命中", null),
            };

            var grid = new Grid { ColumnSpacing = S(16) };
            for (var i = 0; i < cells.Length; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(kpiWeights[i], GridUnitType.Star),
                });
                Grid.SetColumn(cells[i], i);
                grid.Children.Add(cells[i]);
            }

            wrap.Children.Add(grid);
            _kpiHost.Children.Add(wrap);
        }
    }

    private StackPanel Kpi(string value, string label, string? sub)
    {
        var stack = new StackPanel { Spacing = S(2) };
        stack.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = T(22),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = _textBrush,
        });
        stack.Children.Add(new TextBlock { Text = label, FontSize = T(11), Foreground = _mutedBrush });
        if (!string.IsNullOrEmpty(sub))
        {
            stack.Children.Add(new TextBlock
            {
                Text = sub,
                FontSize = T(11),
                Foreground = _faintBrush,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        return stack;
    }

    // ---------------- 近 7 天：本地 + 云端双柱 ----------------

    private void RebuildBars(Snapshot s)
    {
        _bars.Children.Clear();
        var blocks = s.Blocks;
        if (blocks.Count == 0) return;

        var days = blocks[0].Last7.Count;
        if (days == 0) return;

        // 柱高必须用同一个量做刻度。¥ 和 $ 不能比大小，所以用 token 数（跨币种可比）。
        var maxTokens = 1L;
        for (var i = 0; i < days; i++)
            for (var b = 0; b < blocks.Count; b++)
                if (i < blocks[b].Last7.Count && blocks[b].Last7[i].Total > maxTokens)
                    maxTokens = blocks[b].Last7[i].Total;

        var corner = new CornerRadius(S(4));
        var barWidth = D(30);
        var colWidth = D(34);

        for (var i = 0; i < days; i++)
        {
            var day = blocks[0].Last7[i].Day;

            var col = new StackPanel { Spacing = S(3), Width = colWidth * Math.Max(1, blocks.Count) };

            // 金额标签：按来源顺序各一行（第一行本地、第二行云端）
            for (var b = 0; b < blocks.Count; b++)
            {
                var p = i < blocks[b].Last7.Count ? blocks[b].Last7[i] : null;
                // 那天该源根本没有用量时给「—」，不能给 $0：那会被读成「花了 0 元」
                string text;
                if (p is null || p.Total <= 0)
                {
                    text = "—";
                }
                else if (p.ServerCost > 0)
                {
                    text = Format.Money(blocks[b].Currency, p.ServerCost);
                }
                else if (p.Cost <= 0 && p.HasUnpriced)
                {
                    text = "未定价";
                }
                else
                {
                    text = Format.Money(blocks[b].Currency, p.Cost);
                }

                col.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = D(9.5),
                    Foreground = b == 0 ? _mutedBrush : _faintBrush,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextWrapping = TextWrapping.NoWrap,
                });
            }

            var barRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = S(4),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            for (var b = 0; b < blocks.Count; b++)
            {
                var p = i < blocks[b].Last7.Count ? blocks[b].Last7[i] : null;
                var tokens = p?.Total ?? 0;
                var h = tokens > 0 ? Math.Clamp((double)tokens / maxTokens * BarAreaHeight, S(4), BarAreaHeight) : S(2);

                var track = new Border
                {
                    Height = BarAreaHeight,
                    Width = barWidth,
                    Background = _barTrackBrush,
                    CornerRadius = corner,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Child = new Border
                    {
                        Height = h,
                        Width = barWidth,
                        Background = b == 0 ? _barBrush : _bar2Brush,
                        CornerRadius = corner,
                        VerticalAlignment = VerticalAlignment.Bottom,
                    },
                };
                barRow.Children.Add(new Grid { Height = BarAreaHeight, Children = { track } });
            }
            col.Children.Add(barRow);

            col.Children.Add(new TextBlock
            {
                Text = Format.DayLabel(day),
                FontSize = D(10.5),
                Foreground = _mutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            });

            _bars.Children.Add(col);
        }
    }

    // ---------------- 今日逐模型明细：按来源分组 ----------------

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
            // 归属标签：只写来源名与口径，币种与金额在上面那栏 KPI 里已经给了
            if (multi)
            {
                _table.Children.Add(new TextBlock
                {
                    Text = b.Name,
                    FontSize = D(11),
                    Foreground = _accentBrush,
                    Margin = new Thickness(0, S(6), 0, S(2)),
                });
            }

            if (b.TodayByModel.Count == 0)
            {
                _table.Children.Add(TableRow("（今日无用量）", "—", "—", "—", "—", "—", "—", header: false));
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
                // 服务端报了金额就用它，否则是本地单价估算；一律用该来源自己的币种
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

        var texts = new[] { c0, c1, c2, c3, c4, c5, c6 };
        for (var i = 0; i < texts.Length; i++)
        {
            var tb = new TextBlock
            {
                Text = texts[i],
                FontSize = header ? D(11) : D(12),
                FontWeight = header ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = i == 0
                    ? (header ? _faintBrush : _textBrush)
                    : (header ? _faintBrush : (i == 6 ? _accentBrush : _mutedBrush)),
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
        // 第二来源用琥珀色，浅色/深色下都和蓝色柱有明显区别
        _bar2Brush.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(255, 176, 108, 0)
            : Windows.UI.Color.FromArgb(255, 245, 195, 107);
    }

    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);
}
