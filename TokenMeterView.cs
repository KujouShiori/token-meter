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
    private const double ExpandedDetailHeight = 110;
    private const int MaxModelRows = 3;

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
        _totals = new TextBlock { FontSize = 11.5, Foreground = _mutedBrush };

        _detail = new StackPanel { Height = 0, Opacity = 0, Spacing = 3 };
        _detail.Children.Add(_breakdown);
        _detail.Children.Add(_modelRows);
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
        _breakdown.Text = $"输入 {Format.Tokens(c.Input)} · 输出 {Format.Tokens(c.Output)} · 缓存读 {Format.Tokens(c.CacheRead)} · {c.Requests} 次请求";

        _modelRows.Children.Clear();
        var shown = 0;
        foreach (var m in s.TodayByModel)
        {
            if (shown >= MaxModelRows) break;
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

        _totals.Text = $"本周 {Format.Money(s.Currency, s.Week.Cost)} · 本月 {Format.Money(s.Currency, s.Month.Cost)} · 累计 {Format.Money(s.Currency, s.All.Cost)}";
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

        _detail.Height = Math.Max(0, ExpandedDetailHeight * progress);
        _detail.Opacity = Math.Clamp(progress, 0, 1);

        _icon.FontSize = CompactIconSize + (ExpandedIconSize - CompactIconSize) * progress;
        _cost.FontSize = CompactCostSize + (ExpandedCostSize - CompactCostSize) * progress;
    }
}
