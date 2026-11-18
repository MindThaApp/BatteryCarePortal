using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Portal.Core.Battery;
using Windows.Foundation;

namespace BatteryCarePortal.Controls;

/// <summary>
/// Level ring with a tick at the charge limit and a battery glyph in the middle. Level is drawn as an arc from the top,
/// clockwise. Colour follows the state: low, charging, or normal. Drawing only; it never reads the battery itself.
/// </summary>
public sealed class BatteryRing : UserControl
{
    private const double Size = 132, Stroke = 10, Radius = (Size - Stroke) / 2;

    private readonly Microsoft.UI.Xaml.Shapes.Path _track = new() { StrokeThickness = Stroke };
    private readonly Microsoft.UI.Xaml.Shapes.Path _level = new() { StrokeThickness = Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly Line _tick = new() { StrokeThickness = 3, Visibility = Visibility.Collapsed };
    private readonly Rectangle _glyphBody = new() { Width = 40, Height = 20, RadiusX = 3, RadiusY = 3, StrokeThickness = 2 };
    private readonly Rectangle _glyphCap = new() { Width = 3, Height = 8, RadiusX = 1, RadiusY = 1 };
    private readonly Rectangle _glyphFill = new() { Height = 14, RadiusX = 1.5, RadiusY = 1.5 };
    private readonly Line _glyphTick = new() { StrokeThickness = 2, Visibility = Visibility.Collapsed };
    private readonly TextBlock _text = new() { HorizontalAlignment = HorizontalAlignment.Center, FontSize = 22 };

    private int? _percent;
    private int? _limit;
    private bool _charging;

    public BatteryRing()
    {
        var canvas = new Canvas { Width = Size, Height = Size };
        canvas.Children.Add(_track);
        canvas.Children.Add(_level);
        canvas.Children.Add(_tick);

        // The glyph keeps its direction in right-to-left layouts, as the Windows battery icon does.
        var glyph = new Canvas { Width = 44, Height = 20, FlowDirection = FlowDirection.LeftToRight };
        Canvas.SetLeft(_glyphBody, 0); Canvas.SetTop(_glyphBody, 0);
        Canvas.SetLeft(_glyphCap, 41); Canvas.SetTop(_glyphCap, 6);
        Canvas.SetLeft(_glyphFill, 3); Canvas.SetTop(_glyphFill, 3);
        glyph.Children.Add(_glyphBody);
        glyph.Children.Add(_glyphCap);
        glyph.Children.Add(_glyphFill);
        glyph.Children.Add(_glyphTick);
        Canvas.SetLeft(glyph, (Size - 44) / 2); Canvas.SetTop(glyph, 38);
        canvas.Children.Add(glyph);

        Canvas.SetLeft(_text, 0); Canvas.SetTop(_text, 66);
        _text.Width = Size;
        _text.TextAlignment = TextAlignment.Center;
        canvas.Children.Add(_text);

        Content = canvas;
        ActualThemeChanged += (_, _) => Redraw();
        // The settings object outlives the control, so listen only while the control is in the tree.
        Windows.Foundation.TypedEventHandler<Windows.UI.ViewManagement.AccessibilitySettings, object> onContrast =
            (_, _) => DispatcherQueue?.TryEnqueue(Redraw);
        // Some systems refuse the subscription (E_NOTFOUND); the ring then just redraws on theme changes only.
        Loaded += (_, _) => { try { Access.HighContrastChanged += onContrast; } catch (System.Runtime.InteropServices.COMException) { } };
        Unloaded += (_, _) => { try { Access.HighContrastChanged -= onContrast; } catch (System.Runtime.InteropServices.COMException) { } };
        Loaded += (_, _) => Redraw();
        Redraw();
    }

    public void Set(int? percent, int? limit, bool charging)
    {
        _percent = percent;
        _limit = limit is int l && l < 100 ? l : null;
        _charging = charging;
        Redraw();
    }

    private static readonly Windows.UI.ViewManagement.AccessibilitySettings Access = new();
    private static readonly Windows.UI.ViewManagement.UISettings Ui = new();

    // High contrast: every stroke is the system text colour, so the ring never depends on theme accent colours.
    private static Brush ThemeBrush(string key, Brush fallback) =>
        Access.HighContrast ? new SolidColorBrush(Ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground))
        : Application.Current.Resources.TryGetValue(key, out object? o) && o is Brush b ? b : fallback;

    private void Redraw()
    {
        Brush neutral = ThemeBrush("ControlStrongStrokeColorDefaultBrush", new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0x80, 0x80, 0x80)));
        Brush text = ThemeBrush("TextFillColorPrimaryBrush", new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x80, 0x80, 0x80)));
        Brush state = _percent is <= 20 && !_charging ? ThemeBrush("SystemFillColorCriticalBrush", neutral)
            : _charging ? ThemeBrush("SystemFillColorSuccessBrush", neutral)
            : ThemeBrush("AccentFillColorDefaultBrush", neutral);

        _track.Stroke = ThemeBrush("ControlStrokeColorDefaultBrush", neutral);
        _track.Data = Arc(99.99);
        _level.Stroke = state;
        _level.Data = _percent is int p && p > 0 ? Arc(BatteryRingMath.ArcPercent(p)) : null;

        if (_limit is int limit)
        {
            (double x1, double y1) = BatteryRingMath.PointAt(Size / 2, Size / 2, Radius - Stroke, limit);
            (double x2, double y2) = BatteryRingMath.PointAt(Size / 2, Size / 2, Radius + Stroke / 2 + 2, limit);
            _tick.X1 = x1; _tick.Y1 = y1; _tick.X2 = x2; _tick.Y2 = y2;
            _tick.Stroke = text;
            _tick.Visibility = Visibility.Visible;
        }
        else
        {
            _tick.Visibility = Visibility.Collapsed;
        }

        _glyphBody.Stroke = text;
        _glyphCap.Fill = text;
        _glyphFill.Fill = state;
        _glyphFill.Width = _percent is int q ? 34.0 * Math.Clamp(q, 0, 100) / 100.0 : 0;
        if (_limit is int gl)
        {
            double x = 3 + 34.0 * gl / 100.0;
            _glyphTick.X1 = _glyphTick.X2 = x;
            _glyphTick.Y1 = -2; _glyphTick.Y2 = 22;
            _glyphTick.Stroke = text;
            _glyphTick.Visibility = Visibility.Visible;
        }
        else
        {
            _glyphTick.Visibility = Visibility.Collapsed;
        }

        _text.Foreground = text;
        _text.Text = _percent is int pct ? BatteryFormat.Percent(pct, format: "0") : BatteryFormat.None;
        string name = _text.Text + (_limit is int lim ? ", " + string.Format(Strings.Get("OverviewLimitTick"), lim) : string.Empty);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, name);
        ToolTipService.SetToolTip(this, _limit is null ? null : string.Format(Strings.Get("OverviewLimitTick"), _limit));
    }

    private static Geometry Arc(double percent)
    {
        double c = Size / 2;
        (double sx, double sy) = BatteryRingMath.PointAt(c, c, Radius, 0);
        (double ex, double ey) = BatteryRingMath.PointAt(c, c, Radius, percent);
        var figure = new PathFigure { StartPoint = new Point(sx, sy), IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(ex, ey),
            Size = new Size(Radius, Radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = percent > 50,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}
