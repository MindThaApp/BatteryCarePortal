using System.Globalization;
using System.Reflection;
using System.Xml.Linq;

namespace Portal.Core.UI;

/// <summary>
/// Text lookup for the shared controls. The host app can set <see cref="Resolver"/> to answer from its own (translated)
/// resources; anything it returns null for falls back to the English strings embedded in this library.
/// </summary>
public static class Localization
{
    private static readonly Lazy<Dictionary<string, string>> English = new(LoadEnglish);

    public static Func<string, string?>? Resolver { get; set; }

    public static string? TryGet(string key) => Resolver?.Invoke(key) is { Length: > 0 } text ? text : English.Value.GetValueOrDefault(key);

    public static string Get(string key) => Resolver?.Invoke(key) is { Length: > 0 } text ? text : English.Value.GetValueOrDefault(key, string.Empty);

    public static string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    private static Dictionary<string, string> LoadEnglish()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        Assembly assembly = typeof(Localization).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream("Portal.Core.UI.PowerFlow.en-US.resw");
        if (stream is null) return map;
        foreach (XElement data in XDocument.Load(stream).Root!.Elements("data"))
            map[(string)data.Attribute("name")!] = data.Element("value")?.Value ?? string.Empty;
        return map;
    }
}

/// <summary>Lets the host app tell the shared controls to skip animation (for example after a crash loop).</summary>
public static class UiSafeMode
{
    public static bool Enabled { get; set; }
}
