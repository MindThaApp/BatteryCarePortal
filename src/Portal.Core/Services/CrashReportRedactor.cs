using System.Text;
using System.Text.RegularExpressions;

namespace Portal.Core.Services;

/// <summary>
/// Takes the personal parts out of crash-report text before it leaves the PC. The user profile folder becomes
/// <c>%USERPROFILE%</c>, the app's own folder <c>&lt;app&gt;</c>; in every other path, folder and file names become
/// <c>&lt;folder&gt;</c> and <c>&lt;file&gt;.ext</c> (the Windows and Program Files folders stay whole); a network path's server
/// and share become <c>&lt;server&gt;</c> and <c>&lt;share&gt;</c>; the user name and the PC name become <c>&lt;user&gt;</c> and
/// <c>&lt;machine&gt;</c> wherever they still appear as words. Running it again over its own output changes nothing.
/// </summary>
public sealed class CrashReportRedactor
{
    public const string ProfileToken = "%USERPROFILE%";
    public const string AppToken = "<app>";
    public const string UserToken = "<user>";
    public const string MachineToken = "<machine>";
    public const string FolderToken = "<folder>";
    public const string FileToken = "<file>";
    public const string NameToken = "<name>";
    public const string ServerToken = "<server>";
    public const string ShareToken = "<share>";

    private static readonly Regex PathPattern = new(
        @"(?<![\w%])(?:%USERPROFILE%[\\/]|[A-Za-z]:[\\/]|\\\\(?=\w))(?:(?!\s(?:[A-Za-z]:[\\/]|\\\\))[^:*?""<>|\r\n])*",
        RegexOptions.CultureInvariant);

    private static readonly Regex ExtensionInText = new(@"\.[A-Za-z0-9]{1,8}(?=$|[\s,;)\]]|\.(?:\s|$))", RegexOptions.CultureInvariant);

    private const string DocumentExtensions =
        "pdf|docx?|xlsx?|pptx?|txt|rtf|csv|odt|ods|jpe?g|png|gif|bmp|heic|tiff?|webp|svg|mp3|mp4|mkv|avi|mov|wav|flac|zip|7z|rar|log|bak|tmp";

    // A file name with no folder in front: quoted (spaces allowed), as in "Could not find file 'Tax 2025.pdf'", or a plain word.
    private static readonly Regex QuotedFileName = new(
        @"(?<quote>['""])(?<name>[^'""<>\\/\r\n]+?)\.(?<ext>" + DocumentExtensions + @")\k<quote>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex BareFileName = new(
        @"(?<![\w<%\\/.\-])[\w\-]+(?:\.[\w\-]+)*\.(?<ext>" + DocumentExtensions + @")(?![\w])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly (string Prefix, string Token)[] _prefixes;
    private readonly string[] _keptRoots;
    private readonly Regex? _user;
    private readonly Regex? _machine;

    public CrashReportRedactor(string? userProfile, string? userName, string? machineName, string? appDirectory = null, IEnumerable<string>? keptRoots = null)
    {
        var prefixes = new List<(string, string)>();
        AddPrefix(prefixes, appDirectory, AppToken);
        AddPrefix(prefixes, userProfile, ProfileToken);
        _prefixes = [.. prefixes.OrderByDescending(static p => p.Item1.Length)];
        _keptRoots = [.. (keptRoots ?? []).Where(static r => !string.IsNullOrWhiteSpace(r)).Select(static r => r.TrimEnd('\\', '/'))];
        _user = WordPattern(userName);
        _machine = WordPattern(machineName);
    }

    public static CrashReportRedactor ForThisPc(string? appDirectory) =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName, appDirectory,
            [
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ]);

    /// <summary>The text with every personal part replaced (null and empty stay as they are).</summary>
    public string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        string result = ReplacePrefixes(text);
        result = PathPattern.Replace(result, m => RedactPath(m.Value));
        result = QuotedFileName.Replace(result, m => m.Groups["quote"].Value + FileToken + "." + m.Groups["ext"].Value + m.Groups["quote"].Value);
        result = BareFileName.Replace(result, m => FileToken + "." + m.Groups["ext"].Value);
        // The machine name first: it may hold the user name ("JDOE-LAPTOP").
        if (_machine is not null) result = _machine.Replace(result, MachineToken);
        if (_user is not null) result = _user.Replace(result, UserToken);
        return result;
    }

    private static void AddPrefix(List<(string, string)> prefixes, string? folder, string token)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        string trimmed = folder.Trim().TrimEnd('\\', '/');
        // A drive root ("C:") would swallow every path on the drive.
        if (trimmed.Length < 4) return;

        string plain = trimmed.Replace('/', '\\');
        foreach (string form in new[] { plain, plain.Replace('\\', '/'), plain.Replace("\\", "\\\\", StringComparison.Ordinal) }.Distinct(StringComparer.Ordinal))
            prefixes.Add((form, token));
    }

    private static Regex? WordPattern(string? word)
    {
        string? trimmed = word?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length < 2) return null;
        // A whole word only, and never inside our own placeholders.
        return new Regex(@"(?<![\w<%])" + Regex.Escape(trimmed) + @"(?![\w>%])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private string ReplacePrefixes(string text)
    {
        foreach ((string prefix, string token) in _prefixes)
        {
            StringBuilder? builder = null;
            int copied = 0, search = 0, index;
            while (search < text.Length && (index = text.IndexOf(prefix, search, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = index + prefix.Length;
                // C:\Users\ann is not C:\Users\anna (nor C:\Users\ann.old).
                bool bounded = end >= text.Length || !ContinuesName(text, end);
                bool startsFresh = index == 0 || !IsNameChar(text[index - 1]);
                if (bounded && startsFresh)
                {
                    builder ??= new StringBuilder(text.Length);
                    builder.Append(text, copied, index - copied).Append(token);
                    copied = search = end;
                }
                else search = index + 1;
            }

            if (builder is not null)
            {
                builder.Append(text, copied, text.Length - copied);
                text = builder.ToString();
            }
        }
        return text;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '~';

    private static bool ContinuesName(string text, int at) =>
        IsNameChar(text[at]) || (text[at] == '.' && at + 1 < text.Length && IsNameChar(text[at + 1]));

    private static bool IsSeparator(char c) => c is '\\' or '/';

    // One matched path (it may run on into the sentence after it; the last name is cut at its extension or the first space).
    private string RedactPath(string path)
    {
        bool unc = path.StartsWith("\\\\", StringComparison.Ordinal);
        bool profile = path.StartsWith(ProfileToken, StringComparison.Ordinal);
        bool drive = !unc && !profile;
        int rootLength = unc ? 2 : profile ? ProfileToken.Length + 1 : 3;
        if (drive && IsKept(path)) return path;

        string root = path[..rootLength];
        string rest = path[rootLength..];
        var names = new List<(string Name, char Separator)>();
        int start = 0;
        for (int i = 0; i < rest.Length; i++)
        {
            if (!IsSeparator(rest[i])) continue;
            names.Add((rest[start..i], rest[i]));
            start = i + 1;
        }

        string last = rest[start..];
        string tail = string.Empty;
        int cut = LastNameLength(last);
        if (cut < last.Length)
        {
            tail = last[cut..];
            last = last[..cut];
        }

        var builder = new StringBuilder(root);
        int total = names.Count + 1;
        for (int index = 0; index < total; index++)
        {
            bool isLast = index == total - 1;
            string name = isLast ? last : names[index].Name;
            if (unc && index < 2) builder.Append(name.Length == 0 || IsPlaceholder(name) ? name : index == 0 ? ServerToken : ShareToken);
            else if (drive && index == 0 && !isLast && name.Equals("Users", StringComparison.OrdinalIgnoreCase)) builder.Append(name);
            else if (drive && index == 1 && names[0].Name.Equals("Users", StringComparison.OrdinalIgnoreCase))
                builder.Append(name.Length == 0 || IsPlaceholder(name) || IsSharedProfile(name) ? name : UserToken);
            else builder.Append(RedactName(name, isLast));
            if (!isLast) builder.Append(names[index].Separator);
        }

        return builder.Append(tail).ToString();
    }

    private static bool IsSharedProfile(string name) =>
        name.Equals("Public", StringComparison.OrdinalIgnoreCase) || name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Default User", StringComparison.OrdinalIgnoreCase) || name.Equals("All Users", StringComparison.OrdinalIgnoreCase);

    private static int LastNameLength(string name)
    {
        Match extension = ExtensionInText.Match(name);
        int cut = extension.Success ? extension.Index + extension.Length : name.IndexOfAny([' ', '\u00A0']) is >= 0 and var space ? space : name.Length;
        while (cut > 0 && name[cut - 1] is '.' or ',' or ';') cut--;
        return cut;
    }

    private bool IsKept(string path)
    {
        foreach (string root in _keptRoots)
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (path.Length == root.Length || IsSeparator(path[root.Length])))
                return true;
        return false;
    }

    private static bool IsPlaceholder(string name) => name.Length > 2 && name[0] == '<' && name[^1] == '>';

    private static string RedactName(string name, bool last)
    {
        if (name.Length == 0 || IsPlaceholder(name)) return name;
        if (!last) return FolderToken;
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1 && name.Length - dot - 1 <= 8 && name.AsSpan(dot + 1).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789") < 0)
            return FileToken + name[dot..];
        return NameToken;
    }
}
