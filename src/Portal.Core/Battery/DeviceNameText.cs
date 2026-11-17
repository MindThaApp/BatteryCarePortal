namespace Portal.Core.Battery;

public static class DeviceNameText
{
    /// <summary>"Vendor Model", without repeating the vendor when the model already starts with it.</summary>
    public static string Join(string? manufacturer, string? model)
    {
        string m = (manufacturer ?? string.Empty).Trim(), n = (model ?? string.Empty).Trim();
        if (m.Length == 0) return n;
        if (n.Length == 0) return m;
        return n.StartsWith(m, StringComparison.OrdinalIgnoreCase) ? n : m + " " + n;
    }
}
