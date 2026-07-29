# Возвращает в winget-манифест рукописные поля, которые Komac (winget-releaser)
# теряет при пересборке installer-манифеста из portable-exe. История: MinimumOSVersion
# уехал в 0.7.0 и 0.8.0 — без него валидатор winget-pkgs режет PR («Inconsistencies
# detected … Missing property MinimumOSVersion»).
#
# Работает по ветке ФОРКА (Oksion/winget-pkgs), а не по PR: ветку Komac создаёт с
# детерминированным префиксом Oksion.XiControl-<версия>-, коммит в неё сам обновляет
# открытый PR, и не нужен Search API (он лагает первые минуты после создания PR).
# Идемпотентен: поле уже на месте → выходит без изменений, шаг можно перезапускать.
#
# Локальный запуск (нужен gh с правами на форк): ./winget-restore-fields.ps1 -Tag v0.8.0
param([Parameter(Mandatory)][string]$Tag)
$ErrorActionPreference = 'Stop'

$version = $Tag -replace '^v', ''
$fork = 'Oksion/winget-pkgs'
$prefix = "Oksion.XiControl-$version-"
$path = "manifests/o/Oksion/XiControl/$version/Oksion.XiControl.installer.yaml"

# ветка появляется, когда winget-releaser отработал; в CI шаг идёт следом,
# но даём API время на согласованность
$branch = $null
for ($i = 0; $i -lt 12 -and -not $branch; $i++) {
    if ($i) { Start-Sleep -Seconds 10 }
    $branch = gh api "repos/$fork/branches?per_page=100" --paginate --jq '.[].name' |
        Where-Object { $_.StartsWith($prefix) } | Select-Object -First 1
}
if (-not $branch) { throw "Ветка $prefix* в $fork не найдена — winget-releaser не отработал?" }
Write-Host "Ветка PR: $branch"

$file = gh api "repos/$fork/contents/${path}?ref=$branch" | ConvertFrom-Json
$text = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($file.content))

if ($text -match '(?m)^MinimumOSVersion:') {
    Write-Host 'MinimumOSVersion уже на месте — Komac починили? Патч не нужен.'
    exit 0
}

# вставляем по порядку полей эталона (reference/winget/): после Platform, перед InstallerType
$lines = [System.Collections.Generic.List[string]]($text -split "`r?`n")
$idx = $lines.FindIndex({ param($l) $l -match '^InstallerType:' })
if ($idx -lt 0) { throw "В $path не нашлась строка InstallerType — формат манифеста изменился, нужен глаз." }
$lines.Insert($idx, 'MinimumOSVersion: 10.0.0.0')

$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))
gh api -X PUT "repos/$fork/contents/$path" `
    -f message="Restore MinimumOSVersion dropped by Komac (portable manifest)" `
    -f content=$b64 -f sha=$($file.sha) -f branch=$branch | Out-Null
Write-Host "MinimumOSVersion возвращён в $path (ветка $branch); валидация PR перезапустится сама."
