using System.Text;
using System.Text.Json;

namespace Portal.Core.Battery;

/// <summary>
/// Everything one "Apply" writes, built by a backend from validated values and run in a single elevated
/// helper process. Checks run first and abort before any write; steps stop at the first failure and are
/// never retried.
/// </summary>
public sealed record BatteryWritePlan
{
    public string BackendId { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<BatteryWriteCheck> Checks { get; init; } = new();
    public List<BatteryWriteStep> Steps { get; init; } = new();

    /// <summary>True when the plan writes something and every write step is followed by a read-back step before the next write.</summary>
    public bool EveryWriteIsVerified()
    {
        bool anyWrite = false, unverified = false;
        foreach (BatteryWriteStep step in Steps)
        {
            if (step.Kind is BatteryWriteStepKind.WmiMethod or BatteryWriteStepKind.DeviceIoControl)
            {
                if (unverified) return false;
                anyWrite = unverified = true;
            }
            else if (step.Kind is BatteryWriteStepKind.VerifyDeviceIoControlBit or BatteryWriteStepKind.VerifyWmiProperty)
            {
                unverified = false;
            }
            else
            {
                return false;
            }
        }
        return anyWrite && !unverified;
    }
}

/// <summary>A read-only precondition, for example "no BIOS administrator password is set".</summary>
public sealed record BatteryWriteCheck
{
    public string Namespace { get; init; } = string.Empty;
    public string Query { get; init; } = string.Empty;
    public string Property { get; init; } = string.Empty;
    public string ExpectedValue { get; init; } = string.Empty;
    public bool MissingIsFailure { get; init; }
    public string FailMessage { get; init; } = string.Empty;
}

public enum BatteryWriteStepKind
{
    /// <summary>Invoke a WMI method (on the class, or on its first instance) and check a result property.</summary>
    WmiMethod,
    /// <summary>Send a 32-bit input to a device with DeviceIoControl.</summary>
    DeviceIoControl,
    /// <summary>Send a 32-bit query to a device and require one output bit to have the expected value.</summary>
    VerifyDeviceIoControlBit,
    /// <summary>Run a WQL query and require a property to have the expected value.</summary>
    VerifyWmiProperty,
}

public sealed record WmiArgument(string Name, string Type, string Value)
{
    public static WmiArgument String(string name, string value) => new(name, "String", value);
    public static WmiArgument UInt32(string name, uint value) => new(name, "UInt32", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static WmiArgument EmptyBytes(string name) => new(name, "UInt8Array", string.Empty);
}

public sealed record BatteryWriteStep
{
    public BatteryWriteStepKind Kind { get; init; }
    public string Label { get; init; } = string.Empty;

    public string Namespace { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string MethodName { get; init; } = string.Empty;
    public bool OnInstance { get; init; }
    public List<WmiArgument> Arguments { get; init; } = new();
    public string? SuccessProperty { get; init; }
    public List<string> SuccessValues { get; init; } = new();
    public string Query { get; init; } = string.Empty;
    public string Property { get; init; } = string.Empty;
    public string ExpectedValue { get; init; } = string.Empty;

    public string DevicePath { get; init; } = string.Empty;
    public uint IoControlCode { get; init; }
    public uint InputValue { get; init; }
    public int Bit { get; init; }
    public bool ExpectedBitValue { get; init; }
    public int DelayMs { get; init; }
}

/// <summary>Outcome reported by the elevated helper.</summary>
public sealed record BatteryWritePlanResult
{
    public bool Ok { get; init; }
    public int FailedStep { get; init; } = -1;
    public string? Message { get; init; }
    public string? ReadBackRaw { get; init; }
    public bool Cancelled { get; init; }
    /// <summary>False only when the helper is known to have stopped before its first write step (checks failed, never started).</summary>
    public bool WritesAttempted { get; init; } = true;
}

public interface IBatteryWriteExecutor
{
    Task<BatteryWritePlanResult> ExecuteAsync(BatteryWritePlan plan, CancellationToken cancellationToken = default);
}

/// <summary>Runs plans through the elevated helper verb (one administrator prompt per plan).</summary>
public sealed class ElevatedBatteryWriteExecutor : IBatteryWriteExecutor
{
    private readonly Services.ElevationService _elevation;

    public ElevatedBatteryWriteExecutor(Services.ElevationService? elevation = null) =>
        _elevation = elevation ?? new Services.ElevationService();

    public Task<BatteryWritePlanResult> ExecuteAsync(BatteryWritePlan plan, CancellationToken cancellationToken = default) =>
        _elevation.RunBatteryWritePlanAsync(plan, cancellationToken);
}

/// <summary>
/// Builds the Windows PowerShell script the elevated helper runs. The plan and the result pipe name are embedded as
/// Base64 data, never as code. The helper compiles its DeviceIoControl shim only inside a fresh folder under
/// ProgramData that only SYSTEM and Administrators can write, and reports its result over the app's own named pipe,
/// so the elevated process never loads or writes anything in a folder the user can change.
/// </summary>
public static class BatteryWritePlanScript
{
    /// <summary>Exit codes: 0 done; 1 failed after a write step started; 2 failed before any write; 3 a check refused; 10 + n step n failed.</summary>
    public const int ExitFailedBeforeWrite = 2;
    public const int ExitCheckFailed = 3;

    public static string Build(BatteryWritePlan plan, string pipeName)
    {
        string planB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan)));
        string pipeB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(pipeName));
        return Template.Replace("{PLAN}", planB64, StringComparison.Ordinal)
                       .Replace("{PIPE}", pipeB64, StringComparison.Ordinal);
    }

    public static string EncodeForCommandLine(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    private const string Template = """
$ErrorActionPreference = 'Stop'
$plan = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{PLAN}')) | ConvertFrom-Json
$pipeName = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{PIPE}'))
$script:readBack = $null
$script:wrote = $false
$script:n = -1
$script:work = $null
function Save([bool]$ok, [int]$step, [string]$msg) {
  try {
    $json = [pscustomobject]@{ Ok = $ok; FailedStep = $step; Message = $msg; ReadBackRaw = $script:readBack; WritesAttempted = $script:wrote } | ConvertTo-Json -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::Out, [System.IO.Pipes.PipeOptions]::None, [System.Security.Principal.TokenImpersonationLevel]::Identification)
    try { $pipe.Connect(5000); $pipe.Write($bytes, 0, $bytes.Length); $pipe.Flush() } finally { $pipe.Dispose() }
  } catch { }
}
function New-AdminOnlyAcl {
  $acl = New-Object System.Security.AccessControl.DirectorySecurity
  $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
  $acl.SetOwner($admins)
  $acl.SetAccessRuleProtection($true, $false)
  foreach ($sid in @((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')), $admins)) {
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid, [System.Security.AccessControl.FileSystemRights]::FullControl, [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit', [System.Security.AccessControl.PropagationFlags]::None, [System.Security.AccessControl.AccessControlType]::Allow)))
  }
  return $acl
}
function Assert-AdminFolder([string]$path) {
  $item = Get-Item -LiteralPath $path -Force
  if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "The helper folder is not a plain folder." }
  $owner = $item.GetAccessControl().GetOwner([System.Security.Principal.SecurityIdentifier]).Value
  if ($owner -ne 'S-1-5-18' -and $owner -ne 'S-1-5-32-544') { throw "The helper folder is not owned by the administrators." }
}
function Use-Ioctl {
  if (-not ('PbcBatteryIoctl' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class PbcBatteryIoctl {
  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);
  [DllImport("kernel32.dll", SetLastError = true)]
  static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref uint input, uint inputSize, out uint output, uint outputSize, out uint returned, IntPtr overlapped);
  public static uint Send(string path, uint code, uint input) {
    using (SafeFileHandle h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
      if (h.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
      uint output; uint returned;
      if (!DeviceIoControl(h, code, ref input, 4, out output, 4, out returned, IntPtr.Zero))
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
      return output;
    }
  }
}
'@
  }
}
function Invoke-Plan {
  foreach ($c in @($plan.Checks)) {
    $rows = @(Get-CimInstance -Namespace $c.Namespace -Query $c.Query -ErrorAction SilentlyContinue)
    if ($rows.Count -eq 0) {
      if ($c.MissingIsFailure) { Save $false -1 $c.FailMessage; return 3 }
      continue
    }
    foreach ($r in $rows) {
      if ([string]$r.($c.Property) -ne [string]$c.ExpectedValue) { Save $false -1 $c.FailMessage; return 3 }
    }
  }
  $steps = @($plan.Steps)
  for ($script:n = 0; $script:n -lt $steps.Count; $script:n++) {
    $n = $script:n
    $s = $steps[$n]
    switch ([string]$s.Kind) {
      'WmiMethod' {
        $a = @{}
        foreach ($arg in @($s.Arguments)) {
          switch ([string]$arg.Type) {
            'UInt32' { $a[$arg.Name] = [uint32]$arg.Value }
            'UInt8Array' { $a[$arg.Name] = [byte[]]@() }
            default { $a[$arg.Name] = [string]$arg.Value }
          }
        }
        if ($s.OnInstance) {
          $inst = @(Get-CimInstance -Namespace $s.Namespace -ClassName $s.ClassName) | Select-Object -First 1
          if ($null -eq $inst) { Save $false $n "$($s.ClassName) was not found."; return (10 + $n) }
          $script:wrote = $true
          $r = Invoke-CimMethod -InputObject $inst -MethodName $s.MethodName -Arguments $a
        } else {
          $script:wrote = $true
          $r = Invoke-CimMethod -Namespace $s.Namespace -ClassName $s.ClassName -MethodName $s.MethodName -Arguments $a
        }
        if ($s.SuccessProperty) {
          $v = [string]$r.($s.SuccessProperty)
          if (@($s.SuccessValues) -notcontains $v) { Save $false $n "$($s.Label) returned '$v'."; return (10 + $n) }
        }
      }
      'DeviceIoControl' {
        Use-Ioctl
        $script:wrote = $true
        [void][PbcBatteryIoctl]::Send([string]$s.DevicePath, [uint32]$s.IoControlCode, [uint32]$s.InputValue)
      }
      'VerifyDeviceIoControlBit' {
        Use-Ioctl
        if ($s.DelayMs -gt 0) { Start-Sleep -Milliseconds ([int]$s.DelayMs) }
        $out = [PbcBatteryIoctl]::Send([string]$s.DevicePath, [uint32]$s.IoControlCode, [uint32]$s.InputValue)
        $script:readBack = ('0x{0:X8}' -f $out)
        $bit = (($out -shr [int]$s.Bit) -band 1) -eq 1
        if ($bit -ne [bool]$s.ExpectedBitValue) { Save $false $n "$($s.Label): read-back $($script:readBack) does not show the new state."; return (10 + $n) }
      }
      'VerifyWmiProperty' {
        if ($s.DelayMs -gt 0) { Start-Sleep -Milliseconds ([int]$s.DelayMs) }
        $row = @(Get-CimInstance -Namespace $s.Namespace -Query $s.Query) | Select-Object -First 1
        $v = if ($null -eq $row) { $null } else { [string]$row.($s.Property) }
        $script:readBack = $v
        if ($v -ne [string]$s.ExpectedValue) { Save $false $n "$($s.Label): read-back '$v' does not show the new state."; return (10 + $n) }
      }
      default { Save $false $n "Unknown step kind."; return (10 + $n) }
    }
  }
  Save $true -1 'OK'
  return 0
}
$code = 2
try {
  # Add-Type compiles through the TEMP folder and the elevated process loads the result, so TEMP must be a folder
  # only administrators can write: a fresh one under ProgramData, never the user's own TEMP.
  $root = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'BatteryCarePortal'
  if (-not (Test-Path -LiteralPath $root)) { [void][System.IO.Directory]::CreateDirectory($root, (New-AdminOnlyAcl)) }
  Assert-AdminFolder $root
  [System.IO.Directory]::SetAccessControl($root, (New-AdminOnlyAcl))
  $cutoff = (Get-Date).ToUniversalTime().AddHours(-1)
  foreach ($old in @(Get-ChildItem -LiteralPath $root -Directory -Force)) {
    if ($old.Name -match '^helper-[0-9a-f]{32}$' -and -not ($old.Attributes -band [IO.FileAttributes]::ReparsePoint) -and $old.CreationTimeUtc -lt $cutoff) {
      Remove-Item -LiteralPath $old.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
  }
  $work = Join-Path $root ('helper-' + [guid]::NewGuid().ToString('N'))
  if (Test-Path -LiteralPath $work) { throw "The helper folder already exists." }
  [void][System.IO.Directory]::CreateDirectory($work, (New-AdminOnlyAcl))
  $script:work = $work
  Assert-AdminFolder $work
  $env:TEMP = $work
  $env:TMP = $work
  $code = [int](@(Invoke-Plan) | Select-Object -Last 1)
} catch {
  Save $false $script:n $_.Exception.Message
  $code = if ($script:wrote) { 1 } else { 2 }
} finally {
  # Only the one folder this run created.
  if ($script:work) { Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue }
}
exit $code
""";
}

