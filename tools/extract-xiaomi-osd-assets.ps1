param(
    [string]$SourceRoot,
    [string]$DestinationRoot = (Join-Path $PSScriptRoot '..\assets\oem\xiaomi\osd')
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $managerRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'MI\XiaomiPCManager'
    $candidates = @()
    if (Test-Path -LiteralPath $managerRoot -PathType Container) {
        $candidates = @(Get-ChildItem -LiteralPath $managerRoot -Directory |
            ForEach-Object { Join-Path $_.FullName 'res\Image' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container } |
            Sort-Object -Descending)
    }
    if ($candidates.Count -eq 0) {
        throw "Xiaomi OSD resources were not found under $managerRoot. Pass -SourceRoot explicitly."
    }
    $SourceRoot = $candidates[0]
}

$source = [System.IO.Path]::GetFullPath($SourceRoot)
$destination = [System.IO.Path]::GetFullPath($DestinationRoot)

if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "Xiaomi OSD image directory not found: $source"
}
if ([string]::Equals($source, $destination, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'SourceRoot and DestinationRoot must be different directories.'
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
