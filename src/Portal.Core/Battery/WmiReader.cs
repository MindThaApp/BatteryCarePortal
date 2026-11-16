using System.Management;

namespace Portal.Core.Battery;

/// <summary>
/// Read-only WMI access used by snapshot sources and backend detection. Writes never go through
/// this interface: they are expressed as a <see cref="BatteryWritePlan"/> and run by the elevated helper.
/// </summary>
public interface IWmiReader
{
    /// <summary>Runs a WQL query. Returns an empty list when the namespace or class is missing or access is denied.</summary>
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string wmiNamespace, string wql);

    /// <summary>
    /// Calls a read-only (getter) method on the first instance of a class, for example a status query.
    /// Returns null when the class, instance or method is missing or the call fails.
    /// </summary>
    IReadOnlyDictionary<string, object?>? InvokeGetter(string wmiNamespace, string className, string methodName,
        IReadOnlyDictionary<string, object> arguments);
}

public sealed class SystemManagementWmiReader : IWmiReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string wmiNamespace, string wql)
    {
        var results = new List<IReadOnlyDictionary<string, object?>>();
        try
        {
            var scope = new ManagementScope(wmiNamespace, new ConnectionOptions { Timeout = Timeout });
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql),
                new System.Management.EnumerationOptions { Timeout = Timeout, ReturnImmediately = true, Rewindable = false });
            using ManagementObjectCollection collection = searcher.Get();
            foreach (ManagementBaseObject item in collection)
            {
                using (item)
                {
                    results.Add(ToDictionary(item));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException
                                       or System.Runtime.InteropServices.COMException or TimeoutException
                                       or InvalidOperationException or ArgumentException)
        {
            // Missing namespace / class / access denied all mean "not available".
        }
        return results;
    }

    public IReadOnlyDictionary<string, object?>? InvokeGetter(string wmiNamespace, string className, string methodName,
        IReadOnlyDictionary<string, object> arguments)
    {
        try
        {
            var scope = new ManagementScope(wmiNamespace, new ConnectionOptions { Timeout = Timeout });
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM {className}"),
                new System.Management.EnumerationOptions { Timeout = Timeout, ReturnImmediately = true, Rewindable = false });
            using ManagementObjectCollection collection = searcher.Get();
            foreach (ManagementBaseObject item in collection)
            {
                using (item)
                {
                    if (item is not ManagementObject instance) continue;
                    using ManagementBaseObject inParams = instance.GetMethodParameters(methodName);
                    foreach ((string name, object value) in arguments) inParams[name] = value;
                    using ManagementBaseObject outParams = instance.InvokeMethod(methodName, inParams, null);
                    return outParams is null ? null : ToDictionary(outParams);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException
                                       or System.Runtime.InteropServices.COMException or TimeoutException
                                       or InvalidOperationException or ArgumentException)
        {
        }
        return null;
    }

    private static Dictionary<string, object?> ToDictionary(ManagementBaseObject item)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (PropertyData property in item.Properties) map[property.Name] = property.Value;
        return map;
    }
}

internal static class WmiValue
{
    public static long? AsLong(this IReadOnlyDictionary<string, object?> row, string name)
    {
        if (!row.TryGetValue(name, out object? value) || value is null) return null;
        try { return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return null; }
    }

    public static bool? AsBool(this IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out object? value) && value is bool b ? b : null;

    public static string? AsString(this IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out object? value) ? value?.ToString() : null;

    public static string[] AsStringArray(this IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out object? value) && value is string[] values ? values : Array.Empty<string>();
}
