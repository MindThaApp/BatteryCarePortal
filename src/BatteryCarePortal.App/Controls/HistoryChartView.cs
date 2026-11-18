using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Portal.Core.Battery;

namespace BatteryCarePortal.Controls;

/// <summary>One point of a chart line: <see cref="X"/> runs 0 to 1 across the plot.</summary>
internal sealed record PlotPoint(double X, double Value, string Hover);

internal sealed record ChartData(
    IReadOnlyList<IReadOnlyList<PlotPoint>> Lines,
    HistoryScale Scale,
    IReadOnlyList<double> YTicks,
    Func<double, string> FormatTick,
    string StartLabel,
    string MiddleLabel,
    string EndLabel,
    string Summary);

/// <summary>
/// A plain line chart drawn with shapes in the app's theme brushes. The plot has a fixed height but every label sits
/// outside it in auto-sized cells, so larger text never clips. Time runs left to right in every language.
/// </summary>
internal sealed class HistoryChartView : Grid
{
    private const double PlotHeight = 180;
    private const double TickLabelWidth = 56;

    private readonly TextBlock _hover = new() { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
    private readonly Canvas _labels = new() { Height = PlotHeight, Width = TickLabelWidth };
    private readonly Canvas _plot = new() { Height = PlotHeight, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0)) };
    private readonly TextBlock _start = new(), _middle = new() { HorizontalAlignment = HorizontalAlignment.Center }, _end = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private ChartData? _data;

    public HistoryChartView()
    {
        FlowDirection = FlowDirection.LeftToRight;
        RowSpacing = 4;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Place(_hover, 0, 1);
        Place(_labels, 1, 0);
        Place(_plot, 1, 1);
        var axis = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        axis.ColumnDefinitions.Add(new ColumnDefinition());
        axis.ColumnDefinitions.Add(new ColumnDefinition());
        axis.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (TextBlock t in new[] { _start, _middle, _end })
        {
            t.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
            t.Foreground = Secondary();
        }
        SetColumn(_start, 0); SetColumn(_middle, 1); SetColumn(_end, 2);
        axis.Children.Add(_start); axis.Children.Add(_middle); axis.Children.Add(_end);
        Place(axis, 2, 1);

        _plot.SizeChanged += (_, _) => Draw();
        _plot.PointerMoved += OnPointerMoved;
        _plot.PointerExited += (_, _) => _hover.Text = _data?.Summary ?? "";
        ActualThemeChanged += (_, _) => Draw();
    }

    public void SetData(ChartData data)
    {
        _data = data;
        _start.Text = data.StartLabel;
        _middle.Text = data.MiddleLabel;
        _end.Text = data.EndLabel;
        _hover.Text = data.Summary;
        AutomationProperties.SetName(_plot, data.Summary);
        Draw();
    }

    private void Place(UIElement element, int row, int column)
    {
        SetRow((FrameworkElement)element, row);
        SetColumn((FrameworkElement)element, column);
        Children.Add(element);
    }

    private static Brush Secondary() => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    private void Draw()
    {
        _plot.Children.Clear();
        _labels.Children.Clear();
        double w = _plot.ActualWidth;
        if (_data is not { } d || w < 40) return;

        Brush grid = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];
        Brush accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        Brush text = Secondary();
        const double inset = 8;
        double h = PlotHeight - 2 * inset;
        double Y(double value) => inset + h * (1 - d.Scale.Fraction(value));

        foreach (double tick in d.YTicks)
        {
            double y = Y(tick);
            _plot.Children.Add(new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = grid, StrokeThickness = 1, Opacity = 0.4 });
            var label = new TextBlock { Text = d.FormatTick(tick), Foreground = text, Width = TickLabelWidth - 6, TextAlignment = TextAlignment.Right, FontSize = 12 };
            label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetTop(label, y - label.DesiredSize.Height / 2);
            _labels.Children.Add(label);
        }

        double baseline = Y(Math.Clamp(0, d.Scale.Low, d.Scale.High));
        foreach (IReadOnlyList<PlotPoint> line in d.Lines)
        {
            if (line.Count == 0) continue;
            if (line.Count == 1)
            {
                var dot = new Ellipse { Width = 6, Height = 6, Fill = accent };
                Canvas.SetLeft(dot, line[0].X * w - 3);
                Canvas.SetTop(dot, Y(line[0].Value) - 3);
                _plot.Children.Add(dot);
                continue;
            }
            var points = new PointCollection();
            foreach (PlotPoint p in line) points.Add(new Windows.Foundation.Point(p.X * w, Y(p.Value)));
            var area = new PointCollection();
            area.Add(new Windows.Foundation.Point(line[0].X * w, baseline));
            foreach (Windows.Foundation.Point p in points) area.Add(p);
            area.Add(new Windows.Foundation.Point(line[^1].X * w, baseline));
            _plot.Children.Add(new Polygon { Points = area, Fill = accent, Opacity = 0.15 });
            _plot.Children.Add(new Polyline { Points = points, Stroke = accent, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round });
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_data is not { } d || _plot.ActualWidth < 40) return;
        double x = e.GetCurrentPoint(_plot).Position.X / _plot.ActualWidth;
        PlotPoint? best = null;
        double distance = double.MaxValue;
        foreach (IReadOnlyList<PlotPoint> line in d.Lines)
            foreach (PlotPoint p in line)
            {
                double dist = Math.Abs(p.X - x);
                if (dist < distance) { distance = dist; best = p; }
            }
        if (best is not null) _hover.Text = best.Hover;
    }
}
