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

/// <summary>Builds the Windows PowerShell script the elevated helper runs. The plan is embedded as Base64 JSON, never as code.</summary>
public static class BatteryWritePlanScript
{
    public static string Build(BatteryWritePlan plan, string resultPath)
    {
        string planB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan)));
        string resultB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(resultPath));
        return Template.Replace("{PLAN}", planB64, StringComparison.Ordinal)
                       .Replace("{RESULT}", resultB64, StringComparison.Ordinal);
    }

    public static string EncodeForCommandLine(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    private const string Template = """
$ErrorActionPreference = 'Stop'
$plan = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{PLAN}')) | ConvertFrom-Json
$resultPath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{RESULT}'))
$script:readBack = $null
function Save([bool]$ok, [int]$step, [string]$msg) {
  [pscustomobject]@{ Ok = $ok; FailedStep = $step; Message = $msg; ReadBackRaw = $script:readBack } |
    ConvertTo-Json -Compress | Set-Content -LiteralPath $resultPath -Encoding UTF8
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
$n = -1
try {
  foreach ($c in @($plan.Checks)) {
    $rows = @(Get-CimInstance -Namespace $c.Namespace -Query $c.Query -ErrorAction SilentlyContinue)
    if ($rows.Count -eq 0) {
      if ($c.MissingIsFailure) { Save $false -1 $c.FailMessage; exit 3 }
      continue
    }
    foreach ($r in $rows) {
      if ([string]$r.($c.Property) -ne [string]$c.ExpectedValue) { Save $false -1 $c.FailMessage; exit 3 }
    }
  }
  $steps = @($plan.Steps)
  for ($n = 0; $n -lt $steps.Count; $n++) {
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
          if ($null -eq $inst) { Save $false $n "$($s.ClassName) was not found."; exit (10 + $n) }
          $r = Invoke-CimMethod -InputObject $inst -MethodName $s.MethodName -Arguments $a
        } else {
          $r = Invoke-CimMethod -Namespace $s.Namespace -ClassName $s.ClassName -MethodName $s.MethodName -Arguments $a
        }
        if ($s.SuccessProperty) {
          $v = [string]$r.($s.SuccessProperty)
          if (@($s.SuccessValues) -notcontains $v) { Save $false $n "$($s.Label) returned '$v'."; exit (10 + $n) }
        }
      }
      'DeviceIoControl' {
        Use-Ioctl
        [void][PbcBatteryIoctl]::Send([string]$s.DevicePath, [uint32]$s.IoControlCode, [uint32]$s.InputValue)
      }
      'VerifyDeviceIoControlBit' {
        Use-Ioctl
        if ($s.DelayMs -gt 0) { Start-Sleep -Milliseconds ([int]$s.DelayMs) }
        $out = [PbcBatteryIoctl]::Send([string]$s.DevicePath, [uint32]$s.IoControlCode, [uint32]$s.InputValue)
        $script:readBack = ('0x{0:X8}' -f $out)
        $bit = (($out -shr [int]$s.Bit) -band 1) -eq 1
        if ($bit -ne [bool]$s.ExpectedBitValue) { Save $false $n "$($s.Label): read-back $($script:readBack) does not show the new state."; exit (10 + $n) }
      }
      'VerifyWmiProperty' {
        if ($s.DelayMs -gt 0) { Start-Sleep -Milliseconds ([int]$s.DelayMs) }
        $row = @(Get-CimInstance -Namespace $s.Namespace -Query $s.Query) | Select-Object -First 1
        $v = if ($null -eq $row) { $null } else { [string]$row.($s.Property) }
        $script:readBack = $v
        if ($v -ne [string]$s.ExpectedValue) { Save $false $n "$($s.Label): read-back '$v' does not show the new state."; exit (10 + $n) }
      }
      default { Save $false $n "Unknown step kind."; exit (10 + $n) }
    }
  }
  Save $true -1 'OK'
  exit 0
} catch {
  Save $false $n $_.Exception.Message
  exit 1
}
""";
}
