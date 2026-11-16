using Microsoft.Windows.ApplicationModel.Resources;

namespace PortalBatteryCare;

internal static class Strings
{
    private static readonly ResourceLoader Loader = new();

    public static string Get(string key) => Loader.GetString(key);
}
