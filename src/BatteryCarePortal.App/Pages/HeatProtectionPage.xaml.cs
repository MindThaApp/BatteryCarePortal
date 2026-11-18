using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Portal.Core.Battery;
using Windows.UI;

namespace BatteryCarePortal.Pages;

public sealed partial class HeatProtectionPage : Page
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private HeatProtectionModel? _model;

    public HeatProtectionPage()
    {
        InitializeComponent();
        LayoutHelper.AlignToStart(Column);
        LayoutHelper.AlignToStart(SimulatedChip);
        SwitchTitle.Text = Strings.Get("HeatSwitchHeader");
        ThresholdTitle.Text = Strings.Get("HeatThresholdTitle");
        ThresholdRange.Text = Strings.Get("HeatThresholdRange");
        WhatTitle.Text = Strings.Get("HeatWhatTitle");
        NotActiveText.Text = Strings.Get("HeatNotActive");
        Scale.SizeChanged += (_, _) => DrawScale();
        Loaded += (_, _) =>
        {
            AppServices.Battery.SnapshotChanged += OnSnapshotChanged;
            AppServices.SupportChanged += Refresh;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            AppServices.Battery.SnapshotChanged -= OnSnapshotChanged;
            AppServices.SupportChanged -= Refresh;
        };
    }

    private void OnSnapshotChanged(object? sender, BatterySnapshot snapshot) => _dispatcher.TryEnqueue(Refresh);

    private async void Refresh()
    {
        try
        {
            BatterySnapshot battery = AppServices.Battery.Current;
            ChargeLimitCapabilities? caps = AppServices.Charge?.Capabilities;
            HeatProtectionSettings heat = await Task.Run(() => AppServices.Settings.LoadLastGood().HeatProtection);
            Show(HeatProtectionModel.Build(battery, heat, caps), battery.IsSimulated, heat.EndTopUpWhenHot);
        }
        catch (Exception) { }
    }

    private void Show(HeatProtectionModel m, bool simulated, bool endTopUpWhenHot)
    {
        _model = m;
        _updatingSwitch = true;
        EndTopUpSwitch.IsOn = endTopUpWhenHot;
        _updatingSwitch = false;
        EndTopUpSwitch.Visibility = m.Behaviour is HeatBehaviour.PausesCharging or HeatBehaviour.EndsTopUp ? Visibility.Visible : Visibility.Collapsed;
        SimulatedChip.Visibility = simulated ? Visibility.Visible : Visibility.Collapsed;
        ThresholdValue.Text = BatteryFormat.Temperature(m.ThresholdC);
        LessButton.IsEnabled = m.ThresholdC > HeatProtectionModel.MinThresholdC;
        MoreButton.IsEnabled = m.ThresholdC < HeatProtectionModel.MaxThresholdC;

        if (m.Behaviour == HeatBehaviour.NoReading)
        {
            NowText.Text = Strings.Get("HeatNoReading");
            Scale.Visibility = ThresholdRow.Visibility = NotActiveText.Visibility = WhatCard.Visibility = Visibility.Collapsed;
            return;
        }

        Scale.Visibility = ThresholdRow.Visibility = NotActiveText.Visibility = WhatCard.Visibility = Visibility.Visible;
        NowText.Text = string.Format(Strings.Get("HeatNow"), BatteryFormat.Temperature(m.TemperatureC),
            BatteryFormat.Temperature(m.ThresholdC), BatteryFormat.Temperature(m.ResumeC));
        WhatText.Text = m.Behaviour switch
        {
            HeatBehaviour.PausesCharging => Strings.Get("HeatBehPause"),
            HeatBehaviour.EndsTopUp => Strings.Get("HeatBehTopUp"),
            _ => Strings.Get("HeatBehMonitor"),
        };
        DrawScale();
    }

    private bool _updatingSwitch;

    // Only the saved setting changes here; nothing is sent to the charge limit controller.
    private async void OnEndTopUpToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingSwitch) return;
        bool on = EndTopUpSwitch.IsOn;
        try { await Task.Run(() => AppServices.Settings.Update(s => s.HeatProtection.EndTopUpWhenHot = on)); }
        catch (Exception) { }
    }

    // Only the threshold in the settings file changes here; nothing is sent to the charge limit controller.
    private async void OnLess(object sender, RoutedEventArgs e) => await StepAsync(-1);
    private async void OnMore(object sender, RoutedEventArgs e) => await StepAsync(1);

    private async Task StepAsync(int step)
    {
        if (_model is null) return;
        double next = HeatProtectionModel.StepThreshold(_model.ThresholdC, step);
        try { await Task.Run(() => AppServices.Settings.Update(s => s.HeatProtection.MaxTempC = next)); }
        catch (Exception) { }
        Refresh();
    }

    private void DrawScale()
    {
        Scale.Children.Clear();
        HeatProtectionModel? m = _model;
        double w = Scale.ActualWidth;
        if (m is null || m.TemperatureC is null || w < 100) return;

        bool dark = ActualTheme == ElementTheme.Dark;
        var hot = new SolidColorBrush(dark ? Color.FromArgb(255, 255, 168, 105) : Color.FromArgb(255, 169, 72, 11));
        var hotSoft = new SolidColorBrush(dark ? Color.FromArgb(255, 71, 44, 25) : Color.FromArgb(255, 251, 230, 212));
        Brush accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        Brush track = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];
        Brush text = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        Brush surface = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];

        const double pad = 20, barY = 26, barH = 8;
        double span = w - 2 * pad;
        double now = pad + span * m.TemperatureFraction!.Value;
        double limit = pad + span * m.ThresholdFraction;
        Brush fill = m.Zone == HeatZone.Hot ? hot : accent;

        Add(new Rectangle { Width = span, Height = barH, RadiusX = 4, RadiusY = 4, Fill = track, Opacity = 0.35 }, pad, barY);
        Add(new Rectangle { Width = Math.Max(0, pad + span - limit), Height = barH, RadiusX = 4, RadiusY = 4, Fill = hotSoft, Stroke = hot, StrokeThickness = 1 }, limit, barY);
        Add(new Rectangle { Width = Math.Max(0, now - pad), Height = barH, RadiusX = 4, RadiusY = 4, Fill = fill }, pad, barY);
        Add(new Rectangle { Width = 2, Height = 28, Fill = hot }, limit - 1, 16);
        Add(new Ellipse { Width = 16, Height = 16, Fill = surface, Stroke = fill, StrokeThickness = 4 }, now - 8, barY + barH / 2 - 8);

        // The threshold label drops below the scale line when it would collide with the reading.
        bool clash = Math.Abs(limit - now) < 120;
        AddLabel(string.Format(Strings.Get("HeatScaleNow"), BatteryFormat.Temperature(m.TemperatureC)), now, 0, text, center: true);
        AddLabel(string.Format(Strings.Get("HeatScaleThreshold"), BatteryFormat.Temperature(m.ThresholdC)), limit + 4, clash ? 44 : 0, text, center: false);
        foreach (int t in new[] { 20, 30, 40, 50, 60 })
            AddLabel(t == 60 ? "60 °C" : t.ToString(), pad + span * HeatProtectionModel.ScaleFraction(t), 66, text, center: true, small: true);

        AutomationProperties.SetName(Scale, string.Format(Strings.Get("HeatScaleAria"),
            BatteryFormat.Temperature(m.TemperatureC), BatteryFormat.Temperature(m.ThresholdC)));
    }

    private void Add(UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        Scale.Children.Add(element);
    }

    private void AddLabel(string content, double x, double y, Brush brush, bool center, bool small = false)
    {
        var label = new TextBlock { Text = content, Foreground = brush, FontSize = small ? 11 : 12 };
        label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        Add(label, center ? x - label.DesiredSize.Width / 2 : x, y);
    }
}
