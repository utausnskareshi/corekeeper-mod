# =============================================================================
# Generates data/player-body-layout.json.
#
# Records the sprite sheet layout of Core Keeper's player body layers (the Miner_skin.png family)
# by reading the game assets extracted with AssetRipper and writing them out as JSON.
# If a game update changes the sheet layout, re-extract the assets and run this script again.
#
# Prerequisites:
#   1. Core Keeper has been exported to extracted/ExportedProject with AssetRipper 1.2.1
#   2. Run under Windows PowerShell 5.1 (this file must be saved as UTF-8 with BOM)
#
# The generated JSON holds coordinates only; it never contains the game's image data.
# =============================================================================

$src = Join-Path $PSScriptRoot '..\extracted\ExportedProject\Assets\Art\Characters\Player Customization'
$dst = Join-Path $PSScriptRoot '..\data\player-body-layout.json'
$TEX_W = 234
$TEX_H = 156
$FRAME_COUNT = 39

if (-not (Test-Path $src)) {
    throw "抽出済みアセットが見つからない: $src`n先に AssetRipper でエクスポートすること。"
}

# --- Frame index -> animation name / direction / frame within the animation ----
# This mapping was determined by matching which Miner_skin_N sprites the Miner*.anim
# animation clips reference. D = down (front), R = right (side), U = up (back).
$sem = @{}
function Set-Sem([int]$i, [string]$a, [string]$d, [int]$f) { $script:sem[$i] = @($a, $d, $f) }

Set-Sem 0 'idle' 'down' 0; Set-Sem 1 'idle' 'right' 0; Set-Sem 2 'idle' 'up' 0
for ($k = 0; $k -lt 6; $k++) { Set-Sem (3 + $k)  'run' 'down'  $k }   # 走行6コマ（正面）
for ($k = 0; $k -lt 6; $k++) { Set-Sem (9 + $k)  'run' 'right' $k }   # 走行6コマ（横）
for ($k = 0; $k -lt 6; $k++) { Set-Sem (15 + $k) 'run' 'up'    $k }   # 走行6コマ（背面）
Set-Sem 21 'swing' 'down' 0;  Set-Sem 22 'swing' 'down' 1             # 攻撃・採掘・設置など
Set-Sem 23 'swing' 'right' 0; Set-Sem 24 'swing' 'right' 1
Set-Sem 25 'swing' 'up' 0;    Set-Sem 26 'swing' 'up' 1
Set-Sem 27 'aim' 'down' 0;  Set-Sem 28 'aim' 'right' 0;  Set-Sem 29 'aim' 'up' 0   # 溜め・釣り構え
Set-Sem 30 'hold' 'down' 0; Set-Sem 31 'hold' 'right' 0; Set-Sem 32 'hold' 'up' 0  # 運転・釣り・演奏
Set-Sem 33 'sit' 'down' 0;  Set-Sem 34 'sit' 'right' 0;  Set-Sem 35 'sit' 'up' 0   # 着席
Set-Sem 36 'sitArmsInFront' 'down' 0                                               # 着席（腕を前に）
Set-Sem 37 'sitArmsInFront' 'right' 0
Set-Sem 38 'sitArmsInFront' 'up' 0

# --- Read each sprite rectangle from the Sprite asset (YAML) -------------------
$rx = 'm_Rect:\s*\r?\n\s*serializedVersion:\s*\d+\s*\r?\n\s*x:\s*([-\d\.]+)\s*\r?\n\s*y:\s*([-\d\.]+)\s*\r?\n\s*width:\s*([-\d\.]+)\s*\r?\n\s*height:\s*([-\d\.]+)'
$frames = New-Object System.Collections.Generic.List[psobject]

foreach ($f in (Get-ChildItem $src -Filter 'Miner_skin_*.asset' | Sort-Object { [int]($_.BaseName -replace '.*_', '') })) {
    $i = [int]($f.BaseName -replace '.*_', '')
    $m = [regex]::Match((Get-Content $f.FullName -Raw), $rx)
    if (-not $m.Success) { throw "m_Rect を読み取れなかった: $($f.Name)" }
    if (-not $sem.ContainsKey($i)) { throw "コマ番号 $i に対応するアニメーション定義がない（シート構成が変わった可能性）" }

    $uy = [int]$m.Groups[2].Value   # Unity のスプライト座標は左下原点
    $h = [int]$m.Groups[4].Value
    $frames.Add([pscustomobject]@{
            index    = $i
            anim     = $sem[$i][0]
            dir      = $sem[$i][1]
            frame    = $sem[$i][2]
            x        = [int]$m.Groups[1].Value
            yUnity   = $uy
            yTopLeft = $TEX_H - $uy - $h   # 画像編集ソフト向けの左上原点 Y
            w        = [int]$m.Groups[3].Value
            h        = $h
        })
}

if ($frames.Count -ne $FRAME_COUNT) {
    throw "コマ数が想定と異なる: 期待 $FRAME_COUNT / 実際 $($frames.Count)（ゲーム更新でシート構成が変わった可能性）"
}

# --- Measure the area actually covered by art inside each cell -----------------
# This gives the reference for placing replacement art so that it does not look out of place.
#   contentBox  : union across every frame; drawing outside it looks out of scale
#   standingBox : union of the three idle frames; the standing reference, including the foot line
Add-Type -AssemblyName System.Drawing
$texPath = Join-Path $src 'Miner_skin.png'
if (-not (Test-Path $texPath)) { throw "テクスチャが見つからない: $texPath" }
$bmp = [System.Drawing.Bitmap]::FromFile($texPath)
try {
    function Measure-Box($targetFrames) {
        $minX = [int]::MaxValue; $minY = [int]::MaxValue; $maxX = -1; $maxY = -1
        foreach ($fr in $targetFrames) {
            for ($cy = 0; $cy -lt $fr.h; $cy++) {
                for ($cx = 0; $cx -lt $fr.w; $cx++) {
                    if ($bmp.GetPixel($fr.x + $cx, $fr.yTopLeft + $cy).A -eq 0) { continue }
                    if ($cx -lt $minX) { $minX = $cx }
                    if ($cx -gt $maxX) { $maxX = $cx }
                    if ($cy -lt $minY) { $minY = $cy }
                    if ($cy -gt $maxY) { $maxY = $cy }
                }
            }
        }
        if ($maxX -lt 0) { throw '不透明ピクセルが1つも見つからない（テクスチャが想定と異なる）' }
        [pscustomobject]@{ x = $minX; y = $minY; w = ($maxX - $minX + 1); h = ($maxY - $minY + 1); bottom = $maxY }
    }
    $contentBox = Measure-Box $frames
    $standingBox = Measure-Box ($frames | Where-Object { $_.anim -eq 'idle' })
}
finally { $bmp.Dispose() }

# --- Emit JSON by hand; ConvertTo-Json on PS 5.1 formats it unreadably ---------
function Esc([string]$s) { $s.Replace('\', '\\').Replace('"', '\"') }

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('{')
[void]$sb.AppendLine('  "_note": "' + (Esc 'Core Keeper プレイヤー体レイヤーのスプライトシート配置。ゲーム本体から座標情報のみを抽出した派生データで、画像そのものは含まない。') + '",')
[void]$sb.AppendLine('  "schemaVersion": 1,')
[void]$sb.AppendLine('  "gameVersion": "1.2.1.5-8be0",')
[void]$sb.AppendLine('  "unityVersion": "6000.0.59f2",')
[void]$sb.AppendLine('  "sourceTexture": "Miner_skin.png",')
[void]$sb.AppendLine('  "appliesTo": ["skin", "eyes", "shirt", "pants", "hair", "hairShade"],')
[void]$sb.AppendLine('  "texture": { "width": ' + $TEX_W + ', "height": ' + $TEX_H + ' },')
[void]$sb.AppendLine('  "cell": { "width": 26, "height": 26 },')
[void]$sb.AppendLine('  "pivot": { "x": 0.5, "y": 0.5 },')
[void]$sb.AppendLine('  "pixelsPerUnit": 16,')
[void]$sb.AppendLine('  "frameCount": ' + $frames.Count + ',')
[void]$sb.AppendLine('  "contentBox": {')
[void]$sb.AppendLine('    "_note": "' + (Esc '全39コマで実際に絵が乗っている範囲の和集合。セル左上を原点とする。ここを超える絵は他のコマとサイズ感がずれる。') + '",')
[void]$sb.AppendLine('    "x": ' + $contentBox.x + ', "y": ' + $contentBox.y + ', "width": ' + $contentBox.w + ', "height": ' + $contentBox.h)
[void]$sb.AppendLine('  },')
[void]$sb.AppendLine('  "standingBox": {')
[void]$sb.AppendLine('    "_note": "' + (Esc '待機3コマの占有範囲。差し替え画像の既定の配置基準。baselineY は足元の1つ下（下端の排他境界）。') + '",')
[void]$sb.AppendLine('    "x": ' + $standingBox.x + ', "y": ' + $standingBox.y + ', "width": ' + $standingBox.w + ', "height": ' + $standingBox.h + ',')
[void]$sb.AppendLine('    "baselineY": ' + ($standingBox.bottom + 1) + ', "centerX": ' + [int]([math]::Round(26 / 2)))
[void]$sb.AppendLine('  },')
[void]$sb.AppendLine('  "directions": {')
[void]$sb.AppendLine('    "_note": "' + (Esc 'left 方向は right のコマを水平反転して描画されるため専用コマは存在しない。左右非対称な絵は左向き時に鏡像になる。') + '",')
[void]$sb.AppendLine('    "available": ["down", "right", "up"],')
[void]$sb.AppendLine('    "mirroredFromRight": "left"')
[void]$sb.AppendLine('  },')

# Animation name -> ordered frame indices
$animNames = $frames | ForEach-Object { "$($_.anim)_$($_.dir)" } | Select-Object -Unique | Sort-Object
[void]$sb.AppendLine('  "animations": {')
for ($n = 0; $n -lt $animNames.Count; $n++) {
    $name = $animNames[$n]
    $idxs = @($frames | Where-Object { "$($_.anim)_$($_.dir)" -eq $name } | Sort-Object { $_.frame } | ForEach-Object { $_.index })
    $comma = if ($n -lt $animNames.Count - 1) { ',' } else { '' }
    [void]$sb.AppendLine('    "' + $name + '": [' + ($idxs -join ', ') + ']' + $comma)
}
[void]$sb.AppendLine('  },')

# Frame definitions, one per line
[void]$sb.AppendLine('  "frames": [')
for ($n = 0; $n -lt $frames.Count; $n++) {
    $fr = $frames[$n]
    $comma = if ($n -lt $frames.Count - 1) { ',' } else { '' }
    [void]$sb.AppendLine(('    {{ "index": {0}, "anim": "{1}", "dir": "{2}", "frame": {3}, "x": {4}, "yUnity": {5}, "yTopLeft": {6}, "w": {7}, "h": {8} }}{9}' -f `
                $fr.index, $fr.anim, $fr.dir, $fr.frame, $fr.x, $fr.yUnity, $fr.yTopLeft, $fr.w, $fr.h, $comma))
}
[void]$sb.AppendLine('  ]')
[void]$sb.AppendLine('}')

New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null

# Left alone when nothing but the verifiedVersions line differs. This script does not write that
# line, and it records the builds the numbers were checked against: running it to check a new
# game version, as data/supported-versions.json says to, used to wipe the record it was checking.
# When the numbers do differ the file is rewritten as before - the old record no longer holds.
$newText = $sb.ToString()
$unchanged = $false
if (Test-Path $dst) {
    $oldText = [System.IO.File]::ReadAllText($dst, (New-Object System.Text.UTF8Encoding($false)))
    $stripped = [regex]::Replace($oldText, '(?m)^[ \t]*"verifiedVersions":.*\r?\n', '')
    $unchanged = ($stripped -ne $oldText) -and (($stripped -replace "`r`n", "`n") -eq ($newText -replace "`r`n", "`n"))
}
if ($unchanged) {
    Write-Output "実測値は既存の定義と同じ。verifiedVersions を保つため書き換えない。確かめた版を verifiedVersions に足すこと。"
}
else {
    # Write JSON as UTF-8 without BOM so that C# and Unity read it without special handling
    [System.IO.File]::WriteAllText($dst, $newText, (New-Object System.Text.UTF8Encoding($false)))
}

# --- Validate the generated output ----------------------------------------------
# Get-Content mistakes BOM-less UTF-8 for ANSI, so read it back as UTF-8 explicitly
$raw = [System.IO.File]::ReadAllText($dst, (New-Object System.Text.UTF8Encoding($false)))
$check = $raw | ConvertFrom-Json

$errors = New-Object System.Collections.Generic.List[string]
$seen = @{}
foreach ($fr in $check.frames) {
    $key = "$($fr.x),$($fr.yUnity)"
    if ($seen.ContainsKey($key)) { $errors.Add("矩形が重複している: ($key)") }
    $seen[$key] = $true
    if ($fr.x -lt 0 -or $fr.yUnity -lt 0 -or ($fr.x + $fr.w) -gt $TEX_W -or ($fr.yUnity + $fr.h) -gt $TEX_H) {
        $errors.Add("矩形がテクスチャ外にはみ出している: index $($fr.index)")
    }
}
if ($errors.Count -gt 0) { throw ($errors -join "`n") }

Write-Output "生成完了: $dst"
Write-Output "  コマ数         = $($check.frames.Count)"
Write-Output "  アニメーション = $(($check.animations.PSObject.Properties | Measure-Object).Count) 種"
Write-Output "  テクスチャ     = $($check.texture.width) x $($check.texture.height)"
