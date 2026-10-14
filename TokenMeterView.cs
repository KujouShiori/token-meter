using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace TokenMeter;

/// <summary>
/// 岛上的视图：同一棵可视树同时承载紧凑态与展开态。
///
/// 四条硬规矩（技能里的铁律 5 / 铁律 6）：
///   1. 形态动画逐帧直接给属性赋值，禁止 Storyboard —— 否则动画 tick 上抛 COMException，大岛收回后卡死；
///   2. 展开态元素常驻可视树，用 Height=0 + Opacity=0 隐藏，不用 Visibility=Collapsed；
///   3. 中性色走共享画刷，订阅 theme.Changed 只改画刷 Color，浅/深两套主题都读得清；
///   4. 根元素背景保持透明，岛体材质由宿主画。
/// </summary>
public sealed class TokenMeterView : UserControl, IMorphView
{
    private const double CompactIconSize = 16;
    private const double ExpandedIconSize = 22;
    private const double CompactCostSize = 15;
    private const double ExpandedCostSize = 19;
    /// <summary>展开态 _detail 的目标高度。分配规则（谁让位给谁、为什么是定值）在
    /// <see cref="IslandLayout.Plan"/> 里，那是个纯函数，有断言盯着"高度不随接口数量增长"。</summary>
    private double _detailHeight = IslandLayout.BaseHeight;

    /// <summary>与宿主内置视图同一条 BackEase 曲线的幅度。</summary>
    private const double BackAmplitude = 0.45;

    private readonly IIslandTheme _theme;
    private readonly FontIcon _icon;
    private readonly TextBlock _cost;
    private readonly TextBlock _sep;
    private readonly TextBlock _tokens;
    private readonly StackPanel _detail;
    private readonly TextBlock _breakdown;
    private readonly StackPanel _modelRows;
    private readonly StackPanel _otherHost;
    private readonly TextBlock _totals;

    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _accentBrush = new();

    private readonly DispatcherQueueTimer? _morphTimer;
    private DateTimeOffset _morphStart;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;
    private double _progress;

    private Snapshot? _last;
    private bool _scanning;

    public TokenMeterView(PluginManifest manifest, IIslandTheme theme)
    {
        _theme = theme;
        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;

        _icon = new FontIcon
        {
            Glyph = manifest.IconGlyph ?? "\uE8EF",
            FontSize = CompactIconSize,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _accentBrush,
        };

        _cost = new TextBlock
        {
            Text = "—",
            FontSize = CompactCostSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _textBrush,
        };

        _sep = new TextBlock
        {
            Text = "·",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _faintBrush,
        };

        _tokens = new TextBlock
        {
            Text = "—",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _mutedBrush,
        };

        _breakdown = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.NoWrap, Foreground = _mutedBrush };
        _modelRows = new StackPanel { Spacing = 1 };
        // 其他来源（云端）各占一行；没云端数据时这块为空，展开高度会自动缩回去
        _otherHost = new StackPanel { Spacing = 1 };
        _totals = new TextBlock { FontSize = 11.5, Foreground = _mutedBrush };

        _detail = new StackPanel { Height = 0, Opacity = 0, Spacing = 3 };
        _detail.Children.Add(_breakdown);
        _detail.Children.Add(_modelRows);
        _detail.Children.Add(_otherHost);
        _detail.Children.Add(_totals);

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(_icon);
        header.Children.Add(_cost);
        header.Children.Add(_sep);
        header.Children.Add(_tokens);

        var root = new StackPanel
        {
            Spacing = 7,
            Padding = new Thickness(16, 8, 16, 8),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        root.Children.Add(header);
        root.Children.Add(_detail);
        Content = root;

        _morphTimer = DispatcherQueue?.CreateTimer();
        if (_morphTimer is not null)
        {
            _morphTimer.Interval = TimeSpan.FromMilliseconds(16);
            _morphTimer.IsRepeating = true;
            _morphTimer.Tick += (_, _) => OnMorphTick();
        }

        Unloaded += (_, _) => _morphTimer?.Stop();
    }

    public UIElement View => this;

    /// <summary>把一次快照铺到岛上。只在 UI 线程调用。</summary>
    public void Apply(Snapshot s)
    {
        _last = s;
        Rebuild();
    }

    /// <summary>扫描进行中：岛上先给一句状态，别让数字停在上一次。</summary>
    public void SetScanning(bool scanning)
    {
        if (_scanning == scanning) return;
        _scanning = scanning;
        Rebuild();
    }

    private void Rebuild()
    {
        var s = _last;

        if (_scanning && s is null)
        {
            _cost.Text = "…";
            _tokens.Text = "首次扫描中";
            _sep.Visibility = Visibility.Collapsed;
            return;
        }

        _sep.Visibility = Visibility.Visible;

        if (s is null)
        {
            _cost.Text = "—";
            _tokens.Text = "暂无数据";
            _breakdown.Text = "";
            _totals.Text = "";
            _modelRows.Children.Clear();
            _otherHost.Children.Clear();
            _detailHeight = IslandLayout.BaseHeight;
            return;
        }

        var pricedInToday = s.Today.Models - s.Today.UnpricedModels;

        if (!s.HasData)
        {
            _cost.Text = "—";
            _tokens.Text = "没有记录";
        }
        else if (pricedInToday <= 0)
        {
            _cost.Text = "未定价";
            _tokens.Text = $"{Format.Tokens(s.Today.C.Total)} tok";
        }
        else
        {
            _cost.Text = Format.Money(s.Currency, s.Today.Cost);
            _tokens.Text = $"{Format.Tokens(s.Today.C.Total)} tok";
        }

        var c = s.Today.C;
        // 紧凑态只有 268 宽，放不下"今日 ¥213 已达预算 ¥200"这种句子，但一个 ⚠ 放得下：
        // 预算超了却没任何提示，比多一个符号难看要紧得多
        if (!string.IsNullOrEmpty(s.AlertTag) && _cost.Text != "—") _cost.Text += " ⚠";
        _breakdown.Text = $"输入 {Format.Tokens(c.Input)} · 输出 {Format.Tokens(c.Output)} · 缓存读 {Format.Tokens(c.CacheRead)} · {c.Requests} 次请求";

        // 岛面展开态能用的垂直空间是固定的，而「来源数」是这次新长出来的变量。
        // 高度守恒靠压缩逐模型明细的行数来做，不是加高 —— 规则在 IslandLayout.Plan 里，
        // 那里是纯函数，能被断言锁住。
        var (modelCap, rowsToDraw, hiddenSources, detailHeight) =
            IslandLayout.Plan(Math.Max(0, s.Blocks.Count - 1));

        _modelRows.Children.Clear();
        var shown = 0;
        foreach (var m in s.TodayByModel)
        {
            if (shown >= modelCap) break;
            _modelRows.Children.Add(MakeRow(m, s.Currency));
            shown++;
        }
        var extra = s.TodayByModel.Count - shown;
        if (extra > 0)
        {
            _modelRows.Children.Add(new TextBlock
            {
                Text = $"…另有 {extra} 个模型（点岛体看全部）",
                FontSize = 11,
                Foreground = _faintBrush,
            });
        }

        // 其他来源各一行：一个来源只给一行，装不下的折成一句提示，剩下的去聚光卡看
        _otherHost.Children.Clear();
        var otherRows = 0;
        for (var i = 1; i < s.Blocks.Count && otherRows < rowsToDraw; i++)
        {
            var b = s.Blocks[i];

            if (b.IsSummary)
            {
                // 余额型来源：只有「此刻还剩多少」，套用量那套文案会写成「今日无 · 累计 ¥0」，既不对也读不懂。
                // 这里刻意不再写"余额"两个字 —— 接口显示名是用户自己起的，他很可能会写成
                // "XX 余额"，界面再补一遍就成了"示例网关 余额 余额 $99.9"。
                // 行形本身就是区分：只有一笔钱 + 更新时间，没有 token 与今日/累计。
                var at = b.SummaryAtUtc is null ? string.Empty : $"（{Format.Ago(b.SummaryAtUtc.Value)}更新）";
                _otherHost.Children.Add(new TextBlock
                {
                    Text = $"{b.Name}  {Format.Money(b.Currency, b.SummaryValue!.Value)}{at}"
                         + (string.IsNullOrEmpty(b.AlertTag) ? string.Empty : "  ⚠" + b.AlertTag),
                    FontSize = 11.5,
                    Foreground = string.IsNullOrEmpty(b.AlertTag) ? _mutedBrush : _accentBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                otherRows++;
                continue;
            }

            var pricedB = b.Today.Models - b.Today.UnpricedModels;
            string costText;
            if (!b.HasData || b.Today.C.Total <= 0)
                costText = $"今日无 · 累计 {Format.Money(b.Currency, b.All.Cost)}";
            else if (b.Today.CostBasis == "本地估算" && pricedB <= 0)
                costText = "未定价";
            else
                costText = Format.Money(b.Currency, b.Today.Cost);

            _otherHost.Children.Add(new TextBlock
            {
                Text = $"{b.Name}  {Format.Tokens(b.Today.C.Total)} tok  {costText}"
                     + (string.IsNullOrEmpty(b.AlertTag) ? string.Empty : "  ⚠" + b.AlertTag),
                FontSize = 11.5,
                Foreground = string.IsNullOrEmpty(b.AlertTag) ? _mutedBrush : _accentBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            otherRows++;
        }
        if (hiddenSources > 0)
        {
            _otherHost.Children.Add(new TextBlock
            {
                Text = $"…另有 {hiddenSources} 个来源（点岛体看全部）",
                FontSize = 11,
                Foreground = _faintBrush,
            });
        }

        _detailHeight = detailHeight;

        _totals.Text = $"本周 {Format.Money(s.Currency, s.Week.Cost)} · 本月 {Format.Money(s.Currency, s.Month.Cost)} · 累计 {Format.Money(s.Currency, s.All.Cost)}"
            + (string.IsNullOrEmpty(s.AlertTag) ? string.Empty : $"　⚠{s.AlertTag}");
    }

    private UIElement MakeRow(ModelUsage m, string currency)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });

        var name = new TextBlock
        {
            Text = m.Model,
            FontSize = 12,
            Foreground = _textBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 0);

        var tok = new TextBlock
        {
            Text = Format.Tokens(m.C.Total),
            FontSize = 12,
            Foreground = _mutedBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(tok, 1);

        // 服务端报了金额就优先用它，否则才是本地单价估算
        var serverCost = m.C.FromServer && m.C.Cost > 0;
        var cost = new TextBlock
        {
            Text = serverCost
                ? Format.Money(currency, (decimal)m.C.Cost)
                : (m.Priced ? Format.Money(currency, m.Cost) : "未定价"),
            FontSize = 12,
            Foreground = serverCost || m.Priced ? _accentBrush : _faintBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(cost, 2);

        grid.Children.Add(name);
        grid.Children.Add(tok);
        grid.Children.Add(cost);
        return grid;
    }

    public void AnimateToExpanded(TimeSpan duration) => StartMorph(1, duration);

    public void AnimateToCompact(TimeSpan duration) => StartMorph(0, duration);

    private void ApplyThemeColors()
    {
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(205);
        _faintBrush.Color = Neutral(150);
        _accentBrush.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(255, 0, 96, 192)
            : Windows.UI.Color.FromArgb(255, 108, 200, 255);
    }

    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    private void StartMorph(double target, TimeSpan duration)
    {
        _morphFrom = _progress;
        _morphTarget = target;
        _morphDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1);
        _morphStart = DateTimeOffset.UtcNow;

        if (_morphTimer is null)
        {
            ApplyMorph(target);
            return;
        }
        _morphTimer.Start();
    }

    private void OnMorphTick()
    {
        try
        {
            var elapsed = (DateTimeOffset.UtcNow - _morphStart).TotalMilliseconds;
            var t = Math.Clamp(elapsed / Math.Max(1, _morphDuration.TotalMilliseconds), 0, 1);
            ApplyMorph(_morphFrom + (_morphTarget - _morphFrom) * BackEaseOut(t));

            if (t >= 1)
            {
                _morphTimer?.Stop();
                ApplyMorph(_morphTarget);      // 收尾锁终态，不留中间值
            }
        }
        catch (Exception)
        {
            // 定时器里的异常会直接进宿主未处理异常计数（累计 5 次自动停用），必须自己兜住
            _morphTimer?.Stop();
            ApplyMorph(_morphTarget);
        }
    }

    /// <summary>BackEase(EaseOut, A) = 1 + (A+1)(t-1)³ + A(t-1)²。</summary>
    private static double BackEaseOut(double t)
    {
        var d = t - 1;
        return 1 + (BackAmplitude + 1) * d * d * d + BackAmplitude * d * d;
    }

    private void ApplyMorph(double progress)
    {
        _progress = progress;

        _detail.Height = Math.Max(0, _detailHeight * progress);
        _detail.Opacity = Math.Clamp(progress, 0, 1);

        _icon.FontSize = CompactIconSize + (ExpandedIconSize - CompactIconSize) * progress;
        _cost.FontSize = CompactCostSize + (ExpandedCostSize - CompactCostSize) * progress;
    }
}
