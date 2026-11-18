namespace Portal.Core.Battery;

public enum FirstRunStep
{
    Welcome,
    DeviceCheck,
    ChargeLimit,
    PowerPlanManagerNote,
    StartWithWindows,
    Notifications,
    /// <summary>A separate, off-by-default choice for anonymous crash reports. Only the slot exists; a later change wires it.</summary>
    CrashReports,
    Finish,
}

/// <summary>What the device-check page tells the user to do next.</summary>
public enum FirstRunNextAction
{
    /// <summary>Supported: the charge-limit page follows and offers to turn the limit on.</summary>
    OfferChargeLimit,
    /// <summary>Supported, but Power Plan Manager owns charging: one app writes the limit, so nothing is offered.</summary>
    PowerPlanManagerOwnsCharging,
    /// <summary>A verified model whose maker's driver or interface was not found.</summary>
    InstallMakerDriver,
    /// <summary>On the test list: the Settings page has the opt-in to help test.</summary>
    ConsiderBetaTesting,
    /// <summary>No usable interface: reminders only.</summary>
    UseReminders,
    /// <summary>No battery: nothing to care for.</summary>
    NothingToDo,
}

public sealed record FirstRunPlan(FirstRunNextAction Action, IReadOnlyList<FirstRunStep> Steps)
{
    /// <summary>True only for the one page that may start a firmware change, and only when the user presses its button.</summary>
    public bool OffersChargeLimit => Steps.Contains(FirstRunStep.ChargeLimit);
}

/// <summary>Pure: which first-run pages and next action each device state gets, and whether the flow shows at all.</summary>
public static class FirstRunLogic
{
    /// <summary>Shown once: until the user has finished or skipped it.</summary>
    public static bool ShouldShow(BatterySettings settings) => settings.FirstRunCompletedUtc is null;

    public static FirstRunPlan For(DeviceSupportState state, PowerPlanManagerStatus powerPlanManager)
    {
        FirstRunNextAction action = state switch
        {
            DeviceSupportState.Supported when powerPlanManager.OwnsCharging => FirstRunNextAction.PowerPlanManagerOwnsCharging,
            DeviceSupportState.Supported => FirstRunNextAction.OfferChargeLimit,
            DeviceSupportState.OneStepMissing => FirstRunNextAction.InstallMakerDriver,
            DeviceSupportState.OnTestList => FirstRunNextAction.ConsiderBetaTesting,
            DeviceSupportState.MonitorMode => FirstRunNextAction.UseReminders,
            _ => FirstRunNextAction.NothingToDo,
        };

        var steps = new List<FirstRunStep> { FirstRunStep.Welcome, FirstRunStep.DeviceCheck };
        switch (action)
        {
            case FirstRunNextAction.OfferChargeLimit:
                steps.Add(FirstRunStep.ChargeLimit);
                break;
            case FirstRunNextAction.PowerPlanManagerOwnsCharging:
                steps.Add(FirstRunStep.PowerPlanManagerNote);
                break;
        }
        if (action != FirstRunNextAction.NothingToDo)
        {
            steps.Add(FirstRunStep.StartWithWindows);
            steps.Add(FirstRunStep.Notifications);
        }
        steps.Add(FirstRunStep.CrashReports);
        steps.Add(FirstRunStep.Finish);
        return new FirstRunPlan(action, steps);
    }
}
