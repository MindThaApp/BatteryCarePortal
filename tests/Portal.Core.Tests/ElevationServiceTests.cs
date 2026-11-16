using System.IO.Pipes;
using System.Text;
using Portal.Core.Battery;
using Portal.Core.Services;
using Xunit;

namespace Portal.Core.Tests;

/// <summary>No real elevation: the launcher is replaced by a fake that plays the helper's part on the pipe.</summary>
public class ElevationServiceTests
{
    private static readonly BatteryWritePlan Plan = new() { BackendId = "x" };

    private static async Task SendAsHelper(string pipeName, string json)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
        await client.ConnectAsync(5000);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await client.WriteAsync(bytes);
    }

    [Fact]
    public void Script_compiles_only_in_an_admin_only_folder_and_writes_no_result_file()
    {
        string script = BatteryWritePlanScript.Build(Plan, "BatteryCarePortal-test");
        Assert.Contains("GetFolderPath('CommonApplicationData')", script);
        Assert.Contains("SetAccessRuleProtection($true, $false)", script);
        Assert.Contains("S-1-5-32-544", script);
        Assert.Contains("TokenImpersonationLevel]::Identification", script);
        Assert.DoesNotContain("Set-Content", script);
        Assert.DoesNotContain("Out-File", script);
        Assert.DoesNotContain("GetTempPath", script);

        int temp = script.IndexOf("$env:TEMP = $work", StringComparison.Ordinal);
        int tmp = script.IndexOf("$env:TMP = $work", StringComparison.Ordinal);
        int invoke = script.IndexOf("$code = [int](@(Invoke-Plan)", StringComparison.Ordinal);
        Assert.True(temp > 0 && tmp > 0 && invoke > temp && invoke > tmp);
        Assert.True(script.IndexOf("Assert-AdminFolder $work", StringComparison.Ordinal) < temp);
        Assert.Contains("Remove-Item -LiteralPath $script:work -Recurse", script);
    }

    [Fact]
    public async Task Result_comes_back_over_the_pipe_and_the_exit_code_decides()
    {
        var service = new ElevationService(async (pipe, _, _) =>
        {
            await SendAsHelper(pipe, "{\"Ok\":true,\"FailedStep\":-1,\"Message\":\"OK\",\"WritesAttempted\":true}");
            return new HelperRun(HelperOutcome.Exited, 0);
        });
        BatteryWritePlanResult ok = await service.RunBatteryWritePlanAsync(Plan);
        Assert.True(ok.Ok);
        Assert.Equal("OK", ok.Message);

        var lying = new ElevationService(async (pipe, _, _) =>
        {
            await SendAsHelper(pipe, "{\"Ok\":true,\"WritesAttempted\":false}");
            return new HelperRun(HelperOutcome.Exited, 1);
        });
        BatteryWritePlanResult notOk = await lying.RunBatteryWritePlanAsync(Plan);
        Assert.False(notOk.Ok);
        Assert.True(notOk.WritesAttempted);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(11, true)]
    public async Task Without_a_reported_result_only_the_before_write_exit_codes_count_as_not_attempted(int exitCode, bool attempted)
    {
        var service = new ElevationService((_, _, _) => Task.FromResult(new HelperRun(HelperOutcome.Exited, exitCode)));
        BatteryWritePlanResult result = await service.RunBatteryWritePlanAsync(Plan);
        Assert.False(result.Ok);
        Assert.Equal(attempted, result.WritesAttempted);
    }

    [Fact]
    public async Task Declined_prompt_is_cancelled_and_nothing_ran()
    {
        var service = new ElevationService((_, _, _) => Task.FromResult(new HelperRun(HelperOutcome.Cancelled)));
        BatteryWritePlanResult result = await service.RunBatteryWritePlanAsync(Plan);
        Assert.True(result.Cancelled);
        Assert.False(result.WritesAttempted);
    }

    [Fact]
    public async Task Caller_cancelling_while_the_helper_runs_is_uncertain_not_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var service = new ElevationService((_, _, token) =>
        {
            cts.Cancel();
            Assert.True(token.IsCancellationRequested);
            return Task.FromResult(new HelperRun(HelperOutcome.StillRunning));
        });
        BatteryWritePlanResult result = await service.RunBatteryWritePlanAsync(Plan, cts.Token);
        Assert.False(result.Ok);
        Assert.False(result.Cancelled);
        Assert.True(result.WritesAttempted);
    }
}
