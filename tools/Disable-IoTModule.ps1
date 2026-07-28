<#
.SYNOPSIS
  Выключает / включает always-on IoT-модуль удалённого включения (Xiaomi remote wake)
  на ноутбуках Xiaomi/Redmi. Driver-free, полностью обратимо.

.DESCRIPTION
  Модуль (ACPI\IOTD0000) — отдельный Wi-Fi-чип, который висит в домашней сети и облаке Xiaomi
  даже при выключенном ПК, чтобы включать его удалённо. Вне Китая фича не работает (нет облачного
  бэкенда), а модуль всё равно поднимается и «сам включается». Этот скрипт его глушит:

    1) отключает службу-«будильник» IoTSvc (часть драйвера, не приложения — именно она включает
       модуль на старте);
    2) гасит питание модуля через MIFS WMI (MiCommonInterface, команда 0x0C/0x03).

  Ничего не удаляет и не ломает: всё возвращается ключом -Action Enable. Разбор целиком —
  docs/11-iot-remote-wake.md. НЕ трогает Wi-Fi-провижининг и привязку (это уже OEM-территория).

.PARAMETER Action
  Disable (по умолчанию) — отключить IoTSvc и погасить модуль.
  Enable  — вернуть как было: IoTSvc → Automatic + запуск, включить модуль.
  Status  — только показать текущее состояние, ничего не менять.

.EXAMPLE
  .\Disable-IoTModule.ps1            # заглушить модуль
  .\Disable-IoTModule.ps1 -Action Status
  .\Disable-IoTModule.ps1 -Action Enable

.NOTES
  Требует прав администратора (MIFS-вызовы). Скрипт сам поднимет UAC при необходимости.
  Работает и в Windows PowerShell 5.1, и в PowerShell 7 (pwsh).
#>
[CmdletBinding()]
param(
    [ValidateSet('Disable', 'Enable', 'Status')]
    [string]$Action = 'Disable',
    [switch]$Elevated   # служебный: выставляется при само-перезапуске от админа (пауза в конце)
)

# --- само-элевейт, если запущено без прав администратора ---
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $exe = (Get-Process -Id $PID).Path
    if (-not $exe) { $exe = 'powershell.exe' }
    Write-Host 'Нужны права администратора — поднимаю UAC...'
    Start-Process -FilePath $exe -Verb RunAs -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-Action', $Action, '-Elevated'
    )
    return
}

$SvcName = 'IoTSvc'

# --- MIFS: питание модуля (команда 0x0C, под-функция 0x03) ---
function Get-MifsInstance {
    try {
        return Get-CimInstance -Namespace 'root/wmi' -ClassName 'MiCommonInterface' -ErrorAction Stop | Select-Object -First 1
    } catch {
        return $null
    }
}
function Invoke-Mifs {
    param($Inst, [byte]$Op, [byte]$Val = 0)
    $b = New-Object 'byte[]' 32
    $b[1] = $Op        # 0xFA = GET, 0xFB = SET
    $b[3] = 0x0C       # команда: канал IoT-модуля
    $b[4] = 0x03       # под-функция: питание
    $b[6] = $Val
    $r = Invoke-CimMethod -InputObject $Inst -MethodName 'MiInterface' -Arguments @{ InData = [byte[]]$b } -ErrorAction Stop
    return $r.OutData
}
function Get-ModulePower { param($Inst) return (Invoke-Mifs -Inst $Inst -Op 0xFA)[6] }        # 1 = вкл, 0 = выкл
function Set-ModulePower { param($Inst, [byte]$Val) return (Invoke-Mifs -Inst $Inst -Op 0xFB -Val $Val)[1] } # 0x80 = ок

function Get-SvcState {
    $s = Get-Service -Name $SvcName -ErrorAction SilentlyContinue
    if ($null -eq $s) { return 'нет службы' }
    return "$($s.StartType)/$($s.Status)"
}

# --- вывод состояния ---
function Show-State {
    param($Inst, [string]$Tag)
    $power = if ($null -eq $Inst) { 'MIFS недоступен (не Xiaomi/Redmi?)' }
             else { if ((Get-ModulePower -Inst $Inst) -eq 1) { 'ВКЛ' } else { 'ВЫКЛ' } }
    Write-Host ("{0,-10} питание модуля: {1,-5}  служба IoTSvc: {2}" -f $Tag, $power, (Get-SvcState))
}

Write-Host ''
Write-Host "=== IoT-модуль удалённого включения — действие: $Action ==="
$inst = Get-MifsInstance
if ($null -eq $inst -and $Action -ne 'Status') {
    Write-Host 'MIFS-интерфейс (MiCommonInterface) не найден — это не Xiaomi/Redmi с прошивкой MIFS, либо нет прав. Выхожу.'
    if ($Elevated) { Read-Host 'Enter для выхода' }
    return
}

Show-State -Inst $inst -Tag 'сейчас:'

switch ($Action) {
    'Status' {
        # ничего не меняем
    }
    'Disable' {
        # 1) погасить «будильник»
        $svc = Get-Service -Name $SvcName -ErrorAction SilentlyContinue
        if ($svc) {
            try {
                Set-Service -Name $SvcName -StartupType Disabled -ErrorAction Stop
                if ($svc.Status -ne 'Stopped') { Stop-Service -Name $SvcName -Force -ErrorAction Stop }
                Write-Host "  IoTSvc → Disabled + остановлена"
            } catch { Write-Host "  IoTSvc: не удалось ($($_.Exception.Message))" }
        } else {
            Write-Host "  IoTSvc: службы нет (OEM-стек, похоже, уже снесён) — пропускаю"
        }
        # 2) погасить модуль через MIFS
        try {
            $st = Set-ModulePower -Inst $inst -Val 0
            Write-Host ("  MIFS питание → выкл (status 0x{0:X2})" -f $st)
        } catch { Write-Host "  MIFS: не удалось ($($_.Exception.Message))" }
        Start-Sleep -Seconds 2
        Show-State -Inst $inst -Tag 'итог:'
        Write-Host 'Модуль заглушён: он не включится сам и не выйдет в сеть. Вернуть: -Action Enable.'
    }
    'Enable' {
        $svc = Get-Service -Name $SvcName -ErrorAction SilentlyContinue
        if ($svc) {
            try {
                Set-Service -Name $SvcName -StartupType Automatic -ErrorAction Stop
                Start-Service -Name $SvcName -ErrorAction Stop
                Write-Host "  IoTSvc → Automatic + запущена"
            } catch { Write-Host "  IoTSvc: не удалось ($($_.Exception.Message))" }
        } else {
            Write-Host "  IoTSvc: службы нет — вернуть автозапуск нечему (OEM-стек снесён)"
        }
        try {
            $st = Set-ModulePower -Inst $inst -Val 1
            Write-Host ("  MIFS питание → вкл (status 0x{0:X2})" -f $st)
        } catch { Write-Host "  MIFS: не удалось ($($_.Exception.Message))" }
        Start-Sleep -Seconds 2
        Show-State -Inst $inst -Tag 'итог:'
        Write-Host 'Модуль возвращён. Учти: облачный remote-wake вне Китая всё равно не работает (см. docs/11).'
    }
}

Write-Host ''
if ($Elevated) { Read-Host 'Готово. Enter для выхода' }
