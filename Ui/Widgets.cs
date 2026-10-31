using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ShanksitoTweaker.Ui;

internal static class W
{
    public static Brush B(string key) => (Brush)Application.Current.Resources[key];

    public static T With<T>(this T item, Action<T> configure)
    {
        configure(item);
        return item;
    }

    public static TextBlock Text(string text, double size = 13, string color = "Text", bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = B(color),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Icon(string glyph, double size = 15, string color = "Muted") => new()
    {
        Text = glyph,
        FontSize = size,
        Foreground = B(color),
        FontFamily = (FontFamily)Application.Current.Resources["Icons"],
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Heading(string text) => Text(text, 14, "Muted", true).With(t => t.Margin = new Thickness(0, 12, 0, 8));

    public static Border Card(UIElement child, string? edge = null) => new()
    {
        Background = B("Panel"),
        BorderBrush = B(edge ?? "Line"),
        BorderThickness = edge is null ? new Thickness(1) : new Thickness(3, 1, 1, 1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(16, 12, 16, 12),
        Margin = new Thickness(0, 0, 0, 8),
        Child = child,
    };

    public static StackPanel Page(string title, string? intro = null)
    {
        var page = new StackPanel();
        page.Children.Add(Text(title, 26, "Text", true).With(t => t.Margin = new Thickness(0, 0, 0, 4)));
        if (intro is not null) page.Children.Add(Text(intro, 13, "Muted").With(t => t.Margin = new Thickness(0, 0, 0, 16)));
        return page;
    }

    public static Button Btn(string label, Action click, bool primary = false)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) };
        if (primary) button.Style = (Style)Application.Current.Resources["Primary"];
        button.Click += (_, _) => click();
        return button;
    }

    public static WrapPanel Toolbar(params UIElement[] items)
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var item in items) bar.Children.Add(item);
        return bar;
    }

    public static Border Pill(string text, string color) => new()
    {
        BorderBrush = B(color),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(7, 0, 7, 1),
        Margin = new Thickness(8, 1, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Text(text, 11, color),
    };

    public static Border Tile(string label, string value, string color = "Text", string? sub = null)
    {
        var stack = new StackPanel();
        stack.Children.Add(Text(label, 12, "Muted"));
        stack.Children.Add(Text(value, 24, color, true));
        if (sub is not null) stack.Children.Add(Text(sub, 12, "Muted"));
        return Card(stack).With(c => c.Margin = new Thickness(0, 0, 8, 8));
    }

    public static StackPanel Line(string title, string detail)
    {
        var stack = new StackPanel();
        stack.Children.Add(Text(title, 14, "Text", true));
        stack.Children.Add(Text(detail, 12.5, "Muted"));
        return stack;
    }

    public static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    public static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} Go" : $"{bytes / (double)(1L << 20):0} Mo";

    /// <summary>Horizontal bar split by share, for frame-rate buckets.</summary>
    public static Grid StackBar(params (double Value, string Color)[] parts)
    {
        var grid = new Grid { Height = 12 };
        var total = parts.Sum(p => p.Value);
        if (total <= 0) return grid;
        var column = 0;
        foreach (var (value, color) in parts)
        {
            if (value <= 0) continue;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(value, GridUnitType.Star) });
            var bar = new Border { Background = B(color), Margin = new Thickness(0, 0, 2, 0), CornerRadius = new CornerRadius(3) };
            Grid.SetColumn(bar, column++);
            grid.Children.Add(bar);
        }
        return grid;
    }
}

/// <summary>Line chart drawn directly, so it needs no chart library.</summary>
internal sealed class LineChart : FrameworkElement
{
    public IReadOnlyList<double> Values { get; init; } = [];
    public IReadOnlyList<string> Labels { get; init; } = [];
    public ISet<int> Marks { get; init; } = new HashSet<int>();

    private FormattedText Label(string text, Brush brush) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), 10.5, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        var (width, height) = (ActualWidth, ActualHeight);
        if (Values.Count == 0 || width < 40) return;
        const double left = 34, right = 14, top = 12, bottom = 22;
        var min = Math.Floor(Values.Min() * 0.9 / 10) * 10;
        var max = Math.Ceiling(Values.Max() * 1.05 / 10) * 10;
        if (max - min < 10) max = min + 10;

        double X(int i) => Values.Count == 1 ? left : left + i * (width - left - right) / (Values.Count - 1);
        double Y(double v) => top + (max - v) / (max - min) * (height - top - bottom);

        var grid = new Pen(W.B("Line"), 1);
        var muted = W.B("Muted");
        foreach (var step in new[] { 0.0, 0.5, 1.0 })
        {
            var value = min + (max - min) * step;
            dc.DrawLine(grid, new Point(left, Y(value)), new Point(width - right, Y(value)));
            dc.DrawText(Label($"{value:0}", muted), new Point(2, Y(value) - 7));
        }

        var mark = new Pen(W.B("Warn"), 1) { DashStyle = DashStyles.Dash };
        foreach (var i in Marks.Where(m => m < Values.Count))
            dc.DrawLine(mark, new Point(X(i), top), new Point(X(i), height - bottom));

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(X(0), Y(Values[0])), false, false);
            for (var i = 1; i < Values.Count; i++) context.LineTo(new Point(X(i), Y(Values[i])), true, true);
        }
        geometry.Freeze();
        var accent = W.B("Accent");
        dc.DrawGeometry(null, new Pen(accent, 2) { LineJoin = PenLineJoin.Round }, geometry);

        var every = Math.Max(1, Values.Count / 8);
        for (var i = 0; i < Values.Count; i++)
        {
            dc.DrawEllipse(accent, null, new Point(X(i), Y(Values[i])), 3, 3);
            if (i >= Labels.Count || (i % every != 0 && i != Values.Count - 1)) continue;
            var text = Label(Labels[i], muted);
            dc.DrawText(text, new Point(Math.Clamp(X(i) - text.Width / 2, 0, width - text.Width), height - 15));
        }
    }
}

internal sealed class ScoreRing : FrameworkElement
{
    public int Score { get; init; }

    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - 8;
        if (radius <= 0) return;
        var color = W.B(Score >= 80 ? "Good" : Score >= 55 ? "Warn" : "Accent");
        dc.DrawEllipse(null, new Pen(W.B("Line"), 10), center, radius, radius);

        var sweep = Math.Clamp(Score, 0, 100) * 3.6;
        if (sweep >= 359.9)
        {
            dc.DrawEllipse(null, new Pen(color, 10), center, radius, radius);
        }
        else if (sweep > 0)
        {
            var angle = (sweep - 90) * Math.PI / 180;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(center.X, center.Y - radius), false, false);
                context.ArcTo(new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)),
                    new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(color, 10) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
        }

        var pixels = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(Score.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 34, W.B("Text"), pixels);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }
}
