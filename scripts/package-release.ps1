# =============================================================================
# Builds the release package (a portable ZIP).
#
# The resulting ZIP runs as soon as it is extracted. Neither Unity nor .NET is
# required, and deleting the folder removes it completely.
#
# Run scripts/build-mod.ps1 first so that build/mod-payload exists.
# Without it the mod cannot be bundled and users would be unable to install it.
# =============================================================================

param(
    # win-x64 / linux-x64
    [string[]]$Runtimes = @('win-x64'),
    # Build from a working tree with uncommitted changes anyway, for a trial package. Its product
    # version then says so with a "-dirty" suffix.
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$repoRoot = Split-Path $PSScriptRoot -Parent
$payload = Join-Path $repoRoot 'build\mod-payload\CustomPlayerSkin.zip'
$outputRoot = Join-Path $repoRoot 'build\release'

if (-not (Test-Path -LiteralPath $payload)) {
    throw @"
MOD の同梱データが無い: $payload
先に scripts/build-mod.ps1 を実行すること。
これが無いと、利用者が MOD を導入できない配布物になる。
"@
}

# Stale is as bad as missing, and only the missing case was checked. A failed or forgotten
# mod build leaves the previous zip in place, so the release went out carrying a mod built
# from source nobody had touched in days - with no sign of it anywhere in the output. The
# only thing standing between that and a user was somebody remembering the right order.
$payloadTime = (Get-Item -LiteralPath $payload).LastWriteTimeUtc
$modSources = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src\mod') -Recurse -File -Filter *.cs
$newer = $modSources | Where-Object { $_.LastWriteTimeUtc -gt $payloadTime }

if ($newer) {
    $names = ($newer | ForEach-Object { $_.Name }) -join ', '
    throw @"
同梱データが src/mod より古い: $payload
新しいファイル: $names
先に scripts/build-mod.ps1 を実行すること。
これを見逃すと、古い MOD を同梱した配布物が黙って出来上がる。
"@
}

Write-Output ("同梱する MOD: {0} ({1} KB)" -f $payload, [math]::Round((Get-Item -LiteralPath $payload).Length / 1KB, 1))

# The product version's +hash is stamped by the SDK from HEAD, and it cannot see uncommitted
# changes: a package built before committing claimed the commit before the one it contained (the
# package of 2026-09-29 did). Refused unless asked for, and marked when it is.
$revisionArgs = @()
if (Get-Command git -ErrorAction SilentlyContinue) {
    # Windows PowerShell 5.1 turns a redirected native stderr line into a terminating error under Stop
    $changes = & { $ErrorActionPreference = 'Continue'; git -C $repoRoot status --porcelain -- src data packaging scripts LICENSE THIRD-PARTY-NOTICES.md 2>$null }
    $gitExit = $LASTEXITCODE
    if ($gitExit -eq 0 -and $changes) {
        if (-not $AllowDirty) {
            throw ("未コミットの変更がある:`n" + ($changes -join "`n") + "`n" +
                "製品バージョンは HEAD のコミットを名乗るため、先にコミットしてから作り直すこと（試しに作るだけなら -AllowDirty）。")
        }
        $head = & { $ErrorActionPreference = 'Continue'; git -C $repoRoot rev-parse HEAD 2>$null }
        $revisionArgs = @("-p:SourceRevisionId=$head-dirty")
        Write-Warning "未コミットの変更を含めて作る。製品バージョンに -dirty を付ける。"
    }
    elseif ($gitExit -ne 0) {
        # A source tree without .git - an archive download, say. The SDK stamps no hash then, so
        # nothing false is claimed.
        Write-Warning "git の作業ツリーではないため、未コミットの変更を確かめられない (exit=$gitExit)"
    }
}

foreach ($runtime in $Runtimes) {
    Write-Output ''
    Write-Output "=== $runtime ==="

    $stage = Join-Path $outputRoot $runtime
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    foreach ($project in @('src\gui\CoreKeeperSkinTool.Gui.csproj', 'src\tool\CoreKeeperSkinTool.csproj')) {
        $name = Split-Path (Split-Path $project -Parent) -Leaf
        Write-Output "  発行: $name"

        # Keep the output: piping it away hides the reason a publish failed, leaving only
        # an exit code to go on. CksRequireModPayload turns a missing bundled mod into a
        # build error, so a release can never ship with the install button disabled.
        # DebugType=none: no symbols are shipped (the .pdb files are deleted below), and an
        # assembly built with them records where its .pdb was written - a folder on this
        # machine - so that path went out inside every released executable.
        $publishLog = & dotnet publish (Join-Path $repoRoot $project) `
            -c Release `
            -r $runtime `
            --self-contained true `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true `
            -p:DebugType=none `
            -p:CksRequireModPayload=true `
            @revisionArgs `
            -o $stage `
            --nologo 2>&1

        if ($LASTEXITCODE -ne 0) {
            $publishLog | Where-Object { $_ -match 'error|warning' } | ForEach-Object { Write-Output "    $_" }
            throw "$name の発行に失敗した (exit=$LASTEXITCODE)"
        }
    }

    # Release packages do not need debug symbols (SkiaSharp's alone is 85 MB)
    Get-ChildItem -LiteralPath $stage -Filter *.pdb -Recurse | Remove-Item -Force

    # Portable marker. With it present, settings and working data stay inside the app folder,
    # so that deleting the folder removes everything.
    # "Remove mod" is the button that takes the mod out of the game. "Remove from game" only
    # gives the ticked characters their own appearance back and leaves the mod installed - and
    # it cannot even be pressed with no character ticked. Naming the wrong one here contradicted
    # readme.txt sitting beside it in the same zip.
    Set-Content -LiteralPath (Join-Path $stage 'portable.txt') -Encoding UTF8 -Value @'
このファイルがあると、設定をこのフォルダ内に保存します。
フォルダごと削除すれば、アプリの設定は完全に消えます。
ゲーム側に入れた MOD は、アプリ右上の「MOD を削除」で取り除いてください。
'@

    # The libraries inside the self-contained build are MIT-licensed, which obliges us to ship
    # their copyright and permission notice with the binaries. Missing files are a hard error:
    # silently releasing without them would put the package out of licence compliance.
    #
    # readme.txt is treated the same way. It is the only place the user is told what to do about
    # the things that stop them before the application ever opens - SmartScreen refusing to run
    # an unsigned executable, an antivirus product objecting to a single-file build, and the
    # game not being found - and none of that can be said from inside an application they have
    # not managed to start.
    foreach ($notice in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        $source = Join-Path $repoRoot $notice
        if (-not (Test-Path -LiteralPath $source)) { throw "配布に必要なファイルが無い: $source" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $stage $notice) -Force
    }

    # Kept under packaging/ rather than dist/, which .gitignore excludes as an output folder.
    # A source file living there would not reach a clone, and this script refuses to run
    # without it, so packaging would fail for everyone but the machine it was written on.
    $readme = Join-Path $repoRoot 'packaging\readme.txt'
    if (-not (Test-Path -LiteralPath $readme)) { throw "配布に必要なファイルが無い: $readme" }
    Copy-Item -LiteralPath $readme -Destination (Join-Path $stage "readme.txt") -Force

    $zip = Join-Path $outputRoot "CoreKeeperSkinTool-$runtime.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

    Write-Output ''
    Get-ChildItem -LiteralPath $stage -File |
    Sort-Object Length -Descending |
    Select-Object @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }, Name |
    Format-Table -AutoSize | Out-String -Width 80 | Write-Output

    Write-Output ("配布物: {0} ({1} MB)" -f $zip, [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1))
}
