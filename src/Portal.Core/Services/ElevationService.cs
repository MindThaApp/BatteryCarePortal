using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Portal.Core.Battery;

namespace Portal.Core.Services;

public enum HelperOutcome
{
    Exited,
    /// <summary>The administrator prompt was declined. Nothing ran.</summary>
    Cancelled,
    /// <summary>The helper process could not be started. Nothing ran.</summary>
    NotStarted,
    /// <summary>The caller stopped waiting and the helper had still not finished after a grace period.</summary>
    StillRunning,
}

public sealed record HelperRun(HelperOutcome Outcome, int ExitCode = 0);

/// <summary>Runs the battery write helper in one elevated process (one administrator prompt per plan).</summary>
public sealed class ElevationService
{
    /// <summary>How long to keep waiting for a helper that is already running after the caller cancels.</summary>
    public static readonly TimeSpan HelperExitGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResultReadGrace = TimeSpan.FromSeconds(2);
    private const int MaxResultBytes = 64 * 1024;

    private readonly Func<string, string, CancellationToken, Task<HelperRun>> _runElevated;

    public ElevationService() => _runElevated = RunElevatedAsync;

    /// <summary>For tests: <paramref name="runElevated"/> gets the pipe name and the PowerShell arguments instead of a real elevated launch.</summary>
    public ElevationService(Func<string, string, CancellationToken, Task<HelperRun>> runElevated) => _runElevated = runElevated;

    /// <summary>
    /// Elevated battery helper: runs every check and write step of one charge-limit plan in a single administrator
    /// process. The plan travels as Base64 JSON inside an encoded Windows PowerShell command. The result comes back over
    /// a named pipe this process creates (one instance, so nobody else can serve that name; the helper connects at
    /// identification level only, so the pipe owner cannot act as the administrator). The elevated side therefore writes
    /// nothing into the user's folders, and the process exit code stays the authority on success.
    /// </summary>
    public async Task<BatteryWritePlanResult> RunBatteryWritePlanAsync(BatteryWritePlan plan, CancellationToken cancellationToken = default)
    {
        string pipeName = $"BatteryCarePortal-{Guid.NewGuid():N}";
        string encoded = BatteryWritePlanScript.EncodeForCommandLine(BatteryWritePlanScript.Build(plan, pipeName));
        string arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}";
        if (arguments.Length > 30000)
            return new BatteryWritePlanResult { Ok = false, WritesAttempted = false, Message = "The change is too large for one administrator call." };

        NamedPipeServerStream server;
        try
        {
            server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new BatteryWritePlanResult { Ok = false, WritesAttempted = false, Message = "The administrator helper could not be prepared." };
        }

        using (server)
        using (var readCts = new CancellationTokenSource())
        {
            Task<string?> reading = ReadResultAsync(server, readCts.Token);
            HelperRun run = await _runElevated(pipeName, arguments, cancellationToken).ConfigureAwait(false);

            if (run.Outcome != HelperOutcome.Exited)
            {
                readCts.Cancel();
                await reading.ConfigureAwait(false);
                return run.Outcome switch
                {
                    HelperOutcome.Cancelled => new BatteryWritePlanResult
                    {
                        Ok = false, Cancelled = true, WritesAttempted = false, Message = "The administrator prompt was cancelled.",
                    },
                    HelperOutcome.NotStarted => new BatteryWritePlanResult
                    {
                        Ok = false, WritesAttempted = false, Message = "The administrator helper could not start.",
                    },
                    _ => new BatteryWritePlanResult
                    {
                        Ok = false, Message = "The administrator helper was still running when the app stopped waiting, so the change may or may not have been made.",
                    },
                };
            }

            readCts.CancelAfter(ResultReadGrace);
            string? json = await reading.ConfigureAwait(false);
            return Interpret(json, run.ExitCode);
        }
    }

    /// <summary>The exit code decides success and whether a write can have started; the pipe adds the details.</summary>
    private static BatteryWritePlanResult Interpret(string? json, int exitCode)
    {
        bool noWriteStarted = exitCode is BatteryWritePlanScript.ExitFailedBeforeWrite or BatteryWritePlanScript.ExitCheckFailed;
        BatteryWritePlanResult? reported = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(json))
                reported = System.Text.Json.JsonSerializer.Deserialize<BatteryWritePlanResult>(json.TrimStart('﻿'));
        }
        catch (System.Text.Json.JsonException) { }

        if (reported is null)
        {
            return new BatteryWritePlanResult
            {
                Ok = false,
                WritesAttempted = !noWriteStarted,
                Message = $"The administrator helper ended with code {exitCode}.",
            };
        }
        return reported with
        {
            Ok = reported.Ok && exitCode == 0,
            Cancelled = false,
            WritesAttempted = reported.WritesAttempted || !noWriteStarted,
        };
    }

    private static async Task<string?> ReadResultAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        try
        {
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[4096];
            int read;
            while ((read = await server.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxResultBytes) return null;
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static async Task<HelperRun> RunElevatedAsync(string pipeName, string arguments, CancellationToken cancellationToken)
    {
        string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = arguments,
                // A fixed working directory: nothing the elevated process might look up there is user-writable.
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new HelperRun(HelperOutcome.Cancelled);
        }
        catch (Win32Exception)
        {
            return new HelperRun(HelperOutcome.NotStarted);
        }
        if (process is null) return new HelperRun(HelperOutcome.NotStarted);

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The helper may be mid-write: keep waiting a bounded time rather than abandoning it.
                using var grace = new CancellationTokenSource(HelperExitGrace);
                try { await process.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return new HelperRun(HelperOutcome.StillRunning); }
            }
            return new HelperRun(HelperOutcome.Exited, process.ExitCode);
        }
    }
}
