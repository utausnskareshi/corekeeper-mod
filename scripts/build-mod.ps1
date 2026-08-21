# =============================================================================
# Builds the mod and installs it into Core Keeper.
#
# Starts Unity in batch mode and calls the SDK's ModBuilder.BuildMod directly.
# No GUI interaction is needed, but the build fails if the same project is open in the
# Unity editor, which locks the project. Close it first.
#
# Prerequisites:
#   - Unity 6000.0.59f2 is installed
#   - "Update Game Files" has been run on the SDK's Update SDK tab
#   - scripts/setup-mod-project.ps1 has been run
#   - Core Keeper is installed. The usual Steam locations are searched; pass -GamePath, or
#     set CKS_GAME_PATH, when the game lives somewhere the search does not reach.
# =============================================================================

param(
    # Where Core Keeper is installed. Left empty, it is searched for; see Resolve-GamePath.
    [string]$GamePath = '',
    # Which Unity editor to use
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.0.59f2\Editor\Unity.exe'
)

$ErrorActionPreference = 'Stop'

# Keep non-ASCII output readable when it is redirected
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

# --- Locate the game ------------------------------------------------------------
# Searched for here rather than defaulted in the parameter, which would bake one machine's
# drive layout into the script. A Steam library can sit on any drive, so a hard-coded path
# sends everyone else straight to the not-found error below - the author included, once the
# library moves.
function Resolve-GamePath {
    param([string]$Explicit)

    if ($Explicit) { return $Explicit }
    if ($env:CKS_GAME_PATH) { return $env:CKS_GAME_PATH }

    $suffix = 'steamapps\common\Core Keeper'
    $candidates = @()

    # Steam's own install locations. Note the braces: "$env:ProgramFiles(x86)" parses as
    # $env:ProgramFiles followed by the literal (x86).
    foreach ($root in @(${env:ProgramFiles(x86)}, ${env:ProgramFiles})) {
        if ($root) { $candidates += (Join-Path $root "Steam\$suffix") }
    }

    # Extra libraries, which Steam offers to create as <drive>\SteamLibrary. Fixed drives
    # only: probing a disconnected network drive can block for seconds each.
    foreach ($drive in [System.IO.DriveInfo]::GetDrives()) {
        if ($drive.DriveType -ne [System.IO.DriveType]::Fixed -or -not $drive.IsReady) { continue }
        $root = $drive.RootDirectory.FullName
        $candidates += (Join-Path $root "SteamLibrary\$suffix")
        $candidates += (Join-Path $root "Steam\$suffix")
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }

    if ($candidates.Count -eq 0) {
        throw 'ゲームの場所を探せない。-GamePath で指定するか、CKS_GAME_PATH を設定すること。'
    }

    # Nothing found. Hand back the first candidate so the check below names a real path
    # instead of failing on an empty one.
    return $candidates[0]
}

$GamePath = Resolve-GamePath -Explicit $GamePath

$repoRoot = Split-Path $PSScriptRoot -Parent
$sdkRoot = Join-Path $repoRoot 'CoreKeeperModSDK'
$editorScriptSource = Join-Path $repoRoot 'src\sdk-editor\CksBuildAutomation.cs'
$editorScriptDir = Join-Path $sdkRoot 'Assets\Editor'
$logPath = Join-Path $repoRoot 'build\unity-build.log'

# Where local mods are looked for. PugMod.SideLoader.Init scans
#   Application.streamingAssetsPath + "/Mods"
# and looks for ModManifest.json in each folder directly beneath it.
# The official "Manual Installation" documentation says directly under StreamingAssets,
# but disassembling the actual binary shows the Mods subfolder is correct.
$installPath = Join-Path $GamePath 'CoreKeeper_Data\StreamingAssets\Mods'

# --- Check prerequisites --------------------------------------------------------
if (-not (Test-Path $UnityPath)) { throw "Unity が見つからない: $UnityPath" }
if (-not (Test-Path $sdkRoot)) { throw "ModSDK が見つからない: $sdkRoot" }
if (-not (Test-Path $installPath)) { throw "Core Keeper が見つからない: $installPath" }

$running = Get-Process -Name 'Unity' -ErrorAction SilentlyContinue
if ($running) {
    throw "Unity エディタが起動中。プロジェクトがロックされるため、閉じてから再実行すること。"
}

# Say which copy of the game was chosen. The path is searched for rather than given, so a
# second Steam library on the machine would otherwise take the mod without a word.
Write-Output "ゲーム: $GamePath"

# --- Copy the helper script into the SDK -----------------------------------------
# Place it outside the mod folder (Assets/Editor); inside, it would end up in the build output.
# The asmdef goes with it. Without it the script lands in the default Assembly-CSharp-Editor,
# which cannot reference the SDK editor assembly (ModSDK.Editor), so PugMod is not found.
New-Item -ItemType Directory -Force -Path $editorScriptDir | Out-Null
Copy-Item $editorScriptSource (Join-Path $editorScriptDir 'CksBuildAutomation.cs') -Force
Copy-Item (Join-Path $repoRoot 'src\sdk-editor\CksBuildAutomation.asmdef') `
    (Join-Path $editorScriptDir 'CksBuildAutomation.asmdef') -Force

New-Item -ItemType Directory -Force -Path (Split-Path $logPath) | Out-Null
if (Test-Path $logPath) { Remove-Item $logPath -Force }

# --- Run in batch mode ---------------------------------------------------------
Write-Output "ビルド開始（数分かかる。ログ: $logPath）"
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

# Unity.exe is a GUI subsystem binary, so PowerShell does not wait for it; wait explicitly.
# Quote every path: one containing a space (such as "Core Keeper") would be split into two arguments.
$process = Start-Process -FilePath $UnityPath -PassThru -ArgumentList @(
    '-batchmode',
    '-disable-assembly-updater',
    '-projectPath', "`"$sdkRoot`"",
    '-executeMethod', 'CksBuildAutomation.BuildAndInstall',
    '-cksInstallPath', "`"$installPath`"",
    '-logFile', "`"$logPath`""
)
$process.WaitForExit()

$elapsed = [math]::Round($stopwatch.Elapsed.TotalMinutes, 1)
Write-Output "Unity 終了 (exit=$($process.ExitCode)) 所要 $elapsed 分"

# --- Report the result ---------------------------------------------------------
$compileErrors = @()
if (Test-Path $logPath) {
    $compileErrors = @(Select-String -Path $logPath -Pattern 'error CS\d+' | Select-Object -First 20)
    if ($compileErrors.Count -gt 0) {
        Write-Output ''
        Write-Output '=== コンパイルエラー ==='
        $compileErrors | ForEach-Object { $_.Line.Trim() }
    }

    $modLog = @(Select-String -Path $logPath -Pattern '\[cks\]' | Select-Object -Last 10)
    if ($modLog.Count -gt 0) {
        Write-Output ''
        Write-Output '=== ビルドログ ==='
        $modLog | ForEach-Object { $_.Line.Trim() }
    }
}

# A compile error must stop the script even when Unity reports success. Otherwise the folder
# left by a previous build is repackaged and shipped as the payload, so the released
# application would silently contain the last version that happened to compile.
if ($compileErrors.Count -gt 0) {
    Write-Output ''
    Write-Output "コンパイルエラーがあるため中止した。詳細は $logPath を確認すること。"
    exit 1
}

$installedPath = Join-Path $installPath 'CustomPlayerSkin'
if ($process.ExitCode -eq 0 -and (Test-Path $installedPath)) {
    Write-Output ''
    Write-Output "インストール先: $installedPath"
    Get-ChildItem $installedPath -Recurse -File |
    Select-Object @{n = 'KB'; e = { [math]::Round($_.Length / 1KB, 1) } }, Name |
    Format-Table -AutoSize | Out-String -Width 100 | Write-Output

    # ------------------------------------------------------------------------
    # Build the payload that ships with the application.
    # Embedding this in the GUI lets users install the mod without owning Unity.
    # ------------------------------------------------------------------------
    $payloadDir = Join-Path $repoRoot 'build\mod-payload'
    $payload = Join-Path $payloadDir 'CustomPlayerSkin.zip'
    New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null
    if (Test-Path $payload) { Remove-Item $payload -Force }

    # Not Compress-Archive: PowerShell 5.1 writes entry names with a backslash separator,
    # which the zip format does not allow. Entries then fail to be recognised as folders,
    # and extracting on a non-Windows system produces files literally named "Scripts\x.cs".
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.IO.Compression
    $archive = [System.IO.Compression.ZipFile]::Open($payload, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $prefix = [System.IO.Path]::GetFullPath($installedPath).TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
            [System.IO.Path]::DirectorySeparatorChar
        foreach ($file in Get-ChildItem $installedPath -Recurse -File) {
            $entryName = $file.FullName.Substring($prefix.Length).Replace([System.IO.Path]::DirectorySeparatorChar, '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $archive.Dispose()
    }

    Write-Output ''
    Write-Output ("同梱用データ: {0} ({1} KB)" -f $payload, [math]::Round((Get-Item $payload).Length / 1KB, 1))
    Write-Output '  この後 scripts/package-release.ps1 を実行すると、これを埋め込んだ配布物ができる。'
}
else {
    Write-Output ''
    Write-Output "ビルドに失敗した。詳細は $logPath を確認すること。"
    exit 1
}
