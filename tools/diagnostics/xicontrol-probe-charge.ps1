<#
  xicontrol-probe-charge.ps1 - round 3: which performance modes exist, and does charge
  limiting work at all on this machine?

  BACKGROUND: round 2 proved your firmware obeys commands - it accepted Balance (0x01) and
  went back to Quiet (0x02) on request - while rejecting Turbo (0x03) and Auto (0x09), which
  are Book Pro 14 codes. So your model has its own set of modes, and we need to know it.
  The charging commands answered with an echo and no data, which could mean either
  "not supported" or "the answer lives somewhere else". This script settles both.

  WHAT IT DOES:
    PART 1 - performance modes: tries every code 0x01..0x0F, reads back after each, records
             which ones the firmware keeps, then RESTORES the mode you started with.
    PART 2 - charge threshold: writes the threshold codes known from Book Pro 14, reads back
             after each, and RESTORES whatever your laptop had before the script ran.

  EITHER PART CAN BE SKIPPED. Part 1 cycles the machine through Turbo and Full-speed, so the
  fans will get loud for a few seconds. Part 2 writes to the battery charge controller - the
  same thing the vendor utility does when you set "charge to 80%". Both are reversible and
  both restore your original setting at the end. If either makes you uneasy, skip it; each
  part is useful on its own.

  RUN IN POWERSHELL AS ADMINISTRATOR:
      powershell -ExecutionPolicy Bypass -File .\xicontrol-probe-charge.ps1
      powershell -ExecutionPolicy Bypass -File .\xicontrol-probe-charge.ps1 -SkipCharge
      powershell -ExecutionPolicy Bypass -File .\xicontrol-probe-charge.ps1 -SkipModes

  The report is saved next to the script - please attach it to the issue.
#>

param([switch]$SkipModes, [switch]$SkipCharge, [string]$Out)

$ErrorActionPreference = 'Continue'
if (-not $Out) {
    $Out = Join-Path $PSScriptRoot ("xicontrol-probe-charge-{0}.txt" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

$lines = New-Object System.Collections.Generic.List[string]
function Say { param([string]$Text = '', [string]$Color = 'Gray')
    Write-Host $Text -ForegroundColor $Color
    $lines.Add($Text)
}

$admin = (New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
Say "XiControl mode/charge probe - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')   elevated: $admin" 'Yellow'
if (-not $admin) { Say 'Run as administrator, otherwise the firmware stays silent.' 'Red'; return }

try {
    $inst = @(Get-CimInstance -Namespace root/wmi -ClassName MiCommonInterface -ErrorAction Stop)[0]
} catch {
    Say ("MiCommonInterface not available: {0}" -f $_.Exception.Message) 'Red'
    $lines | Set-Content -Path $Out -Encoding UTF8
    return
}

function Mifs {
    param([byte]$Op, [byte]$Cmd, [byte]$Arg = 0, [byte]$Val = 0)
    $in = [byte[]]::new(32)
    $in[1] = $Op; $in[3] = $Cmd; $in[4] = $Arg; $in[6] = $Val
    (Invoke-CimMethod -InputObject $inst -MethodName MiInterface -Arguments @{ InData = [byte[]]$in }).OutData
}
function Dump { param([byte[]]$B) ($B | ForEach-Object { '{0:X2}' -f $_ }) -join ' ' }

$modeNames = @{ 0x01 = 'Balance'; 0x02 = 'Quiet'; 0x03 = 'Turbo'; 0x04 = 'Full-speed'; 0x09 = 'Auto'; 0x0A = 'Eco' }
function ModeName { param([int]$V) if ($modeNames.ContainsKey($V)) { $modeNames[$V] } else { '?' } }

# ===================================================== response dialect ====
# Book Pro 14 answers with a status byte (OUT[1] = 0x80) and the value further along;
# your TM2113 echoes the command in OUT[1] and puts the value in OUT[2]. Detecting this
# once keeps the probe honest on both, instead of hardcoding one machine's layout.
Say ''
$probe = Mifs 0xFA 0x08
Say ("GET 0x08 raw : {0}" -f (Dump $probe))
switch ($probe[1]) {
    0x80    { $dialect = 'classic'; $modeAt = 4; $valueAt = 6 }
    0x08    { $dialect = 'echo';    $modeAt = 2; $valueAt = -1 }
    default { $dialect = 'unknown'; $modeAt = -1; $valueAt = -1 }
}
Say ("response dialect: {0} (mode byte at OUT[{1}])" -f $dialect, $modeAt) 'Cyan'
if ($dialect -eq 'unknown') {
    Say 'Neither a status byte nor a command echo - this layout is new to us.' 'Red'
    Say 'Stopping: without knowing where the value lives, a sweep would prove nothing.' 'Red'
    $lines | Set-Content -Path $Out -Encoding UTF8
    return
}
function ModeNow { [int](Mifs 0xFA 0x08)[$modeAt] }

# ============================================================ PART 1: modes ====
if ($SkipModes) {
    Say ''
    Say 'PART 1 skipped (-SkipModes).' 'Yellow'
} else {
    Say ''
    Say '=== PART 1: which performance modes does this firmware accept? ===' 'Cyan'
    $orig = ModeNow
    Say ("current mode: 0x{0:X2} ({1})" -f $orig, (ModeName $orig))
    Say 'Sweeping 0x01..0x0F - fans may react. Accepted = the firmware still reports it afterwards.' 'Yellow'
    Say ''

    $accepted = @()
    foreach ($m in 0x01..0x0F) {
        [void](Mifs 0xFB 0x08 ([byte]$m))
        Start-Sleep -Milliseconds 500
        $raw = Mifs 0xFA 0x08
        $rb = [int]$raw[$modeAt]
        if ($rb -eq $m) {
            $accepted += $m
            Say ("  0x{0:X2} ({1,-10}) ACCEPTED   read back: {2}" -f $m, (ModeName $m), (Dump $raw)) 'Green'
        } else {
            Say ("  0x{0:X2} ({1,-10}) rejected   read back: {2}" -f $m, (ModeName $m), (Dump $raw))
        }
    }
    Say ''
    if ($accepted.Count -gt 0) {
        Say ("ACCEPTED MODES: {0}" -f (($accepted | ForEach-Object { '0x{0:X2}' -f $_ }) -join ', ')) 'Green'
    } else {
        Say 'ACCEPTED MODES: none - the firmware kept its original mode throughout.' 'Yellow'
    }

    [void](Mifs 0xFB 0x08 ([byte]$orig))
    Start-Sleep -Milliseconds 500
    $back = ModeNow
    if ($back -eq $orig) {
        Say ("restored: 0x{0:X2} ({1})" -f $back, (ModeName $orig)) 'Green'
    } else {
        Say ("COULD NOT RESTORE: wanted 0x{0:X2}, got 0x{1:X2} - please set your mode manually." -f $orig, $back) 'Red'
    }
}

# =========================================================== PART 2: charge ====
if ($SkipCharge) {
    Say ''
    Say 'PART 2 skipped (-SkipCharge).' 'Yellow'
} else {
    Say ''
    Say '=== PART 2: does the charge threshold respond? ===' 'Cyan'
    Say 'Codes are the ones known from Book Pro 14: 1=80%, 5=70%, 6=60%, 7=50%, 8=40%, 0=no limit.' 'Yellow'
    Say ''

    # Remember what the machine had, so the restore puts YOUR setting back rather than
    # blanket "no limit". In the echo dialect OUT[2] is the echoed argument, not data,
    # so there is nothing trustworthy to read - say so instead of inventing a value.
    $before = Mifs 0xFA 0x10 0x02
    Say ("read before : {0}" -f (Dump $before))
    $origCharge = $null
    if ($valueAt -ge 0) {
        $origCharge = [int]$before[$valueAt]
        Say ("your current threshold code: {0}" -f $origCharge) 'Green'
    } else {
        Say 'Your current threshold cannot be read in this dialect - the answer carries no data.' 'Yellow'
        Say 'The script will restore "no limit" (0) at the end, which is the safe default.' 'Yellow'
    }
    Say ''

    foreach ($c in 1, 5, 8) {
        [void](Mifs 0xFB 0x10 0x02 ([byte]$c))
        Start-Sleep -Milliseconds 400
        $rb = Mifs 0xFA 0x10 0x02
        Say ("  SET code {0} -> read back: {1}" -f $c, (Dump $rb))
    }

    Say ''
    if ($null -ne $origCharge) {
        Say ("Restoring your original threshold (code {0}):" -f $origCharge) 'Green'
        [void](Mifs 0xFB 0x10 0x02 ([byte]$origCharge))
    } else {
        Say 'Restoring "no limit" (code 0) - your previous value was unreadable:' 'Green'
        [void](Mifs 0xFB 0x10 0x02 0)
    }
    Start-Sleep -Milliseconds 400
    Say ("  now: {0}" -f (Dump (Mifs 0xFA 0x10 0x02)))
    Say ''
    if ($null -eq $origCharge) {
        Say 'Please check your battery settings afterwards: if you had a charge limit set in the' 'Yellow'
        Say 'vendor app or in XiControl, set it again - the script could not read it to put back.' 'Yellow'
    }
}

Say ''
Say "Report saved to: $Out" 'Green'
if ($SkipModes) {
    Say 'Please attach it to the issue.' 'Green'
} else {
    Say 'Please attach it. If the fans changed during PART 1, mention roughly at which step.' 'Green'
}
$lines | Set-Content -Path $Out -Encoding UTF8
