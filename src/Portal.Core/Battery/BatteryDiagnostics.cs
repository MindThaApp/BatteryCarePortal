using System.Globalization;
using System.Text;
using Portal.Core.Battery.Backends;

namespace Portal.Core.Battery;

/// <summary>
/// Read-only report of the battery, every backend probe and the energy meters, for verifying support on a
/// real device (app argument <c>--battery-diag</c>). Nothing is written to the firmware.
/// </summary>
public static class BatteryDiagnostics
{
    public const string CommandLineSwitch = "--battery-diag";

    public static string DefaultReportPath =>
        Path.Combine(Path.GetTempPath(), "BatteryCarePortal-battery-diag.txt");

    /// <summary>Writes the report to a new file and returns its path; an existing file is never overwritten (a numbered name is used instead). Never throws for device errors.</summary>
    public static string WriteReport(string? path = null)
    {
        path ??= DefaultReportPath;
        string report = BuildReport();
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        string candidate = path;
        for (int n = 2; ; n++)
        {
            try
            {
                using var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.Write(report);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
            }
        }
    }

    public static string BuildReport()
    {
        var sb = new StringBuilder();
        var wmi = new SystemManagementWmiReader();
        void Line(string text = "") => sb.AppendLine(text);
        void Safe(string title, Action action)
        {
            try { action(); }
            catch (Exception ex) { Line($"{title}: failed ({ex.GetType().Name}: {ex.Message})"); }
        }

        Line("Battery Care Portal diagnostics (read-only)");
        Line($"Time (UTC): {DateTimeOffset.UtcNow:O}");
        Line($"OS: {Environment.OSVersion.VersionString}; 64-bit process: {Environment.Is64BitProcess}");
        Line($"Fake battery (PBC_FAKE_BATTERY / PPM_FAKE_BATTERY): {BatteryEnvironment.IsFakeBatteryEnabled}");
        Safe("System", () =>
        {
            foreach (var row in wmi.Query(@"root\cimv2", "SELECT Manufacturer, Model, SystemFamily FROM Win32_ComputerSystem"))
                Line($"System: {row.AsString("Manufacturer")} / {row.AsString("Model")} / family {row.AsString("SystemFamily")}");
            foreach (var row in wmi.Query(@"root\cimv2", "SELECT Version, Name FROM Win32_ComputerSystemProduct"))
                Line($"Product: {row.AsString("Name")} / {row.AsString("Version")}");
            foreach (var row in wmi.Query(@"root\cimv2", "SELECT SMBIOSBIOSVersion, Manufacturer FROM Win32_BIOS"))
                Line($"BIOS: {row.AsString("Manufacturer")} {row.AsString("SMBIOSBIOSVersion")}");
        });

        Line();
        Line("[Snapshot]");
        Safe("Snapshot", () =>
        {
            BatterySnapshot snapshot = new WindowsBatterySnapshotSource(wmi).Read();
            foreach (var property in typeof(BatterySnapshot).GetProperties())
                Line($"{property.Name}: {Format(property.GetValue(snapshot))}");
        });

        Line();
        Line("[root\\wmi battery classes]");
        foreach (string cls in new[] { "BatteryStaticData", "BatteryFullChargedCapacity", "BatteryStatus", "BatteryCycleCount", "BatteryTemperature" })
        {
            Safe(cls, () =>
            {
                var rows = wmi.Query(@"root\wmi", $"SELECT * FROM {cls}");
                Line($"{cls}: {rows.Count} instance(s)");
                foreach (var row in rows)
                    Line("  " + string.Join("; ", row.Where(kv => kv.Value is not null && kv.Key != "InstanceName")
                        .Select(kv => $"{kv.Key}={Format(kv.Value)}")));
            });
        }

        Line();
        Line("[Lenovo energy-management driver]");
        Safe("Lenovo", () =>
        {
            DeviceIoResult query = new LenovoEnergyDriver().Send(LenovoConservationBackend.QueryCode);
            if (query.Success)
            {
                Line($"Battery mode word: 0x{query.Output:X8} (binary {Convert.ToString(query.Output, 2).PadLeft(32, '0')})");
                Line($"Conservation bit {LenovoConservationBackend.ConservationBit}: {(LenovoConservationBackend.IsConservationOn(query.Output) ? "on" : "off")}");
            }
            else
            {
                Line($"Query failed: Win32 error {query.Win32Error}{(query.DeviceMissing ? " (driver not installed)" : query.AccessDenied ? " (access denied)" : string.Empty)}");
            }
        });

        Line();
        Line("[Backends] (probe ignores the verified-models list; nothing is written)");
        IChargeLimitBackend? selected = null;
        Safe("Selection", () => selected = ChargeLimitBackendRegistry.Detect(wmi));
        Line($"Selected backend: {selected?.Name ?? "none (software reminders only)"}");
        foreach (IChargeLimitBackend backend in ChargeLimitBackendRegistry.CreateCandidates(wmi))
        {
            Safe(backend.Id, () =>
            {
                bool probe = backend is ChargeLimitBackendBase b ? b.ProbeForDiagnostics() : backend.Detect();
                Line($"{backend.Id} ({backend.Confidence}): probe {(probe ? "found" : "not found")}");
                if (backend is LenovoConservationBackend lenovo && lenovo.DetectionDetail is string detail) Line($"  detail: {detail}");
                if (!probe) return;
                ChargeLimitCapabilities caps = backend.Capabilities;
                Line($"  capabilities: {caps.Kind}, {caps.MinLimit}-{caps.MaxLimit}, fixed [{string.Join(",", caps.DiscreteValues)}], start threshold {caps.SupportsStartThreshold}, pause {caps.CanPauseCharging}");
                ChargeLimitState state = backend.GetState();
                Line($"  state: limit {Format(state.Limit)}, start {Format(state.StartThreshold)}, paused {Format(state.ChargingPaused)}, raw {Format(state.RawValue)}, message {Format(state.Message)}");
            });
        }

        Line();
        Line("[Energy meters (EMI)]");
        Safe("EMI", () =>
        {
            var meter = new EnergyMeterReader();
            IReadOnlyList<string> meters = meter.DescribeMeters();
            Line(meters.Count == 0 ? "No readable energy meter." : string.Join(Environment.NewLine, meters));
            if (meters.Count > 0)
            {
                meter.Sample();
                Thread.Sleep(1000);
                EnergyMeterSample? sample = meter.Sample();
                foreach (EmiChannelPower channel in sample?.Channels ?? Array.Empty<EmiChannelPower>())
                    Line($"  {channel.Name}: {Format(channel.Watts)} W");
                Line($"  System channel: {Format(sample?.SystemW)} W");
            }
        });

        return sb.ToString();
    }

    private static string Format(object? value) => value switch
    {
        null => "—",
        string[] array => "[" + string.Join(",", array) + "]",
        Array array => "[" + string.Join(",", array.Cast<object>()) + "]",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "—",
    };
}
