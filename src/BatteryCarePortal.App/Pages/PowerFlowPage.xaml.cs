using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Portal.Core.Battery;
using Portal.Core.UI.Controls;

namespace BatteryCarePortal.Pages;

public sealed partial class PowerFlowPage : Page
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly PowerFlowView _flow = new();

    public PowerFlowPage()
    {
        InitializeComponent();
        LayoutHelper.AlignToStart(Column);
        LayoutHelper.AlignToStart(SimulatedChip);
        FlowHost.Child = _flow;
        LegendText.Text = Portal.Core.UI.Localization.Get("PowerFlowLegend");
        Loaded += (_, _) =>
        {
            AppServices.Battery.SnapshotChanged += OnSnapshotChanged;
            Refresh();
        };
        Unloaded += (_, _) => AppServices.Battery.SnapshotChanged -= OnSnapshotChanged;
    }

    private void OnSnapshotChanged(object? sender, BatterySnapshot snapshot) => _dispatcher.TryEnqueue(Refresh);

    private void Refresh()
    {
        try
        {
            BatterySnapshot battery = AppServices.Battery.Current;
            LaptopPowerModel model = LaptopPowerModelBuilder.Build(battery);
            _flow.Update(model.Flow);
            SimulatedChip.Visibility = battery.IsSimulated ? Visibility.Visible : Visibility.Collapsed;
            StateText.Text = Strings.Get(model.State switch
            {
                LaptopPowerState.Charging => "PFStateCharging",
                LaptopPowerState.PluggedIn => "PFStatePluggedIn",
                LaptopPowerState.OnBattery => "PFStateOnBattery",
                _ => "PFStateNoBattery",
            });
            SummaryText.Text = Sentence(model);
        }
        catch (Exception) { }
    }

    // The same data as the picture in words; a value the laptop does not report is said so, never filled in.
    private static string Sentence(LaptopPowerModel m)
    {
        var parts = new List<string>();
        switch (m.State)
        {
            case LaptopPowerState.NoBattery:
                return Strings.Get("PFLineNoBattery");
            case LaptopPowerState.Charging:
                parts.Add(m.BatteryNotReported
                    ? Strings.Get("PFBatteryNotReported")
                    : string.Format(Strings.Get("PFLineCharging"), BatteryFormat.Watts(Math.Abs(m.Flow.BatteryW.Watts!.Value)).TrimStart('+')));
                if (m.SystemNotReported) parts.Add(Strings.Get("PFSystemNotReported"));
                break;
            case LaptopPowerState.PluggedIn:
                parts.Add(Strings.Get("PFLinePluggedIn"));
                if (m.SystemNotReported) parts.Add(Strings.Get("PFSystemNotReported"));
                break;
            case LaptopPowerState.OnBattery:
                parts.Add(m.SystemNotReported
                    ? Strings.Get("PFSystemNotReported")
                    : string.Format(Strings.Get("PFLineOnBattery"), BatteryFormat.Watts(m.Flow.SystemW.Watts).TrimStart('+')));
                if (m.TimeRemaining is TimeSpan left && left > TimeSpan.Zero)
                    parts.Add(string.Format(Strings.Get("PFTimeLeft"), $"{(int)left.TotalHours} h {left.Minutes} min"));
                break;
        }
        return string.Join(" ", parts);
    }
}
