# =============================================================================
# Wires the mod into the ModSDK project.
#
# Produces the equivalent of the SDK's "New Mod" button without depending on the GUI,
# so that anyone who clones the repository can reproduce the same state.
#
#   1. src/mod/<ModName>.asmdef          - assembly definition listing every imported game assembly
#   2. <SDK>/Assets/<ModName>.asset      - ModBuilderSettings (build configuration)
#   3. <SDK>/Assets/<ModName>            - junction pointing at src/mod
#
# Prerequisite: "Update Game Files" has been run on the SDK's Update SDK tab.
# =============================================================================

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$sdkRoot = Join-Path $repoRoot 'CoreKeeperModSDK'
$modSourceDir = Join-Path $repoRoot 'src\mod'
$modName = 'CustomPlayerSkin'
$modDisplayName = 'Custom Player Skin'

$gameAssemblyDir = Join-Path $sdkRoot 'Assets\Plugins\CoreKeeper'
$sdkAssemblyDir = Join-Path $sdkRoot 'Assets\Plugins\CoreKeeperModSDK'
$assetsDir = Join-Path $sdkRoot 'Assets'
$junctionPath = Join-Path $assetsDir $modName
$settingsPath = Join-Path $assetsDir "$modName.asset"
$asmdefPath = Join-Path $modSourceDir "$modName.asmdef"

# --- Check prerequisites --------------------------------------------------------
if (-not (Test-Path $sdkRoot)) { throw "ModSDK が見つからない: $sdkRoot" }
if (-not (Test-Path $modSourceDir)) { throw "MOD ソースが見つからない: $modSourceDir" }
if (-not (Test-Path $gameAssemblyDir)) {
    throw "ゲームアセンブリが未取り込み: $gameAssemblyDir`nSDK の Update SDK タブで「Update Game Files」を先に実行すること。"
}

$gameDlls = @(Get-ChildItem $gameAssemblyDir -Recurse -Filter *.dll)
if ($gameDlls.Count -eq 0) {
    throw "ゲームアセンブリが0件。「Update Game Files」が完了していない可能性がある。"
}

# --- 1. asmdef ---------------------------------------------------------------
# Mirrors the structure produced by the SDK's ModBuilderWindow.CreateNewMod.
# precompiledReferences lists every imported DLL, matching what the SDK itself does.
$sdkDlls = @(if (Test-Path $sdkAssemblyDir) { Get-ChildItem $sdkAssemblyDir -Recurse -Filter *.dll } else { @() })
$precompiled = @(($gameDlls + $sdkDlls) | ForEach-Object { $_.Name } | Sort-Object -Unique)

$references = @(
    'Unity.Burst', 'Unity.Collections', 'Unity.Entities', 'Unity.Entities.Hybrid',
    'Unity.Jobs', 'Unity.Mathematics', 'Unity.NetCode', 'Unity.NetCode.Physics',
    'Unity.Networking.Transport', 'Unity.Physics', 'Unity.Physics.Hybrid',
    'Unity.Properties', 'Unity.Transforms', 'PugMod.SDK'
)

function ConvertTo-JsonArray([string[]]$values) {
    if ($values.Count -eq 0) { return '[]' }
    '["' + (($values | ForEach-Object { $_.Replace('\', '\\').Replace('"', '\"') }) -join '","') + '"]'
}

$asmdef = '{"name":"' + $modName + '"' +
',"references":' + (ConvertTo-JsonArray $references) +
',"includePlatforms":[],"excludePlatforms":[]' +
',"allowUnsafeCode":false,"overrideReferences":true' +
',"precompiledReferences":' + (ConvertTo-JsonArray $precompiled) +
',"autoReferenced":false,"defineConstraints":[],"versionDefines":[],"useGUIDs":false}'

[System.IO.File]::WriteAllText($asmdefPath, $asmdef, (New-Object System.Text.UTF8Encoding($false)))
Write-Output "asmdef を生成: $asmdefPath (参照 DLL $($precompiled.Count) 件)"

# --- 2. ModBuilderSettings ---------------------------------------------------
# Reuse the existing guid. A new identifier on every regeneration would
# because that would break its identity on the Workshop.
$modGuid = $null
if (Test-Path $settingsPath) {
    $existing = [System.IO.File]::ReadAllText($settingsPath)
    $m = [regex]::Match($existing, 'guid:\s*([0-9a-fA-F]{32})\s*[\r\n]')
    if ($m.Success) { $modGuid = $m.Groups[1].Value }
}
if (-not $modGuid) { $modGuid = [guid]::NewGuid().ToString('N') }

# The m_Script guid is that of the SDK's ModBuilderSettings.cs (a fixed value)
$settingsYaml = @"
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!114 &11400000
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 0}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {fileID: 11500000, guid: bc43e4983a160e543856e5ba0421c9e1, type: 3}
  m_Name: $modName
  m_EditorClassIdentifier:
  metadata:
    guid: $modGuid
    name: $modName
    displayName: $modDisplayName
    skipSafetyChecks: 0
    disableScripts: 0
    accessesExtraAssemblies: 1
    disableHarmonyPatching: 0
    requiredOn: 0
    files: []
    dependencies: []
  modPath: Assets\$modName
  forceReimport: 1
  buildBundles: 1
  cacheBundles: 0
  buildLinux: 1
  assets: []
  lastBuildLinux: 0
"@

[System.IO.File]::WriteAllText($settingsPath, $settingsYaml, (New-Object System.Text.UTF8Encoding($false)))
Write-Output "ビルド設定を生成: $settingsPath (mod guid: $modGuid)"

# --- 3. Junction -------------------------------------------------------------
# The SDK copy is disposable (git-ignored); the repository owns the sources.
if (Test-Path $junctionPath) {
    $item = Get-Item $junctionPath -Force
    if ($item.LinkType -eq 'Junction') {
        # Delete the link itself, never its contents. Remove-Item without -Recurse refuses a
        # junction whose target has children, which made every re-run of this script fail;
        # Directory.Delete removes the reparse point and leaves the target untouched.
        [System.IO.Directory]::Delete($junctionPath, $false)
    }
    else {
        throw "$junctionPath が実体のフォルダとして存在する。中身を確認して手動で退避すること。"
    }
}

New-Item -ItemType Junction -Path $junctionPath -Target $modSourceDir | Out-Null
Write-Output "ジャンクションを作成: $junctionPath -> $modSourceDir"

Write-Output ''
Write-Output '完了。Unity エディタにフォーカスを移すと再インポートとコンパイルが走る。'
