# =============================================================================
# Publishes the cks conversion tool for distribution.
#
# The result is a single file with the .NET runtime bundled, so the recipient can run
# cks.exe without installing anything.
# SkiaSharp's native libraries are embedded into the executable as well.
# =============================================================================

param(
    # win-x64, linux-x64, osx-arm64 and so on
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$repoRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repoRoot 'src\tool\CoreKeeperSkinTool.csproj'
$output = Join-Path $repoRoot "build\cks\$Runtime"

if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }

Write-Output "発行中: $Runtime"
& dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $output `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "発行に失敗した (exit=$LASTEXITCODE)" }

# Debug symbols are not wanted in a release. SkiaSharp's native pdb alone is 85 MB and
# leaving it in would more than double the download size.
Get-ChildItem -LiteralPath $output -Filter *.pdb -Recurse | Remove-Item -Force

Write-Output ''
Get-ChildItem -LiteralPath $output -File |
Sort-Object Length -Descending |
Select-Object @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }, Name |
Format-Table -AutoSize | Out-String -Width 80 | Write-Output

# Confirm the published binary actually runs
$exe = Join-Path $output 'cks.exe'
if (Test-Path -LiteralPath $exe) {
    Write-Output '=== 動作確認 (cks layout) ==='
    # Capture the whole output first. Piping straight into Select-Object -First closes the
    # pipeline early, which kills the process and leaves $LASTEXITCODE at -1, so the check
    # below used to fail every single time no matter how well the executable worked.
    $smokeOutput = & $exe layout 2>&1
    $smokeExit = $LASTEXITCODE
    $smokeOutput | Select-Object -First 6 | ForEach-Object { Write-Output "    $_" }
    if ($smokeExit -ne 0) { throw "発行した実行ファイルが動作しない (exit=$smokeExit)" }
    Write-Output ''
    Write-Output "発行先: $exe"
}
