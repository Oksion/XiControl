param(
    [string]$SourceRoot = 'C:\Program Files\MI\XiaomiPCManager\5.8.1.121\res\Image',
    [string]$DestinationRoot = (Join-Path $PSScriptRoot '..\assets\oem\xiaomi\osd')
)

$ErrorActionPreference = 'Stop'
$source = [System.IO.Path]::GetFullPath($SourceRoot)
$destination = [System.IO.Path]::GetFullPath($DestinationRoot)

if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "Xiaomi OSD image directory not found: $source"
}

$files = @(Get-ChildItem -LiteralPath $source -File -Filter '*.png' | Sort-Object Name)
if ($files.Count -eq 0) {
    throw "No PNG resources found in: $source"
}

New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($file in $files) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name) -Force
}

$bytes = ($files | Measure-Object -Property Length -Sum).Sum
Write-Output ("Extracted {0} Xiaomi OSD PNG files ({1:N2} MiB) to {2}" -f
    $files.Count, ($bytes / 1MB), $destination)
