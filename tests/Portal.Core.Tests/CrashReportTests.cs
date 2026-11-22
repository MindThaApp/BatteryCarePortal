using Portal.Core.Battery;
using Portal.Core.Services;
using Xunit;

namespace Portal.Core.Tests;

public class CrashReportTests
{
    private static readonly CrashReportRedactor Redactor = new(
        @"C:\Users\jdoe", "jdoe", "JDOE-LAPTOP", @"C:\Users\jdoe\AppData\Local\Packages\App\LocalState",
        [@"C:\Windows", @"C:\Program Files"]);

    [Fact]
    public void ProfilePathBecomesToken_AndNamesAreHidden()
    {
        string text = Redactor.Redact(@"Could not read C:\Users\jdoe\Documents\Tax 2025\return.pdf now")!;
        Assert.DoesNotContain("jdoe", text);
        Assert.DoesNotContain("Tax", text);
        Assert.DoesNotContain("return", text);
        Assert.StartsWith("Could not read %USERPROFILE%", text);
        Assert.EndsWith(".pdf now", text);
    }

    [Fact]
    public void OtherProfileAndDrivePathsAreHidden()
    {
        string text = Redactor.Redact(@"D:\Backups\Anna\notes.txt and C:\Users\anna\Desktop\x.log")!;
        Assert.DoesNotContain("Backups", text);
        Assert.DoesNotContain("Anna", text);
        Assert.DoesNotContain("anna", text);
        Assert.Contains(@"C:\Users\<user>", text);
    }

    [Fact]
    public void NetworkPathHidesServerAndShare()
    {
        string text = Redactor.Redact(@"open \\nas01\family\pics\a.jpg failed")!;
        Assert.DoesNotContain("nas01", text);
        Assert.DoesNotContain("family", text);
        Assert.Contains(CrashReportRedactor.ServerToken, text);
    }

    [Fact]
    public void WindowsAndProgramFilesStayWhole()
    {
        string path = @"C:\Windows\System32\kernel32.dll";
        Assert.Equal(path, Redactor.Redact(path));
    }

    [Fact]
    public void UserNameAndMachineNameAreReplacedAsWords()
    {
        string text = Redactor.Redact("Host JDOE-LAPTOP, user jdoe, jdoes stay")!;
        Assert.Equal("Host <machine>, user <user>, jdoes stay", text);
    }

    [Fact]
    public void AppFolderBecomesAppToken()
    {
        string text = Redactor.Redact(@"at C:\Users\jdoe\AppData\Local\Packages\App\LocalState\settings.json")!;
        Assert.StartsWith("at <app>", text);
        Assert.DoesNotContain("jdoe", text);
    }

    [Fact]
    public void RedactingTwiceChangesNothing()
    {
        string once = Redactor.Redact(@"x C:\Users\jdoe\Documents\a b\c.txt y \\srv\share\d.txt jdoe")!;
        Assert.Equal(once, Redactor.Redact(once));
    }

    [Fact]
    public void NullAndEmptyPassThrough()
    {
        Assert.Null(Redactor.Redact(null));
        Assert.Equal(string.Empty, Redactor.Redact(string.Empty));
    }

    [Theory]
    [InlineData("1", null, false, true)]
    [InlineData(null, "1", false, true)]
    [InlineData(null, null, true, true)]
    [InlineData(null, null, false, false)]
    [InlineData(null, "0", false, false)]
    public void EnvironmentBlocksReporting(string? harness, string? noReports, bool fake, bool blocked)
    {
        string? Get(string name) => name == CrashReportPolicy.HarnessVariable ? harness : name == CrashReportPolicy.BlockVariable ? noReports : null;
        Assert.Equal(blocked, CrashReportPolicy.IsBlocked(Get, fake));
    }

    [Fact]
    public void NothingIsSentWithoutConsentDsnOrWithABlock()
    {
        const string dsn = "https://key@example.invalid/1";
        Assert.False(CrashReportPolicy.MayReport(dsn, userOptedIn: false, blocked: false));
        Assert.False(CrashReportPolicy.MayReport(dsn, userOptedIn: true, blocked: true));
        Assert.False(CrashReportPolicy.MayReport("", userOptedIn: true, blocked: false));
        Assert.False(CrashReportPolicy.MayReport(null, userOptedIn: true, blocked: false));
        Assert.True(CrashReportPolicy.MayReport(dsn, userOptedIn: true, blocked: false));
    }

    [Fact]
    public void CrashReportsAreOnByDefault() => Assert.True(new BatterySettings().SendCrashReports);

    [Fact]
    public void FileWithoutAChoiceReadsAsOn() =>
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<BatterySettings>("""{"Limit":80}""")!.SendCrashReports);

    [Fact]
    public void ExplicitOffSurvivesSaveAndLoad()
    {
        string folder = Path.Combine(Path.GetTempPath(), "bcp-crash-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BatterySettingsStore(folder);
            store.Update(s => s.SendCrashReports = false);
            Assert.False(new BatterySettingsStore(folder).Load().SendCrashReports);
            store.Update(s => s.Limit = 70);
            Assert.False(store.Load().SendCrashReports);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("""{"SendCrashReports":false}""", false)]
    [InlineData("""{"SendCrashReports":true}""", true)]
    [InlineData("""{"SendCrashReports":"yes"}""", false)]
    [InlineData("""{"SendCrashReports":null}""", false)]
    [InlineData("""{"SendCrashReports":1}""", false)]
    public void StoredValueNeverOverridesAnOff(string json, bool expected) =>
        Assert.Equal(expected, System.Text.Json.JsonSerializer.Deserialize<BatterySettings>(json)!.SendCrashReports);

    [Fact]
    public void FakeBatteryModeStillBlocksWithDefaultConsent() =>
        Assert.False(CrashReportPolicy.MayReport("https://key@example.invalid/1", new BatterySettings().SendCrashReports,
            CrashReportPolicy.IsBlocked(static _ => null, fakeBattery: true)));

    [Fact]
    public void BareAndQuotedFileNamesAreHidden()
    {
        string text = Redactor.Redact("Could not find file 'Tax 2025 return.pdf' or holiday.JPG, then Photo_01.png.")!;
        Assert.DoesNotContain("Tax", text);
        Assert.DoesNotContain("holiday", text);
        Assert.DoesNotContain("Photo", text);
        Assert.Contains("'<file>.pdf'", text);
        Assert.Contains("<file>.JPG", text);
    }

    [Fact]
    public void TypeNamesAndRedactedPlaceholdersStay()
    {
        const string text = "System.IO.IOException at <folder>\\<file>.pdf";
        Assert.Equal(text, Redactor.Redact(text));
    }

    [Theory]
    [InlineData("Store", "store")]
    [InlineData("beta", "beta")]
    [InlineData("dev", "dev")]
    [InlineData("", "dev")]
    [InlineData("production", "dev")]
    public void ChannelIsNormalized(string input, string expected) => Assert.Equal(expected, CrashReportPolicy.NormalizeChannel(input));
}
