namespace Portal.Core.Battery;

/// <summary>What the firmware reports about this computer, used only to match the verified-models list.</summary>
public sealed record DeviceIdentity(string Manufacturer, string Model, string Family, string ProductVersion)
{
    public static DeviceIdentity Read(IWmiReader wmi)
    {
        IReadOnlyDictionary<string, object?>? system = wmi.Query(@"root\cimv2", "SELECT Manufacturer, Model, SystemFamily FROM Win32_ComputerSystem").FirstOrDefault();
        IReadOnlyDictionary<string, object?>? product = wmi.Query(@"root\cimv2", "SELECT Version FROM Win32_ComputerSystemProduct").FirstOrDefault();
        return new DeviceIdentity(
            system?.AsString("Manufacturer") ?? string.Empty,
            system?.AsString("Model") ?? string.Empty,
            system?.AsString("SystemFamily") ?? string.Empty,
            product?.AsString("Version") ?? string.Empty);
    }
}

/// <summary>One verified device family for one backend. A device matches when its manufacturer contains
/// <see cref="Manufacturer"/> and its family, product version or model starts with one of <see cref="ModelPrefixes"/>.</summary>
public sealed record VerifiedModelRule(string BackendId, string Manufacturer, IReadOnlyList<string> ModelPrefixes);

/// <summary>
/// The single source of "verified" models. To enable a model after it has been verified on real hardware, add a rule
/// to <see cref="Rules"/>. Dell, HP and ASUS backends have no rule: they write only through the user's beta opt-in,
/// which <see cref="WriteGate"/> decides separately without ever editing this list.
/// </summary>
public static class VerifiedModels
{
    public static IReadOnlyList<VerifiedModelRule> Rules { get; } = new[]
    {
        new VerifiedModelRule("lenovo-conservation", "LENOVO", new[] { "IdeaPad", "Yoga" }),
    };

    public static bool IsVerified(string backendId, DeviceIdentity device) => IsVerified(Rules, backendId, device);

    public static bool IsVerified(IReadOnlyList<VerifiedModelRule> rules, string backendId, DeviceIdentity device)
    {
        foreach (VerifiedModelRule rule in rules)
        {
            if (!rule.BackendId.Equals(backendId, StringComparison.Ordinal)) continue;
            if (!device.Manufacturer.Contains(rule.Manufacturer, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string candidate in new[] { device.Family, device.ProductVersion, device.Model })
            {
                string name = StripBrand(candidate);
                if (rule.ModelPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return true;
            }
        }
        return false;
    }

    private static string StripBrand(string value)
    {
        string s = value.Trim();
        return s.StartsWith("Lenovo ", StringComparison.OrdinalIgnoreCase) ? s[7..].TrimStart() : s;
    }
}
