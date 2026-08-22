<#
  xicontrol-probe-modes.ps1 - round 2 probe: can this laptop switch performance modes?

  WHY: on Xiaomi Book Pro 14 (TM2424) the firmware answers with status 0x80 in OUT[1] and
  puts the value in OUT[4]. Your laptop answers differently (OUT[1] = the command itself),
  which is why XiControl reports "didn't work" for every firmware command - it checks for
  0x80 and never sees it. Your dump also suggests the current mode came back in OUT[2].
  This script verifies that: it switches modes and checks whether the firmware follows.

  WHAT IT DOES:
    1. Prints the FULL 32-byte answer for a few read-only requests.
    2. Reads the current performance mode, tries setting other modes, reads back after each,
       and finally RESTORES the mode it found at the start.

  IS IT SAFE? It only touches the performance mode - the same setting the vendor's own
  utility flips, fully reversible, and restored at the end. It does NOT touch charging,
  the battery, the BIOS or anything persistent. Fans may briefly spin up or down while it
  runs - that is the point of the test.

  RUN IT IN POWERSHELL AS ADMINISTRATOR:
      powershell -ExecutionPolicy Bypass -File .\xicontrol-probe-modes.ps1

  A report is saved next to the script - please attach it to the GitHub issue.
#>

param([string]$Out)

$ErrorActionPreference = 'Continue'
if (-not $Out) {
    $Out = Join-Path $PSScriptRoot ("xicontrol-probe-modes-{0}.txt" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

$lines = New-Object System.Collections.Generic.List[string]
function Say { param([string]$Text = '', [string]$Color = 'Gray')
    Write-Host $Text -ForegroundColor $Color
    $lines.Add($Text)
}

$admin = (New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
Say "XiControl mode probe - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')   elevated: $admin" 'Yellow'
if (-not $admin) { Say 'Run this as administrator, otherwise the firmware will not answer.' 'Red'; return }

try {
    $inst = @(Get-CimInstance -Namespace root/wmi -ClassName MiCommonInterface -ErrorAction Stop)[0]
} catch {
    Say ("MiCommonInterface not available: {0}" -f $_.Exception.Message) 'Red'
    $lines | Set-Content -Path $Out -Encoding UTF8
    return
}

# IN[1] = 0xFA get / 0xFB set, IN[3] = command, IN[4] = argument, IN[6] = value
function Mifs {
    param([byte]$Op, [byte]$Cmd, [byte]$Arg = 0, [byte]$Val = 0)
    $in = [byte[]]::new(32)
    $in[1] = $Op; $in[3] = $Cmd; $in[4] = $Arg; $in[6] = $Val
    (Invoke-CimMethod -InputObject $inst -MethodName MiInterface -Arguments @{ InData = [byte[]]$in }).OutData
}
function Dump { param([byte[]]$Buf) ($Buf | ForEach-Object { '{0:X2}' -f $_ }) -join ' ' }

$modeNames = @{ 0x01 = 'Balance'; 0x02 = 'Quiet'; 0x03 = 'Turbo'; 0x04 = 'Full-speed'; 0x09 = 'Auto'; 0x0A = 'Eco' }
function ModeName { param([int]$V) if ($modeNames.ContainsKey($V)) { $modeNames[$V] } else { 'unknown' } }

# ---------------------------------------------------------- full dumps ----
Say ''
Say '=== FULL 32-BYTE ANSWERS (read-only) ===' 'Cyan'
foreach ($p in @(
    @{ Cmd = 0x08; Arg = 0x00; Label = 'performance mode' },
    @{ Cmd = 0x10; Arg = 0x02; Label = 'charge threshold' },
    @{ Cmd = 0x10; Arg = 0x06; Label = 'adapter watts' },
    @{ Cmd = 0x10; Arg = 0x01; Label = 'battery health' })) {
    $o = Mifs 0xFA ([byte]$p.Cmd) ([byte]$p.Arg)
    Say ("{0,-18} cmd=0x{1:X2}/{2:X2}" -f $p.Label, $p.Cmd, $p.Arg)
    Say ("                   OUT: {0}" -f (Dump $o))
}

# --------------------------------------------------------- mode switch ----
Say ''
Say '=== PERFORMANCE MODE ROUND-TRIP ===' 'Cyan'
$before = Mifs 0xFA 0x08
$origAt2 = [int]$before[2]
$origAt4 = [int]$before[4]
Say ("start: OUT[2]=0x{0:X2} ({1})   OUT[4]=0x{2:X2} ({3})" -f $origAt2, (ModeName $origAt2), $origAt4, (ModeName $origAt4))
Say 'Trying to switch modes - fans may react. Each step: set, wait, read back.' 'Yellow'
Say ''

foreach ($m in 0x02, 0x03, 0x09, 0x01) {
    if ($m -eq $origAt2) { continue }   # already there, nothing to learn
    $setOut = Mifs 0xFB 0x08 ([byte]$m)
    Start-Sleep -Milliseconds 700
    $rb = Mifs 0xFA 0x08
    $ok = if ([int]$rb[2] -eq $m -or [int]$rb[4] -eq $m) { 'ACCEPTED' } else { 'no change' }
    Say ("  SET 0x{0:X2} ({1,-10}) -> {2}" -f $m, (ModeName $m), $ok)
    Say ("      set answer : {0}" -f (Dump $setOut))
    Say ("      read back  : {0}" -f (Dump $rb))
}

# ------------------------------------------------------------- restore ----
Say ''
$restore = if ($origAt2 -ne 0) { $origAt2 } else { $origAt4 }
if ($restore -ne 0) {
    Say ("Restoring the mode found at start: 0x{0:X2} ({1})" -f $restore, (ModeName $restore)) 'Green'
    [void](Mifs 0xFB 0x08 ([byte]$restore))
    Start-Sleep -Milliseconds 700
    Say ("  now: {0}" -f (Dump (Mifs 0xFA 0x08)))
} else {
    Say 'Nothing to restore - the firmware never reported a mode.' 'Yellow'
}

Say ''
Say "Report saved to: $Out" 'Green'
Say 'Please attach it to the issue. If you noticed the fans change during the test, mention that too.' 'Green'
$lines | Set-Content -Path $Out -Encoding UTF8
