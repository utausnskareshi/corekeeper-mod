# =============================================================================
# Development environment setup helper.
#
# Merely using the mod needs nothing installed: the released GUI runs without Unity or .NET.
# This script is for developers who want to modify the mod itself.
#
# Approach:
#   - Only things confined to this repository (tools/) are installed automatically.
#     Removing the repository removes them too, so nothing else can break.
#   - The .NET SDK is a shared component, so it is installed via winget only after confirmation.
#     It is never removed, because other applications may depend on it.
#   - Unity cannot be automated because it needs an account and licence activation; we explain the steps instead.
#
# What gets installed is recorded in .cks-dev-setup.json, and scripts/cleanup-dev.ps1
# and only what that record lists is removed.
# =============================================================================

param(
    # Skip the prompt and install straight away
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$repoRoot = Split-Path $PSScriptRoot -Parent
$toolsDir = Join-Path $repoRoot 'tools'
$manifestPath = Join-Path $repoRoot '.cks-dev-setup.json'

$requiredUnity = '6000.0.59f2'
$unityRevision = 'ef281c76c3c1'
$assetRipperVersion = '1.2.1'
$assetRipperUrl = "https://github.com/AssetRipper/AssetRipper/releases/download/$assetRipperVersion/AssetRipper_win_x64.zip"

# --- Installation record ---------------------------------------------------------
function Read-Manifest {
    if (Test-Path $manifestPath) {
        try { return Get-Content $manifestPath -Raw | ConvertFrom-Json } catch { }
    }
    return [pscustomobject]@{ installed = @() }
}

function Add-ManifestEntry([string]$Kind, [string]$Path, [string]$Note) {
    $manifest = Read-Manifest
    $entries = @($manifest.installed | Where-Object { $_.path -ne $Path })
    $entries += [pscustomobject]@{ kind = $Kind; path = $Path; note = $Note }
    $json = [pscustomobject]@{ installed = $entries } | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Confirm-Action([string]$Message) {
    if ($Yes) { return $true }
    $answer = Read-Host "$Message [y/N]"
    return $answer -match '^[yY]'
}

# --- Detection -----------------------------------------------------------------
Write-Output '=== 開発環境の確認 ==='
$missing = @()

# .NET SDK
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetOk = $false
if ($dotnet) {
    $sdks = & dotnet --list-sdks 2>$null
    $dotnetOk = @($sdks | Where-Object { $_ -match '^9\.' }).Count -gt 0
}
if ($dotnetOk) {
    Write-Output '  [OK]   .NET SDK 9'
}
else {
    Write-Output '  [不足] .NET SDK 9  … ツールと GUI のビルドに必要'
    $missing += 'dotnet'
}

# Unity
$unityPath = Join-Path $env:ProgramFiles "Unity\Hub\Editor\$requiredUnity\Editor\Unity.exe"
$unityOk = Test-Path $unityPath
if ($unityOk) {
    $linuxMono = Join-Path $env:ProgramFiles "Unity\Hub\Editor\$requiredUnity\Editor\Data\PlaybackEngines\LinuxStandaloneSupport"
    if (Test-Path $linuxMono) {
        Write-Output "  [OK]   Unity $requiredUnity (Linux Build Support あり)"
    }
    else {
        Write-Output "  [注意] Unity $requiredUnity はあるが Linux Build Support が無い … MOD をビルドできない"
        $missing += 'unity-module'
    }
}
else {
    Write-Output "  [不足] Unity $requiredUnity  … MOD 自体を作り変える場合に必要"
    $missing += 'unity'
}

# AssetRipper
$assetRipperExe = Join-Path $toolsDir 'AssetRipper\AssetRipper.GUI.Free.exe'
if (Test-Path $assetRipperExe) {
    Write-Output "  [OK]   AssetRipper $assetRipperVersion"
}
else {
    Write-Output "  [不足] AssetRipper $assetRipperVersion  … ゲームのアセット解析に必要（レイアウト再生成時のみ）"
    $missing += 'assetripper'
}

# The game itself
$gameFound = $false

# Note the braces: "$env:ProgramFiles(x86)" parses as $env:ProgramFiles followed by the literal
# text "(x86)", which is never a real path. The name has to be wrapped to be read as a whole.
$steamRoots = @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")

# Steam is often installed somewhere else entirely, and then only the registry knows where.
foreach ($key in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam')) {
    try {
        $item = Get-ItemProperty -Path $key -ErrorAction Stop
        foreach ($name in @('SteamPath', 'InstallPath')) {
            if ($item.$name) { $steamRoots += $item.$name.Replace('/', '\') }
        }
    }
    catch {
        # Steam is simply not registered here; the well-known folders above still apply.
    }
}

foreach ($library in ($steamRoots | Select-Object -Unique)) {
    $vdf = Join-Path $library 'steamapps\libraryfolders.vdf'
    if (-not (Test-Path $vdf)) { continue }
    foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
        $candidate = Join-Path ($m.Groups[1].Value -replace '\\\\', '\') 'steamapps\common\Core Keeper\CoreKeeper.exe'
        if (Test-Path $candidate) { $gameFound = $true; break }
    }
}
Write-Output $(if ($gameFound) { '  [OK]   Core Keeper' } else { '  [不足] Core Keeper … Steam でインストールすること' })

if ($missing.Count -eq 0) {
    Write-Output ''
    Write-Output 'すべて揃っている。'
    exit 0
}

# --- Installation ---------------------------------------------------------------
Write-Output ''
Write-Output '=== 不足分の導入 ==='

if ($missing -contains 'assetripper') {
    if (Confirm-Action "AssetRipper $assetRipperVersion を tools/ へ導入する？") {
        $zip = Join-Path $toolsDir 'AssetRipper.zip'
        New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
        Write-Output '  ダウンロード中…'
        Invoke-WebRequest -Uri $assetRipperUrl -OutFile $zip -UseBasicParsing
        Expand-Archive -Path $zip -DestinationPath (Join-Path $toolsDir 'AssetRipper') -Force
        Remove-Item $zip -Force
        Add-ManifestEntry 'directory' (Join-Path $toolsDir 'AssetRipper') "AssetRipper $assetRipperVersion"
        Write-Output '  完了。'
    }
}

if ($missing -contains 'dotnet') {
    Write-Output ''
    Write-Output '.NET SDK 9 は複数のアプリで共有されるコンポーネント。'
    Write-Output 'このスクリプトでは削除しない（他のアプリを壊す恐れがあるため）。'
    if (Confirm-Action 'winget で .NET SDK 9 を導入する？') {
        & winget install --id Microsoft.DotNet.SDK.9 --accept-source-agreements --accept-package-agreements
        Add-ManifestEntry 'shared' 'Microsoft.DotNet.SDK.9' '共有コンポーネント。cleanup では削除しない'
    }
}

if ($missing -contains 'unity' -or $missing -contains 'unity-module') {
    Write-Output ''
    Write-Output "Unity $requiredUnity は自動導入できない（アカウントとライセンス認証が必要）。"
    Write-Output '次の手順で導入すること:'
    Write-Output '  1. Unity Hub を入れる: https://unity.com/download'
    Write-Output "  2. 次のリンクを開くと Hub が該当バージョンの導入を始める:"
    Write-Output "       unityhub://$requiredUnity/$unityRevision"
    Write-Output '  3. モジュール選択で「Linux Build Support (Mono)」に必ずチェックを入れる'
    Write-Output '     （これが無いと MOD をビルドできない）'
    if (Confirm-Action 'Unity Hub のダウンロードページを開く？') {
        Start-Process 'https://unity.com/download'
    }
}

Write-Output ''
Write-Output "導入記録: $manifestPath"
Write-Output '不要になったら scripts/cleanup-dev.ps1 で取り除ける。'
