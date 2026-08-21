# =============================================================================
# Measures where each part of the player character sits, frame by frame.
#
# The game keeps every layer of the character - bare skin, eyes, hair, shirt,
# trousers, helmet, chest armour, leg armour - as its own 234x156 sheet using
# the same frame grid as the body. Each part's position and size can therefore
# be measured directly by finding the opaque pixels inside a frame.
#
# ONLY THE MEASUREMENTS ARE KEPT. No game artwork is copied: the output holds
# rectangles and nothing else, which is what lets the tool line its own starter
# template up with the real character without redistributing anything.
#
# Requires AssetRipper output under extracted/ (see the project setup).
# =============================================================================

param(
    [string]$Extracted = (Join-Path (Split-Path $PSScriptRoot -Parent) 'extracted'),
    [string]$Output = (Join-Path (Split-Path $PSScriptRoot -Parent) 'data\player-parts.json')
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path $PSScriptRoot -Parent
$layoutPath = Join-Path $repoRoot 'data\player-body-layout.json'
if (-not (Test-Path $layoutPath)) { throw "レイアウト定義が無い: $layoutPath" }
$layout = Get-Content $layoutPath -Raw -Encoding UTF8 | ConvertFrom-Json

$art = Join-Path $Extracted 'ExportedProject\Assets\Art'
if (-not (Test-Path $art)) { throw "抽出したアセットが無い: $art" }

# One representative file per part. Armour varies per set, so a mid-tier set is
# used: the sets differ in decoration far more than in the area they cover.
$parts = [ordered]@{
    body        = 'Characters\Player Customization\Miner_skin.png'
    eyes        = 'Characters\Player Customization\Miner_eyes.png'
    hair        = 'Characters\Player Customization\Miner_hair1.png'
    shirt       = 'Characters\Player Customization\Miner_shirt.png'
    pants       = 'Characters\Player Customization\Miner_pants.png'
    helm        = 'Items\Armor\Copper Set\copperHelm.png'
    chestArmor  = 'Items\Armor\Copper Set\copperChest.png'
    pantsArmor  = 'Items\Armor\Copper Set\copperPants.png'
}

function Get-PartBounds {
    param([System.Drawing.Bitmap]$Bitmap, $Frame)

    $minX = [int]::MaxValue; $maxX = -1
    $minY = [int]::MaxValue; $maxY = -1

    for ($y = 0; $y -lt $Frame.h; $y++) {
        for ($x = 0; $x -lt $Frame.w; $x++) {
            if ($Bitmap.GetPixel($Frame.x + $x, $Frame.yTopLeft + $y).A -gt 0) {
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }

    if ($maxX -lt 0) { return $null }
    return [ordered]@{ x = $minX; y = $minY; w = $maxX - $minX + 1; h = $maxY - $minY + 1 }
}

Write-Output "レイアウト: $($layout.texture.width)x$($layout.texture.height) / コマ数 $($layout.frames.Count)"
Write-Output ''

$result = [ordered]@{
    note    = 'Measured from the game to line the starter template up with the real character. Rectangles only; contains no game artwork.'
    source  = 'Measured from the player sprite sheet in the game'
    version = $layout.gameVersion
    parts   = [ordered]@{}
}

foreach ($name in $parts.Keys) {
    $file = Join-Path $art $parts[$name]
    if (-not (Test-Path $file)) { throw "パーツ画像が無い: $file" }

    $bitmap = [System.Drawing.Bitmap]::FromFile($file)
    try {
        if ($bitmap.Width -ne $layout.texture.width -or $bitmap.Height -ne $layout.texture.height) {
            throw "$name の寸法がレイアウトと違う: $($bitmap.Width)x$($bitmap.Height)"
        }

        $frames = [ordered]@{}
        $present = 0
        foreach ($frame in $layout.frames) {
            $bounds = Get-PartBounds -Bitmap $bitmap -Frame $frame
            if ($null -ne $bounds) {
                $frames["$($frame.index)"] = $bounds
                $present++
            }
        }

        $result.parts[$name] = [ordered]@{
            source = ($parts[$name] -replace '\\', '/')
            frames = $frames
        }

        Write-Output ("  {0,-12} {1,2}/{2} コマに存在" -f $name, $present, $layout.frames.Count)
    }
    finally {
        $bitmap.Dispose()
    }
}

$json = $result | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($Output, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Output ''
Write-Output "書き出し: $Output ($([math]::Round((Get-Item $Output).Length / 1KB, 1)) KB)"
