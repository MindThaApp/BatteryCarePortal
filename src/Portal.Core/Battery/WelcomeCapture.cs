using System.Text.Json;

namespace Portal.Core.Battery;

/// <summary>Where the welcome window should open for a screenshot run. Theme is "Light", "Dark" or null (leave as is).</summary>
public sealed record WelcomeCaptureOptions(int Step, string? Theme, double TextScale);

/// <summary>
/// Test hook: with the simulated battery on and an empty welcome-capture.flag in the state folder, an optional
/// welcome-capture.json picks the step, theme and text scale of the welcome window. Read-only; ignored outside fake mode.
/// </summary>
public static class WelcomeCapture
{
    public const string FlagFile = "welcome-capture.flag";
    public const string JsonFile = "welcome-capture.json";

    public static WelcomeCaptureOptions? Read(string stateFolder, bool fakeMode)
    {
        if (!fakeMode || string.IsNullOrEmpty(stateFolder)) return null;
        try
        {
            if (!File.Exists(Path.Combine(stateFolder, FlagFile))) return null;
            string path = Path.Combine(stateFolder, JsonFile);
            return Parse(File.Exists(path) ? File.ReadAllText(path) : null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Bad or missing values fall back to step 1, no theme change and scale 1.0; step and scale are clamped.</summary>
    public static WelcomeCaptureOptions Parse(string? json)
    {
        int step = 1;
        string? theme = null;
        double scale = 1.0;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                    {
                        if (p.NameEquals("step") && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int s)) step = s;
                        else if (p.NameEquals("theme") && p.Value.ValueKind == JsonValueKind.String) theme = ParseTheme(p.Value.GetString());
                        else if (p.NameEquals("textScale") && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out double t)) scale = t;
                    }
                }
            }
            catch (JsonException)
            {
            }
        }
        return new WelcomeCaptureOptions(Math.Clamp(step, 1, 3), theme, double.IsFinite(scale) ? Math.Clamp(scale, 1.0, 2.0) : 1.0);
    }

    private static string? ParseTheme(string? value) =>
        string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase) ? "Light"
        : string.Equals(value, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark"
        : null;
}
