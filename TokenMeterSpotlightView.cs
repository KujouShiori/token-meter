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
/// 所以 KPI 按来源各一栏、柱状图每个来源一根柱（柱高用 token 数这个跨币种可比的量，
/// 金额只进标签文字），明细表按来源分组。绝不把 ¥ 和 $ 加在同一个数上。
///
/// 来源数量是"用户想接几个就接几个"的，所以视图里没有任何一处写死 2：
/// 柱宽按来源数反推、颜色四档循环加图例、明细表逐来源各自限长。
/// 余额/汇总型来源（<see cref="SourceBlock.IsSummary"/>）不参与按天图与明细表，只在 KPI 单独一块。
///
/// 近 7 天图有两种画法，跟着快照的 <see cref="Snapshot.ChartMode"/> 走（设置页可换，默认分组）：
///   分组 grouped = 一天里每个来源一根柱并排，比的是形状；
///   堆叠 stacked = 一天一根柱、按来源从下往上叠色，读的是总量与配比。
/// ⚠ 两种都只是"排布方式"，都改变不了刻度是共享的这件事：量级差到 10^4（本地每天几亿 token
/// 对云端一天几万）时，**换成堆叠照样看不见小的那一家**（有断言把这条局限写死）。
/// 真要看清小来源得换思路（逐来源各自归一，或对数轴），别指望这个开关。
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

    private const double BarAreaHeight = 123.2 * DataTextScale;   // 112 × 1.1：柱体加高 10%

    /// <summary>明细表六列（请求/输入/输出/缓存读/合计/费用）的下限与上限，已按字号缩放。
    /// 基准数放在 <see cref="TableLayout"/> 里，断言与界面共用同一组，不会漂。
    /// 列宽现在按内容算，不再写死 —— 写死会把 244.75M 切成 244.7…</summary>
    private static readonly double[] TableColMinPx = TableLayout.BaseMin.Select(D).ToArray();
    private static readonly double[] TableColMaxPx = TableLayout.BaseMax.Select(D).ToArray();

    /// <summary>首列（模型名）至少留出的宽度。</summary>
    private static readonly double FirstColMinWidth = D(TableLayout.BaseFirstColMin);

    /// <summary>明细表**每个来源**最多列几个模型（不是全表合计），超出就折成一句提示。
    /// 卡片是纵向可滚的，所以这里限制的是"一眼看得完"而不是空间。</summary>
    private const int MaxTableRowsPerSource = 12;

    private readonly IIslandTheme _theme;
    private readonly StackPanel _kpiHost;
    private readonly TextBlock _legend;
    private readonly TextBlock _chartTitle;
    private readonly Grid _bars;
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
    private readonly SolidColorBrush _bar3Brush = new();
    private readonly SolidColorBrush _bar4Brush = new();
    private readonly SolidColorBrush _barTrackBrush = new();

    private Snapshot? _last;
    public event Action? RefreshRequested;

    /// <summary>来源 → 柱子/图例的颜色。超过四种就循环复用：接口数量没有上限，颜色不能没有上限
    /// —— 循环之后靠图例上的名字对回去，而不是让某个来源画不出柱子。</summary>
    private SolidColorBrush BarBrushFor(int i) => (i % 4) switch
    {
        0 => _barBrush,
        1 => _bar2Brush,
        2 => _bar3Brush,
        _ => _bar4Brush,
    };

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
        // 用 7 等分星型列而不是固定宽 StackPanel：
        // 之前每格写死 93.8，7 格共 707.3，超过卡片可用的 688.8，
        // 而横向滚动是禁用的，于是最右那一格被挤裁。星型列永远适配卡片宽度。
        _bars = new Grid();
        // 图例：柱子颜色按来源循环复用（接口数量没有上限），所以超过一种时必须有个
        // 「色块 → 名字」的对应关系，否则第三根柱子开始就没人知道它是谁。
        // 用一行会换行的文字而不是横排 StackPanel：横排的不会换行，名字一长就把卡片撑破。
        _legend = new TextBlock
        {
            FontSize = T(11),
            Foreground = _mutedBrush,
            TextWrapping = TextWrapping.Wrap,
        };
        _table = new StackPanel { Spacing = S(2) };

        _chartTitle = new TextBlock { Text = "近 7 天用量", FontSize = D(13), Foreground = _mutedBrush };
        var chartLabel = _chartTitle;
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
        root.Children.Add(_legend);
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
        // 标题跟着画法写出来：不然切完模式看不出到底生效了没有
        _chartTitle.Text = "近 7 天用量（"
            + (string.Equals(s.ChartMode, ChartLayout.Stacked, StringComparison.OrdinalIgnoreCase) ? "堆叠" : "分组")
            + "，柱高按 token）";
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

            if (b.IsSummary)
            {
                // 余额型来源单独一行：它只有「此刻还剩多少」，没有今日/本周/累计，
                // 硬套五格 KPI 会画出五个「—」，看起来像数据坏了
                wrap.Children.Add(new TextBlock
                {
                    Text = $"{b.Name} ｜ {b.Currency} ｜ 余额（某一刻的总额，不进按天统计与柱状图）"
                         + (string.IsNullOrEmpty(b.AlertTag) ? string.Empty : " ｜ ⚠" + b.AlertTag),
                    FontSize = T(11),
                    Foreground = _faintBrush,
                });
                wrap.Children.Add(Kpi(
                    Format.Money(b.Currency, b.SummaryValue!.Value),
                    "当前余额",
                    b.SummaryAtUtc is null ? null : $"{Format.Ago(b.SummaryAtUtc.Value)}读取"));
                _kpiHost.Children.Add(wrap);
                continue;
            }

            wrap.Children.Add(new TextBlock
            {
                Text = $"{b.Name} ｜ {b.Currency} ｜ {b.Today.CostBasis}"
                     + (string.IsNullOrEmpty(b.AlertTag) ? string.Empty : " ｜ ⚠" + b.AlertTag),
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

    // ---------------- 近 7 天：每个来源一根柱子 ----------------

    /// <summary>卡片里横向真正可用的宽度（左右内边距之外，还要给纵向滚动条留位）。
    /// 横向滚动是禁用的，所以柱子总宽必须自己算进这个数里 —— 写死 30 的话第三个来源起就挤出卡片。</summary>
    private const double ChartUsableWidth = CardWidth - 2 * (30 * LayoutScale) - 18 * LayoutScale;

    private void RebuildBars(Snapshot s)
    {
        _bars.Children.Clear();
        _bars.ColumnDefinitions.Clear();
        // 先清掉上一次的图例：下面有两个提前 return，不清就会留着上一帧的来源名
        _legend.Inlines.Clear();
        _legend.Visibility = Visibility.Collapsed;
        // 余额型来源不进柱状图：它没有按天数据，画进去是每天一根一样的空柱 + 一列「—」，
        // 会把真正的用量刻度挤扁
        var blocks = s.Blocks.Where(b => !b.IsSummary).ToList();
        if (blocks.Count == 0) return;

        // 天数不能假定 blocks[0] 就是本地块：本地块没数据时它仍在列表里，但 Last7 的长度
        // 由 SnapshotBuilder 统一给 7，取最大值只是防一手结构变化
        var days = blocks.Max(b => b.Last7.Count);
        if (days == 0) return;

        // 图例：颜色是按来源循环复用的，没有图例的话第三根柱子开始就没人知道它是谁
        if (blocks.Count > 1)
        {
            _legend.Visibility = Visibility.Visible;
            for (var b = 0; b < blocks.Count; b++)
            {
                if (b > 0) _legend.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "    " });
                _legend.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = "■ ",
                    Foreground = BarBrushFor(b),
                });
                _legend.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = blocks[b].Name });
            }
        }
        else
        {
            _legend.Visibility = Visibility.Collapsed;
        }

        var stacked = string.Equals(s.ChartMode, ChartLayout.Stacked, StringComparison.OrdinalIgnoreCase);

        // 柱高必须用同一个量做刻度。¥ 和 $ 不能比大小，所以用 token 数（跨币种可比）。
        // 但"刻度取哪个最大值"两种画法不一样：
        //   分组：每来源单独一根，取【单个来源单日】的最大值，柱子之间比的是形状；
        //   堆叠：一天一根柱是各来源相加，必须取【相加之后】的最大日总量，
        //         用前者做刻度的话，最高的那根叠起来会直接顶破柱区。
        double maxSingle = 1, maxDayTotal = 1;
        for (var i = 0; i < days; i++)
        {
            long dayTotal = 0;
            for (var b = 0; b < blocks.Count; b++)
            {
                if (i >= blocks[b].Last7.Count) continue;
                var t = blocks[b].Last7[i].Total;
                if (t > maxSingle) maxSingle = t;
                dayTotal += t;
            }
            if (dayTotal > maxDayTotal) maxDayTotal = dayTotal;
        }
        var scaleTo = stacked ? maxDayTotal : maxSingle;

        var corner = new CornerRadius(S(4));
        var barSpacing = S(4);
        // 列是星型均分的，所以每天可用的横向空间 = 图表可用宽 / 天数。
        // 分组：来源越多柱越细；堆叠：一天只有一根，可以宽些。两种都不许挤出卡片右缘
        // —— 卡片宽度不能跟着接口数量长（卡片横向是不滚动）。
        var perDay = ChartLayout.PerDayWidth(ChartUsableWidth, days);
        var barWidth = stacked
            ? ChartLayout.StackWidth(perDay, D(34), S(10))
            : ChartLayout.GroupedBarWidth(perDay, blocks.Count, barSpacing, D(30), S(6));

        for (var i = 0; i < days; i++)
        {
            var day = blocks.First(b => i < b.Last7.Count).Last7[i].Day;

            _bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 不再给列固定 Width，交给星型列均分；内部元素居中
            var col = new StackPanel { Spacing = S(3), HorizontalAlignment = HorizontalAlignment.Center };

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
                    // 用该来源柱子那个颜色：标签和柱子对得上，才不需要每个柱子再画一遍名字
                    Foreground = BarBrushFor(b),
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextWrapping = TextWrapping.NoWrap,
                });
            }

            // 金额下面再给一行当天总 token：token 是跨币种可比的量，两个运营商相加有意义
            long dayTokens = 0;
            for (var b = 0; b < blocks.Count; b++)
                if (i < blocks[b].Last7.Count) dayTokens += blocks[b].Last7[i].Total;

            col.Children.Add(new TextBlock
            {
                Text = dayTokens > 0 ? Format.Tokens(dayTokens) + " tok" : "—",
                FontSize = D(9),
                Foreground = _faintBrush,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0, 0, 0, S(2)),
            });

            if (stacked)
            {
                // 一天一根柱：各来源按 token 折算的厚度从下往上叠，本地块在最底。
                // StackPanel 是从上往下排的，所以要把来源顺序反过来加，
                // 否则"本地在下、云端在上"这个读图直觉就是反的。
                var tokensPerSource = new double[blocks.Count];
                for (var b = 0; b < blocks.Count; b++)
                    tokensPerSource[b] = i < blocks[b].Last7.Count ? blocks[b].Last7[i].Total : 0;
                var heights = ChartLayout.StackHeights(tokensPerSource, scaleTo, BarAreaHeight);

                var segHost = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
                for (var b = blocks.Count - 1; b >= 0; b--)
                {
                    if (heights[b] <= 0) continue;   // 0 厚度的段不画：画了会白占一条圆角边
                    segHost.Children.Add(new Border
                    {
                        Height = heights[b],
                        Width = barWidth,
                        Background = BarBrushFor(b),
                        CornerRadius = corner,
                        VerticalAlignment = VerticalAlignment.Bottom,
                    });
                }

                var stackTrack = new Border
                {
                    Height = BarAreaHeight,
                    Width = barWidth,
                    Background = _barTrackBrush,
                    CornerRadius = corner,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = segHost,
                };
                col.Children.Add(new Grid { Height = BarAreaHeight, Children = { stackTrack } });
            }
            else
            {
                var barRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = barSpacing,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                for (var b = 0; b < blocks.Count; b++)
                {
                    var p = i < blocks[b].Last7.Count ? blocks[b].Last7[i] : null;
                    var tokens = p?.Total ?? 0;
                    var h = ChartLayout.GroupedHeight(tokens, scaleTo, BarAreaHeight, S(4));

                    var track = new Border
                    {
                        Height = BarAreaHeight,
                        Width = barWidth,
                        Background = _barTrackBrush,
                        CornerRadius = corner,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Child = h > 0 ? new Border
                        {
                            Height = h,
                            Width = barWidth,
                            Background = BarBrushFor(b),
                            CornerRadius = corner,
                            VerticalAlignment = VerticalAlignment.Bottom,
                        } : null,
                    };
                    barRow.Children.Add(new Grid { Height = BarAreaHeight, Children = { track } });
                }
                col.Children.Add(barRow);
            }

            col.Children.Add(new TextBlock
            {
                Text = Format.DayLabel(day),
                FontSize = D(10.5),
                Foreground = _mutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            });

            Grid.SetColumn(col, i);
            _bars.Children.Add(col);
        }
    }

    // ---------------- 今日逐模型明细：按来源分组 ----------------

    private void RebuildTable(Snapshot s)
    {
        _table.Children.Clear();
        // 明细表是「按模型的今日用量」，余额型来源没有这个概念，在上面那栏单独给了
        var blocks = s.Blocks.Where(b => !b.IsSummary).ToList();
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

        // 先把内容收集成纯字符串，才能按「这一列最长的那条有多宽」算列宽。
        // 之前是边算边往面板里塞，列宽只能写死，写死就会切数字（见 TableLayout 的注释）。
        var header = new[] { "模型", "请求", "输入", "输出", "缓存读", "合计", "费用" };
        // Note 有两种：来源分组标题（用强调色）和"另有 N 个模型"（用淡色），所以带个标记区分
        var items = new List<(string[]? Cells, string? Note, bool IsGroup)>();

        foreach (var b in blocks)
        {
            // 上限必须**逐来源**算。原来是个跨来源共享的计数器，于是第三个来源
            // 一进来就已经超了上限，整块明细被静默丢掉，而那行提示用的又是这个来源自己的模型数
            // 减去共享计数 —— 数字既不对、丢掉的明细也没人知道。
            if (multi) items.Add((null, b.Name, true));

            if (b.TodayByModel.Count == 0)
            {
                items.Add((new[] { "（今日无用量）", "—", "—", "—", "—", "—", "—" }, null, false));
                continue;
            }

            var shown = 0;
            foreach (var m in b.TodayByModel)
            {
                if (shown >= MaxTableRowsPerSource)
                {
                    items.Add((null, $"…另有 {b.TodayByModel.Count - shown} 个模型未列出", false));
                    break;
                }
                // 服务端报了金额就用它，否则是本地单价估算；一律用该来源自己的币种
                var serverCost = m.C.FromServer && m.C.Cost > 0;
                items.Add((new[]
                {
                    m.Model,
                    m.C.Requests.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Format.Tokens(m.C.Input),
                    Format.Tokens(m.C.Output),
                    Format.Tokens(m.C.CacheRead),
                    Format.Tokens(m.C.Total),
                    serverCost
                        ? Format.Money(b.Currency, (decimal)m.C.Cost)
                        : (m.Priced ? Format.Money(b.Currency, m.Cost) : "未定价"),
                }, null, false));
                shown++;
            }
        }

        var numeric = TableLayout.ColumnWidths(
            header[1..],
            items.Where(x => x.Cells is not null).Select(x => x.Cells![1..]).ToList(),
            D(12), TableColMinPx, TableColMaxPx);
        var fitted = FitToWidth(numeric);

        _table.Children.Add(TableRow(header, fitted, header: true));
        foreach (var (cells, note, isGroup) in items)
        {
            if (cells is not null)
            {
                _table.Children.Add(TableRow(cells, fitted, header: false));
                continue;
            }
            if (note is null) continue;
            _table.Children.Add(new TextBlock
            {
                Text = note,
                FontSize = D(11),
                Foreground = isGroup ? _accentBrush : _faintBrush,
                Margin = new Thickness(0, isGroup ? S(6) : S(4), 0, isGroup ? S(2) : 0),
            });
        }
    }

    /// <summary>
    /// 极端情况下（模型名特别长、金额位数特别多）算出来的列宽仍可能把首列挤没，
    /// 所以按比例缩到"至少给模型名留下 <see cref="FirstColMinWidth"/> 的空间"。
    /// 缩完仍然放不下就让它去截断数字列 —— 首列是人名，截不得。
    /// </summary>
    private static double[] FitToWidth(double[] numeric)
    {
        var spacing = S(8);
        var taken = numeric.Sum() + spacing * numeric.Length;
        var room = ChartUsableWidth - taken;
        if (room >= FirstColMinWidth) return numeric;

        var budget = ChartUsableWidth - spacing * numeric.Length - FirstColMinWidth;
        if (budget <= 0) return numeric.Select(x => TableColMinPx[0]).ToArray();
        var scale = budget / numeric.Sum();
        return numeric.Select(x => x * scale).ToArray();
    }

    private UIElement TableRow(string[] texts, double[] numericWidths, bool header)
    {
        var grid = new Grid { ColumnSpacing = S(8), Padding = new Thickness(0, S(2), 0, S(2)) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var w in numericWidths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });

        for (var i = 0; i < texts.Length && i < 7; i++)
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
        // 第三、第四档用青绿与紫：与蓝/琥珀在两套主题下都能一眼分开
        _bar3Brush.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(255, 0, 138, 122)
            : Windows.UI.Color.FromArgb(255, 102, 222, 204);
        _bar4Brush.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(255, 130, 72, 190)
            : Windows.UI.Color.FromArgb(255, 190, 152, 255);
    }

    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);
}
