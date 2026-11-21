namespace BatteryCarePortal;

internal static class AppVersion
{
    /// <summary>Major.minor.build of the package, for example 0.4.0. Empty when the package identity is not available.</summary>
    public static string Short
    {
        get
        {
            try
            {
                Windows.ApplicationModel.PackageVersion v = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch (Exception) { return string.Empty; }
        }
    }
}
