namespace Portal.Core.Services;

/// <summary>
/// Where a deliberate use of a Pro feature is reported (saving a hotkey, creating a rule, taking fan control and so on).
/// The Pro edition points <see cref="Sink"/> at <see cref="LicensingService.NoteProFeatureUsed"/>, which starts the trial on
/// the first use. The Lite edition leaves it empty, so nothing is recorded there. Viewing a page, opening a dialog or
/// background work at startup is not a use and must not call this.
/// </summary>
public static class ProUsage
{
    public static Action<string>? Sink { get; set; }

    public static void Note(string feature)
    {
        try
        {
            Sink?.Invoke(feature);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PortalBatteryCare: Pro use not recorded ({feature}): {ex.Message}");
        }
    }
}
