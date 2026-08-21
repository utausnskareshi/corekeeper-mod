# =============================================================================
# Removes what setup-dev.ps1 installed.
#
# Only entries recorded in .cks-dev-setup.json that live inside this repository are deleted.
# Shared components such as the .NET SDK may be in use by other applications, so they
# are recorded but never removed. Remove them yourself from Windows settings if needed.
# =============================================================================

param(
    # Skip the prompt and delete straight away
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$repoRoot = Split-Path $PSScriptRoot -Parent
$manifestPath = Join-Path $repoRoot '.cks-dev-setup.json'

if (-not (Test-Path $manifestPath)) {
    Write-Output '導入記録が無い。setup-dev.ps1 で導入したものは無いため、削除するものも無い。'
    exit 0
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$entries = @($manifest.installed)

$removable = @($entries | Where-Object { $_.kind -eq 'directory' -and (Test-Path $_.path) })
$shared = @($entries | Where-Object { $_.kind -eq 'shared' })

Write-Output '=== 削除できるもの（このリポジトリ配下） ==='
if ($removable.Count -eq 0) {
    Write-Output '  なし'
}
else {
    foreach ($entry in $removable) {
        $size = [math]::Round(((Get-ChildItem $entry.path -Recurse -File -ErrorAction SilentlyContinue |
                    Measure-Object Length -Sum).Sum) / 1MB, 1)
        Write-Output ("  {0}  ({1} MB)  {2}" -f $entry.path, $size, $entry.note)
    }
}

if ($shared.Count -gt 0) {
    Write-Output ''
    Write-Output '=== 削除しないもの（共有コンポーネント） ==='
    foreach ($entry in $shared) {
        Write-Output ("  {0}  {1}" -f $entry.path, $entry.note)
    }
    Write-Output '  他のアプリが使っている可能性があるため、このスクリプトでは削除しない。'
    Write-Output '  外す場合は Windows の「アプリと機能」から手動で行うこと。'
}

if ($removable.Count -eq 0) {
    exit 0
}

Write-Output ''
if (-not $Yes) {
    $answer = Read-Host '上記を削除する？ [y/N]'
    if ($answer -notmatch '^[yY]') {
        Write-Output '中止した。'
        exit 0
    }
}

$removed = @()
foreach ($entry in $removable) {
    # Verify the path is inside the repository before deleting, in case the record is corrupt.
    # The root needs a trailing separator: a bare prefix test also accepts a sibling whose name
    # merely starts with the same text, so "<repo>-backup" would pass and be deleted.
    $full = [System.IO.Path]::GetFullPath($entry.path)
    $rootPrefix = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Output "  スキップ（リポジトリ外）: $full"
        continue
    }

    Remove-Item $full -Recurse -Force
    $removed += $entry.path
    Write-Output "  削除: $full"
}

# Drop the removed entries from the record
$rest = @($entries | Where-Object { $removed -notcontains $_.path })
$json = [pscustomobject]@{ installed = $rest } | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Output ''
Write-Output "$($removed.Count) 件を削除した。"
