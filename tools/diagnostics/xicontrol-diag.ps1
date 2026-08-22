<#
  xicontrol-diag.ps1 - hardware report for XiControl bug reports.

  WHAT IT DOES (read-only, safe):
    1. Collects laptop model / CPU / BIOS / Windows build.
    2. Checks whether the firmware interface (root\wmi MiCommonInterface) exists
       and what it answers to GET requests - performance mode, charge threshold,
       adapter watts, battery health, microphone.
    3. Checks the Intel DPTF thermal provider (temperatures in the Monitor widget).
    4. Records special-key events (HID_EVENT20) while you press keys, so we learn
       the key codes of YOUR model.

  IT NEVER CHANGES ANYTHING: only GET requests are sent, no SET commands, no
  registry writes, no driver installs. You can read the whole script - it is short.

  HOW TO RUN (PowerShell AS ADMINISTRATOR, in the folder with this file):
      powershell -ExecutionPolicy Bypass -File .\xicontrol-diag.ps1

  The report is printed and also saved next to the script as
  xicontrol-diag-<date>.txt - attach that file to the GitHub issue.

  Optional: -Seconds 90 makes the key-recording window longer (default 60).
#>

param(
    [int]$Seconds = 60,
    [string]$Out
)

$ErrorActionPreference = 'Continue'
if (-not $Out) {
    $Out = Join-Path $PSScriptRoot ("xicontrol-diag-{0}.txt" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

$lines = New-Object System.Collections.Generic.List[string]
function Say { param([string]$Text = '', [string]$Color = 'Gray')
    Write-Host $Text -ForegroundColor $Color
    $lines.Add($Text)
}
function Head { param([string]$Text)
    Say ''
    Say ("=== {0} " -f $Text).PadRight(70, '=') 'Cyan'
}

$admin = (New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

Say "XiControl diagnostics - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" 'Yellow'
Say "Elevated: $admin"
if (-not $admin) {
    Say "WARNING: not running as administrator - firmware probes will likely fail." 'Red'
    Say "Close this window and start PowerShell as administrator." 'Red'
}

# ---------------------------------------------------------------- system ----
Head 'SYSTEM'
try {
    $cs = Get-CimInstance Win32_ComputerSystem
    $bb = Get-CimInstance Win32_BaseBoard
    $bios = Get-CimInstance Win32_BIOS
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    Say ("Vendor / model : {0} / {1}" -f $cs.Manufacturer, $cs.Model)
    Say ("Board          : {0} {1}" -f $bb.Manufacturer, $bb.Product)
    Say ("BIOS           : {0} ({1})" -f $bios.SMBIOSBIOSVersion, $bios.ReleaseDate)
    Say ("CPU            : {0}" -f $cpu.Name)
    Say ("Windows        : {0} build {1}" -f (Get-CimInstance Win32_OperatingSystem).Caption, [Environment]::OSVersion.Version.Build)
} catch { Say ("system info failed: {0}" -f $_.Exception.Message) 'Red' }

# ------------------------------------------------------- firmware iface ----
Head 'FIRMWARE INTERFACE (root\wmi MiCommonInterface)'
$inst = $null
$classFound = $false
try {
    $cls = Get-CimClass -Namespace root/wmi -ClassName MiCommonInterface -ErrorAction Stop
    $classFound = $true
    Say "Class MiCommonInterface : FOUND" 'Green'
    Say ("Methods                 : {0}" -f (($cls.CimClassMethods | ForEach-Object Name) -join ', '))
} catch {
    Say "Class MiCommonInterface : NOT FOUND" 'Red'
    Say ("Reason: {0}" -f $_.Exception.Message)
    Say "This model exposes its firmware through a different WMI class (see the list below)."
}
if ($classFound) {
    try {
        $inst = @(Get-CimInstance -Namespace root/wmi -ClassName MiCommonInterface -ErrorAction Stop)
        Say ("Instances               : {0}" -f $inst.Count)
        $inst = $inst[0]
    } catch {
        Say ("Instances               : CANNOT READ - {0}" -f $_.Exception.Message) 'Red'
        Say "  ('Access denied' here almost always means the script is not running as administrator.)" 'Red'
    }
}

# Send one GET request: IN[1]=0xFA (get), IN[3]=command, IN[4]/IN[6]=arguments.
# Status comes back in OUT[1]: 0x80 = supported, 0xE0 = not supported.
function MifsGet {
    param([byte]$Cmd, [byte]$Arg = 0, [byte]$Arg2 = 0, [string]$Label)
    if (-not $inst) { return }
    try {
        $in = [byte[]]::new(32)
        $in[1] = 0xFA; $in[3] = $Cmd; $in[4] = $Arg; $in[6] = $Arg2
        $out = (Invoke-CimMethod -InputObject $inst -MethodName MiInterface -Arguments @{ InData = [byte[]]$in }).OutData
        # WHOLE buffer: on some models the answer sits at different offsets than on TM2424,
        # and a 10-byte dump hides it (learned from issue #37)
        $hex = ($out | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
        $status = switch ($out[1]) {
            0x80 { 'OK' }
            0xE0 { 'NOT SUPPORTED' }
            default { '0x{0:X2} - not a known status, different response layout' -f $out[1] }
        }
        Say ("  {0,-22} cmd=0x{1:X2}/{2:X2} -> status {3}" -f $Label, $Cmd, $Arg, $status)
        Say ("  {0,-22} OUT: {1}" -f '', $hex)
    } catch {
        Say ("  {0,-22} cmd=0x{1:X2}/{2:X2} -> CALL FAILED: {3}" -f $Label, $Cmd, $Arg, $_.Exception.Message) 'Red'
    }
}

if ($inst) {
    Say ''
    Say 'Read-only probes (nothing is changed):'
    MifsGet -Cmd 0x08 -Label 'performance mode'
    MifsGet -Cmd 0x10 -Arg 0x02 -Label 'charge threshold'
    MifsGet -Cmd 0x10 -Arg 0x03 -Label 'charge zone flag'
    MifsGet -Cmd 0x10 -Arg 0x06 -Label 'adapter watts'
    MifsGet -Cmd 0x10 -Arg 0x01 -Label 'battery health'
    MifsGet -Cmd 0x0A -Label 'microphone'
}

# --------------------------------------------------- alternative classes ----
Head 'OTHER VENDOR WMI CLASSES WITH METHODS (root\wmi)'
Say 'Vendor/OEM interfaces found on this machine (Windows built-ins are filtered out):'
# noise = standard Windows providers; anything left is a candidate vendor interface
$noise = '^(__|MSNdis|MSFC|MSiSCSI|MS_SM|MSStorageDriver|MSAcpi|MSPower|MSKeyboard|MSMouse|MSSerial|MSDtc|Bcd|Wmi|MSSmBios|MSTapeDriveOperations|RootPortAlternateErrorDelivery|FIRE_TEST|MSPS|MSDiskDriver|MSNet|Intel(WiFi|Wireless))'
try {
    Get-CimClass -Namespace root/wmi -ErrorAction Stop |
        Where-Object { $_.CimClassMethods.Count -gt 0 -and $_.CimSystemProperties.ClassName -notmatch $noise } |
        ForEach-Object { Say ("  {0,-42} [{1}]" -f $_.CimSystemProperties.ClassName, (($_.CimClassMethods | ForEach-Object Name) -join ', ')) }
} catch { Say ("enumeration failed: {0}" -f $_.Exception.Message) 'Red' }

# -------------------------------------------------------------- thermal ----
Head 'TEMPERATURES (Intel DPTF provider)'
try {
    $null = Get-CimClass -Namespace root/wmi -ClassName EsifDeviceInformation -ErrorAction Stop
    try {
        $t = @(Get-CimInstance -Namespace root/wmi -ClassName EsifDeviceInformation -ErrorAction Stop)
        Say ("EsifDeviceInformation : FOUND, {0} domain(s)" -f $t.Count) 'Green'
        $t | ForEach-Object { Say ("  {0} = {1} C" -f $_.InstanceName, $_.Temperature) }
    } catch {
        Say ("EsifDeviceInformation : class exists but CANNOT READ - {0}" -f $_.Exception.Message) 'Red'
    }
} catch {
    Say "EsifDeviceInformation : NOT FOUND (expected on AMD models - this provider ships with Intel drivers)" 'Yellow'
}
try {
    $tz = @(Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature -ErrorAction Stop)
    Say ("MSAcpi_ThermalZoneTemperature : FOUND, {0} zone(s)" -f $tz.Count) 'Green'
    $tz | ForEach-Object { Say ("  {0} = {1:N1} C" -f $_.InstanceName, ($_.CurrentTemperature / 10 - 273.15)) }
} catch {
    Say "MSAcpi_ThermalZoneTemperature : NOT FOUND"
}

# ----------------------------------------------------------- key events ----
Head 'SPECIAL KEYS (HID_EVENT20)'
$known = @{
    0x01 = 'projection key (TM2424)'
    0x05 = 'keyboard backlight (TM2424)'
    0x07 = 'Fn-Lock (TM2424)'
    0x1B = 'settings key (TM2424)'
    0x21 = 'microphone (TM2424)'
    0x23 = 'AI key down (TM2424)'
    0x24 = 'AI key up (TM2424)'
    0x25 = 'Mi button down (TM2424)'
    0x26 = 'Mi button up (TM2424)'
}
$captured = New-Object System.Collections.Generic.List[string]
$sub = $null
try {
    $null = Get-CimClass -Namespace root/wmi -ClassName HID_EVENT20 -ErrorAction Stop
    Say "Class HID_EVENT20 : FOUND - key events are delivered on this model" 'Green'
    Say ''
    Say ("NOW PRESS YOUR SPECIAL KEYS, ONE AT A TIME, for the next {0} seconds." -f $Seconds) 'Yellow'
    Say 'Leave 2-3 seconds between presses and press each key TWICE.' 'Yellow'
    Say 'Most important: the Mi button (the one that does nothing).' 'Yellow'
    Say 'Write down the order you pressed them in - you will send it with the report.' 'Yellow'
    Say ''

    $sub = Register-CimIndicationEvent -Namespace root/wmi -Query 'SELECT * FROM HID_EVENT20' -SourceIdentifier XiDiagKeys -Action {
        $d = $Event.SourceEventArgs.NewEvent.EventDetail
        if ($d -and $d.Count -gt 2) {
            $hex = ($d[0..7] | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
            $line = "  {0}  code=0x{1:X2}  value=0x{2:X2}   raw[0..7]: {3}" -f (Get-Date -Format 'HH:mm:ss'), [int]$d[1], [int]$d[2], $hex
            Write-Host $line -ForegroundColor Cyan
            $Event.MessageData.Add($line)
        }
    } -MessageData $captured

    for ($i = $Seconds; $i -gt 0; $i--) {
        Write-Progress -Activity 'Recording key events' -Status "$i seconds left - press your special keys" -PercentComplete ((($Seconds - $i) / $Seconds) * 100)
        Start-Sleep -Seconds 1
    }
    Write-Progress -Activity 'Recording key events' -Completed
} catch {
    Say "Class HID_EVENT20 : NOT FOUND - this model does not report special keys through WMI" 'Red'
    Say ("Reason: {0}" -f $_.Exception.Message)
} finally {
    if ($sub) {
        Unregister-Event -SourceIdentifier XiDiagKeys -ErrorAction SilentlyContinue
        Get-Event -SourceIdentifier XiDiagKeys -ErrorAction SilentlyContinue | Remove-Event -ErrorAction SilentlyContinue
    }
}

Say ''
Say ("Captured {0} key event(s):" -f $captured.Count)
if ($captured.Count -eq 0) {
    Say '  (nothing) - either no key was pressed, or this model sends no WMI key events.'
} else {
    foreach ($l in $captured) {
        $lines.Add($l)
        $code = [Convert]::ToInt32(($l -split 'code=0x')[1].Substring(0, 2), 16)
        if ($known.ContainsKey($code)) { $lines.Add(("      ^ matches {0}" -f $known[$code])) }
    }
}

Head 'DONE'
Say "Report saved to: $Out" 'Green'
Say 'Attach this file to the GitHub issue, and write which key you pressed at which time.' 'Green'

$lines | Set-Content -Path $Out -Encoding UTF8
