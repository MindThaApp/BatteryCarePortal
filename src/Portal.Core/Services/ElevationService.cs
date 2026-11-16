using System.ComponentModel;
using System.Diagnostics;

namespace Portal.Core.Services;

/// <summary>Runs the battery write helper in one elevated process (one administrator prompt per plan).</summary>
public sealed class ElevationService
{
    public bool LastOperationWasCancelled { get; private set; }

    /// <summary>
    /// Elevated battery helper: runs every check and write step of one charge-limit plan in a single
    /// administrator process. The plan travels as Base64 JSON inside an encoded Windows PowerShell command,
    /// so nothing is written to disk for the elevated side to execute.
    /// </summary>
    public async Task<Battery.BatteryWritePlanResult> RunBatteryWritePlanAsync(
        Battery.BatteryWritePlan plan, CancellationToken cancellationToken = default)
    {
        string resultPath = Path.Combine(Path.GetTempPath(), $"BatteryCarePortal-battery-{Guid.NewGuid():N}.json");
        string encoded = Battery.BatteryWritePlanScript.EncodeForCommandLine(Battery.BatteryWritePlanScript.Build(plan, resultPath));
        string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        string arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}";
        if (arguments.Length > 30000)
            return new Battery.BatteryWritePlanResult { Ok = false, Message = "The change is too large for one administrator call." };

        try
        {
            int? exitCode = await RunElevatedForExitCodeAsync(powershell, arguments, hidden: true, cancellationToken);
            if (exitCode is null)
            {
                return new Battery.BatteryWritePlanResult
                {
                    Ok = false,
                    Cancelled = LastOperationWasCancelled,
                    Message = LastOperationWasCancelled ? "The administrator prompt was cancelled." : "The administrator helper could not start.",
                };
            }

            Battery.BatteryWritePlanResult? result = null;
            try
            {
                if (File.Exists(resultPath))
                    result = System.Text.Json.JsonSerializer.Deserialize<Battery.BatteryWritePlanResult>(
                        File.ReadAllText(resultPath).TrimStart('\uFEFF'));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }

            return result is not null
                ? result with { Ok = result.Ok && exitCode == 0 }
                : new Battery.BatteryWritePlanResult { Ok = false, Message = $"The administrator helper ended with code {exitCode}." };
        }
        finally
        {
            // Only the uniquely named result file this call asked the helper to create.
            try { File.Delete(resultPath); } catch { }
        }
    }

    private async Task<int?> RunElevatedForExitCodeAsync(string fileName, string arguments, bool hidden, CancellationToken cancellationToken)
    {
        LastOperationWasCancelled = false;

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                // A fixed working directory: nothing the elevated process might look up there is user-writable.
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            });

            if (process is null) return null;

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            LastOperationWasCancelled = true;
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
