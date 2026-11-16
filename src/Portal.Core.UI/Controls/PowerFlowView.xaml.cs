using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Portal.Core.Battery;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Portal.Core.UI.Controls;

/// <summary>
/// Sankey-style power flow: Charger to System and Battery on AC, Battery to System on battery.
/// Ribbon thickness follows the watts; dots travel along each ribbon in the direction of flow.
/// </summary>
public sealed partial class PowerFlowView : UserControl
{
    private static readonly UISettings SharedUiSettings = new();
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);

    private readonly Ribbon _acSystem;
    private readonly Ribbon _acBattery;
    private readonly Ribbon _dcSystem;
    private readonly DispatcherQueueTimer _timer;
    private PowerFlowSnapshot? _flow;
    private bool _compact;
    private bool _loaded;
    private bool _animationsEnabled;
    private DateTimeOffset _lastFrame;
    private XamlRoot? _observedRoot;

    public PowerFlowView()
    {
        InitializeComponent();
        _acSystem = new Ribbon(AcSystemBase, AcSystemDots, AcSystemMissing, AcSystemLabel, AcSystemLabelText);
        _acBattery = new Ribbon(AcBatteryBase, AcBatteryDots, AcBatteryMissing, AcBatteryLabel, AcBatteryLabelText);
        _dcSystem = new Ribbon(DcSystemBase, DcSystemDots, DcSystemMissing, DcSystemLabel, DcSystemLabelText);
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = FrameInterval;
        _timer.IsRepeating = true;
        _timer.Tick += DispatcherCallbacks.GuardedTick(OnFrame);
        _animationsEnabled = ReadAnimationsEnabled();

        ChargerCaption.Text = Localization.Get("PowerFlowCharger");
        SystemCaption.Text = Localization.Get("PowerFlowSystem");
        BatteryCaption.Text = Localization.Get("PowerFlowBattery");

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => Relayout();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateAnimationState());
        Relayout();
    }

    /// <summary>Single-row mini version for the tray popup (about 300 by 56).</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            MinHeight = value ? 56 : 190;
            Relayout();
        }
    }

    /// <summary>Shows a new reading. Safe to call often; the layout is recomputed in place.</summary>
    public void Update(PowerFlowSnapshot flow)
    {
        _flow = flow;
        Relayout();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        SharedUiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
        _animationsEnabled = ReadAnimationsEnabled();
        ObserveRoot(XamlRoot);
        Relayout();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        SharedUiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        ObserveRoot(null);
        UpdateAnimationState();
    }

    private void ObserveRoot(XamlRoot? root)
    {
        if (_observedRoot is not null) _observedRoot.Changed -= OnRootChanged;
        _observedRoot = root;
        if (root is not null) root.Changed += OnRootChanged;
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateAnimationState();

    private void OnAnimationsEnabledChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) =>
        DispatcherQueue.TryEnqueueGuarded(() =>
        {
            _animationsEnabled = ReadAnimationsEnabled();
            UpdateAnimationState();
        });

    private static bool ReadAnimationsEnabled()
    {
        // A safe start (after a crash loop) runs no optional animation.
        if (UiSafeMode.Enabled) return false;
        try { return SharedUiSettings.AnimationsEnabled; }
        catch { return true; }
    }

    private void Relayout()
    {
        double width = ActualWidth > 0 ? ActualWidth : (_compact ? 300 : 480);
        double height = ActualHeight > 0 ? ActualHeight : MinHeight;
        PowerFlowSnapshot flow = _flow ?? new PowerFlowSnapshot();
        bool onBattery = flow.AdapterConnected == false;
        bool hasBattery = flow.AdapterConnected is not null;

        double tile = _compact ? 26 : 52;
        double captionSpace = _compact ? 0 : 20;
        double inset = _compact ? 0 : 8;
        double usableHeight = Math.Max(tile, height - captionSpace);
        double maxThickness = _compact ? 7 : 16;
        double minThickness = _compact ? 2 : 3;

        ApplyTileMetrics(ChargerTile, ChargerIcon, tile);
        ApplyTileMetrics(SystemTile, SystemIcon, tile);
        ApplyTileMetrics(BatteryTile, BatteryIcon, tile);
        bool showCaptions = !_compact;
        ChargerCaption.Visibility = SystemCaption.Visibility = BatteryCaption.Visibility = showCaptions ? Visibility.Visible : Visibility.Collapsed;
        foreach (TextBlock text in new[] { AcSystemLabelText, AcBatteryLabelText, DcSystemLabelText }) text.FontSize = _compact ? 11 : 12;
        foreach (Border label in new[] { AcSystemLabel, AcBatteryLabel, DcSystemLabel })
        {
            label.Padding = _compact ? new Thickness(4, 0, 4, 1) : new Thickness(6, 1, 6, 2);
            label.CornerRadius = new CornerRadius(_compact ? 6 : 8);
        }

        double leftX = inset;
        double rightX = Math.Max(leftX + tile * 3, width - inset - tile);
        double midY = usableHeight / 2;

        BatteryIcon.Glyph = BatteryGlyph(flow);
        ChargerCaption.Text = flow.AdapterRatedW.HasValue
            ? Localization.Format("PowerFlowAdapterRated", flow.AdapterRatedW.Watts)
            : Localization.Get("PowerFlowCharger");

        if (onBattery)
        {
            // Battery feeds the system.
            ChargerTile.Visibility = Visibility.Collapsed;
            ChargerCaption.Visibility = Visibility.Collapsed;
            BatteryTile.Visibility = Visibility.Visible;
            SystemTile.Visibility = Visibility.Visible;
            PlaceTile(BatteryTile, BatteryCaption, leftX, midY - tile / 2, tile);
            PlaceTile(SystemTile, SystemCaption, rightX, midY - tile / 2, tile);
            _acSystem.Hide();
            _acBattery.Hide();
            PowerReading draw = flow.SystemW.HasValue ? flow.SystemW : Negate(flow.BatteryW);
            _dcSystem.Show(new Point(leftX + tile, midY), new Point(rightX, midY), draw, minThickness, maxThickness, _compact);
        }
        else
        {
            ChargerTile.Visibility = Visibility.Visible;
            SystemTile.Visibility = Visibility.Visible;
            BatteryTile.Visibility = hasBattery ? Visibility.Visible : Visibility.Collapsed;
            if (!hasBattery) BatteryCaption.Visibility = Visibility.Collapsed;
            _dcSystem.Hide();
            PlaceTile(ChargerTile, ChargerCaption, leftX, midY - tile / 2, tile);
            if (hasBattery)
            {
                double spread = _compact ? 15 : Math.Min(usableHeight / 4, tile * 0.9);
                double systemY = Math.Max(tile / 2, midY - spread);
                double batteryY = Math.Min(usableHeight - tile / 2, midY + spread);
                if (_compact) { systemY = tile / 2 + 1; batteryY = usableHeight - tile / 2 - 1; }
                PlaceTile(SystemTile, SystemCaption, rightX, systemY - tile / 2, tile);
                PlaceTile(BatteryTile, BatteryCaption, rightX, batteryY - tile / 2, tile);
                double fan = tile * 0.16;
                _acSystem.Show(new Point(leftX + tile, midY - fan), new Point(rightX, systemY), flow.SystemW, minThickness, maxThickness, _compact);
                PowerReading charge = flow.BatteryW.HasValue && flow.BatteryW.Watts < 0 ? PowerReading.Measured(0) : flow.BatteryW;
                _acBattery.Show(new Point(leftX + tile, midY + fan), new Point(rightX, batteryY), charge, minThickness, maxThickness, _compact);
            }
            else
            {
                PlaceTile(SystemTile, SystemCaption, rightX, midY - tile / 2, tile);
                _acSystem.Show(new Point(leftX + tile, midY), new Point(rightX, midY), flow.SystemW, minThickness, maxThickness, _compact);
                _acBattery.Hide();
            }
        }

        UpdateDescription(flow, onBattery, hasBattery);
        UpdateAnimationState();
        if (!_timer.IsRunning) RenderStaticDots();
    }

    private static PowerReading Negate(PowerReading reading) =>
        reading.HasValue ? reading with { Watts = Math.Abs(reading.Watts!.Value) } : reading;

    private static string BatteryGlyph(PowerFlowSnapshot flow) =>
        flow.AdapterConnected == true && flow.BatteryW.HasValue && flow.BatteryW.Watts > 0 ? "" : "";

    private static void ApplyTileMetrics(Border tile, FontIcon icon, double size)
    {
        tile.Width = size;
        tile.Height = size;
        tile.CornerRadius = new CornerRadius(size >= 40 ? 12 : 7);
        icon.FontSize = size >= 40 ? 22 : 13;
    }

    private static void PlaceTile(Border tile, TextBlock caption, double x, double y, double size)
    {
        Canvas.SetLeft(tile, x);
        Canvas.SetTop(tile, y);
        if (caption.Visibility != Visibility.Visible) return;
        caption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(caption, x + size / 2 - caption.DesiredSize.Width / 2);
        Canvas.SetTop(caption, y + size + 3);
    }

    private void UpdateDescription(PowerFlowSnapshot flow, bool onBattery, bool hasBattery)
    {
        string charger = Localization.Get("PowerFlowCharger");
        string system = Localization.Get("PowerFlowSystem");
        string battery = Localization.Get("PowerFlowBattery");
        var lines = new List<string>();
        if (onBattery)
        {
            lines.Add(Line(battery, system, flow.SystemW.HasValue ? flow.SystemW : Negate(flow.BatteryW)));
        }
        else
        {
            lines.Add(Line(charger, system, flow.SystemW));
            if (hasBattery) lines.Add(Line(charger, battery, flow.BatteryW));
        }
        string summary = string.Join("\n", lines);
        AutomationProperties.SetName(this, summary);
        ToolTipService.SetToolTip(this, summary + "\n\n" + Localization.Get("PowerFlowLegend"));
    }

    private static string Line(string from, string to, PowerReading reading) =>
        Localization.Format("PowerFlowLineFormat", from, to, FormatWatts(reading), Localization.Get(reading.Quality switch
        {
            PowerDataQuality.Measured when reading.HasValue => "PowerFlowQualityMeasured",
            PowerDataQuality.Estimated when reading.HasValue => "PowerFlowQualityEstimated",
            _ => "PowerFlowQualityUnavailable",
        }));

    internal static string FormatWatts(PowerReading reading)
    {
        if (!reading.HasValue) return Localization.Get("PowerFlowNoValue");
        double watts = Math.Abs(reading.Watts!.Value);
        return Localization.Format(reading.Quality == PowerDataQuality.Estimated ? "PowerFlowWattsEstimated" : "PowerFlowWatts", watts);
    }

    private bool ShouldAnimate =>
        _loaded && _animationsEnabled && Visibility == Visibility.Visible
        && XamlRoot is { IsHostVisible: true }
        && (_acSystem.IsMoving || _acBattery.IsMoving || _dcSystem.IsMoving);

    private void UpdateAnimationState()
    {
        if (ShouldAnimate)
        {
            if (_timer.IsRunning) return;
            _lastFrame = DateTimeOffset.UtcNow;
            _timer.Start();
        }
        else if (_timer.IsRunning)
        {
            _timer.Stop();
            RenderStaticDots();
        }
    }

    private void RenderStaticDots()
    {
        _acSystem.ResetOffset();
        _acBattery.ResetOffset();
        _dcSystem.ResetOffset();
    }

    private void OnFrame(DispatcherQueueTimer sender, object args)
    {
        if (!ShouldAnimate)
        {
            UpdateAnimationState();
            return;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        double seconds = Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.2);
        _lastFrame = now;
        _acSystem.Advance(seconds);
        _acBattery.Advance(seconds);
        _dcSystem.Advance(seconds);
    }

    /// <summary>One flow line: translucent base ribbon, travelling dots, and a dashed stand-in when unavailable.</summary>
    private sealed class Ribbon
    {
        private readonly Path _base;
        private readonly Path _dots;
        private readonly Path _missing;
        private readonly Border _label;
        private readonly TextBlock _labelText;
        private double _speedPixels;
        private double _offset;

        public Ribbon(Path basePath, Path dots, Path missing, Border label, TextBlock labelText)
        {
            _base = basePath;
            _dots = dots;
            _missing = missing;
            _label = label;
            _labelText = labelText;
        }

        public bool IsMoving => _dots.Visibility == Visibility.Visible && _speedPixels > 0;

        public void Hide()
        {
            _base.Visibility = _dots.Visibility = _missing.Visibility = _label.Visibility = Visibility.Collapsed;
            _speedPixels = 0;
        }

        public void Show(Point from, Point to, PowerReading reading, double minThickness, double maxThickness, bool compact)
        {
            _label.Visibility = Visibility.Visible;
            _labelText.Text = FormatWatts(reading);
            if (!reading.HasValue)
            {
                _base.Visibility = _dots.Visibility = Visibility.Collapsed;
                _missing.Visibility = Visibility.Visible;
                _missing.Data = Curve(from, to);
                _speedPixels = 0;
                PlaceLabel(from, to);
                return;
            }

            double watts = Math.Abs(reading.Watts!.Value);
            double thickness = Math.Clamp(minThickness + watts * (compact ? 0.12 : 0.3), minThickness, maxThickness);
            _missing.Visibility = Visibility.Collapsed;
            _base.Visibility = Visibility.Visible;
            _base.StrokeThickness = thickness;
            _base.Data = Curve(from, to);
            _base.StrokeDashArray = reading.Quality == PowerDataQuality.Estimated ? new DoubleCollection { 2, 1 } : null;

            bool flowing = watts >= 0.05;
            _dots.Visibility = flowing ? Visibility.Visible : Visibility.Collapsed;
            double dot = Math.Max(compact ? 2 : 3, thickness * 0.42);
            _dots.StrokeThickness = dot;
            _dots.Data = Curve(from, to);
            double gapPixels = compact ? 14 : 22;
            _dots.StrokeDashArray = new DoubleCollection { 0, gapPixels / dot };
            _speedPixels = flowing ? Math.Clamp(14 + watts * 1.6, 14, compact ? 60 : 90) : 0;
            PlaceLabel(from, to);
        }

        public void Advance(double seconds)
        {
            if (!IsMoving) return;
            _offset -= _speedPixels * seconds / Math.Max(1, _dots.StrokeThickness);
            if (_offset < -10000) _offset = 0;
            _dots.StrokeDashOffset = _offset;
        }

        public void ResetOffset()
        {
            _offset = 0;
            _dots.StrokeDashOffset = 0;
        }

        private void PlaceLabel(Point from, Point to)
        {
            _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Point mid = new((from.X + to.X) / 2, (from.Y + to.Y) / 2);
            Canvas.SetLeft(_label, mid.X - _label.DesiredSize.Width / 2);
            Canvas.SetTop(_label, mid.Y - _label.DesiredSize.Height / 2);
        }

        private static Geometry Curve(Point from, Point to)
        {
            double midX = (from.X + to.X) / 2;
            var figure = new PathFigure { StartPoint = from, IsClosed = false, IsFilled = false };
            figure.Segments.Add(new BezierSegment
            {
                Point1 = new Point(midX, from.Y),
                Point2 = new Point(midX, to.Y),
                Point3 = to,
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }
    }
}
