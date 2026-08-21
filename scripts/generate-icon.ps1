# =============================================================================
# Draws the application icon and writes it as a multi-size .ico.
#
# Kept as a script rather than a checked-in binary nobody can edit: the icon is a
# few shapes and some text, and this is the only record of what they are.
#
# Output:
#   src/gui/Assets/icon.ico   - the window and taskbar icon, and the exe's icon
#   src/gui/Assets/icon.png   - the same mark at 256px, for anything that wants a bitmap
#
# The mark is "CK" over "MOD" on the same dark ground the editor canvas uses, in the
# skin tone the presets start from. At 16 pixels only "CK" is drawn: three letters and a
# word underneath turn into mud at that size.
# =============================================================================

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $repoRoot 'src\gui\Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

# Same colours as the editor: the checker behind the canvas, and the default skin tone
$background = [System.Drawing.Color]::FromArgb(255, 26, 26, 32)
$panel = [System.Drawing.Color]::FromArgb(255, 58, 58, 70)
$ink = [System.Drawing.Color]::FromArgb(255, 232, 192, 154)
$accent = [System.Drawing.Color]::FromArgb(255, 214, 84, 84)

function New-IconBitmap {
    param([int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias

    try {
        $g.Clear([System.Drawing.Color]::Transparent)

        # Rounded ground, inset a little so the corners are not clipped by the frame
        $inset = [Math]::Max(1, [int]($Size * 0.04))
        $radius = [Math]::Max(2, [int]($Size * 0.22))
        $rect = New-Object System.Drawing.Rectangle($inset, $inset, ($Size - ($inset * 2)), ($Size - ($inset * 2)))

        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $radius * 2
        $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
        $path.AddArc(($rect.Right - $d), $rect.Y, $d, $d, 270, 90)
        $path.AddArc(($rect.Right - $d), ($rect.Bottom - $d), $d, $d, 0, 90)
        $path.AddArc($rect.X, ($rect.Bottom - $d), $d, $d, 90, 90)
        $path.CloseFigure()

        $brush = New-Object System.Drawing.SolidBrush($background)
        $g.FillPath($brush, $path)
        $brush.Dispose()

        # A thin lit edge, the same cue the character art uses to read as three-dimensional
        $penWidth = [Math]::Max(1.0, $Size * 0.03)
        $pen = New-Object System.Drawing.Pen($panel, $penWidth)
        $g.DrawPath($pen, $path)
        $pen.Dispose()
        $path.Dispose()

        $small = $Size -lt 32

        # "CK" fills the mark on its own at small sizes, and sits above "MOD" otherwise
        $ckSize = if ($small) { $Size * 0.52 } else { $Size * 0.42 }
        $ckFont = New-Object System.Drawing.Font('Segoe UI', $ckSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)

        $format = New-Object System.Drawing.StringFormat
        $format.Alignment = [System.Drawing.StringAlignment]::Center
        $format.LineAlignment = [System.Drawing.StringAlignment]::Center

        $inkBrush = New-Object System.Drawing.SolidBrush($ink)

        if ($small) {
            $g.DrawString('CK', $ckFont, $inkBrush, (New-Object System.Drawing.RectangleF(0, 0, $Size, $Size)), $format)
        }
        else {
            $ckBox = New-Object System.Drawing.RectangleF(0, ($Size * -0.06), $Size, $Size)
            $g.DrawString('CK', $ckFont, $inkBrush, $ckBox, $format)

            # A short bar under the letters, then MOD on it, so the word reads as a label
            $barHeight = [Math]::Max(1, [int]($Size * 0.26))
            $barWidth = [int]($Size * 0.62)
            $barX = [int](($Size - $barWidth) / 2)
            $barY = [int]($Size * 0.60)

            $accentBrush = New-Object System.Drawing.SolidBrush($accent)
            $g.FillRectangle($accentBrush, $barX, $barY, $barWidth, $barHeight)
            $accentBrush.Dispose()

            $modFont = New-Object System.Drawing.Font('Segoe UI', ($Size * 0.17), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            $modBox = New-Object System.Drawing.RectangleF($barX, $barY, $barWidth, $barHeight)
            $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
            $g.DrawString('MOD', $modFont, $white, $modBox, $format)
            $white.Dispose()
            $modFont.Dispose()
        }

        $inkBrush.Dispose()
        $format.Dispose()
        $ckFont.Dispose()
    }
    finally {
        $g.Dispose()
    }

    return $bitmap
}

# Every size below 256 is stored the old way, as a bottom-up 32-bit DIB with an and-mask.
# PNG-compressed entries are legal from Vista onwards and are smaller, but GDI+ decodes them
# unreliably - loading one back through System.Drawing.Icon produced noise - and anything
# that renders an icon through GDI+ rather than the shell would show that noise. Only the
# 256-pixel entry is PNG, which is the one case every tool expects to be compressed.
function ConvertTo-DibEntry {
    param([System.Drawing.Bitmap]$Bitmap)

    $size = $Bitmap.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    try {
        $rowBytes = $size * 4
        $pixels = New-Object byte[] ($rowBytes * $size)

        # Rows come out top-down and a DIB stores them bottom-up, so they are copied in reverse
        for ($y = 0; $y -lt $size; $y++) {
            $source = [IntPtr]::Add($data.Scan0, $data.Stride * ($size - 1 - $y))
            [System.Runtime.InteropServices.Marshal]::Copy($source, $pixels, $rowBytes * $y, $rowBytes)
        }
    }
    finally {
        $Bitmap.UnlockBits($data)
    }

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)

    # BITMAPINFOHEADER. The height is doubled because the and-mask counts as part of the image.
    $writer.Write([UInt32]40)
    $writer.Write([Int32]$size)
    $writer.Write([Int32]($size * 2))
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]0)              # BI_RGB
    $writer.Write([UInt32]0)              # size of image data, may be zero for BI_RGB
    $writer.Write([Int32]0)
    $writer.Write([Int32]0)
    $writer.Write([UInt32]0)
    $writer.Write([UInt32]0)

    $writer.Write($pixels)

    # And-mask: 1 bit per pixel, rows padded to four bytes. Left at zero throughout, so the
    # alpha channel above decides transparency, which is what 32-bit entries are read for.
    $maskRow = [int][Math]::Ceiling($size / 32.0) * 4
    $writer.Write((New-Object byte[] ($maskRow * $size)))

    $writer.Flush()
    $bytes = $stream.ToArray()
    $writer.Dispose()
    $stream.Dispose()

    # The leading comma matters. Returning the array bare makes PowerShell send its elements
    # down the pipeline one at a time, and the caller gets an Object[] of boxed bytes that
    # BinaryWriter will not write - which produced an .ico holding only its own headers.
    return , $bytes
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()

foreach ($size in $sizes) {
    $bitmap = New-IconBitmap -Size $size

    if ($size -ge 256) {
        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $stream.ToArray()
        $stream.Dispose()

        [System.IO.File]::WriteAllBytes((Join-Path $assets 'icon.png'), $bytes)
    }
    else {
        $bytes = ConvertTo-DibEntry -Bitmap $bitmap
    }

    $images += , @{ Size = $size; Bytes = $bytes }
    $bitmap.Dispose()
}

$icoPath = Join-Path $assets 'icon.ico'
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)

# ICONDIR
$writer.Write([UInt16]0)                 # reserved
$writer.Write([UInt16]1)                 # type: icon
$writer.Write([UInt16]$images.Count)

# Each entry is 16 bytes; the image data follows all of them
$offset = 6 + (16 * $images.Count)

foreach ($image in $images) {
    # 0 means 256 in this field, which is why it is a single byte
    $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }

    $writer.Write([Byte]$dimension)       # width
    $writer.Write([Byte]$dimension)       # height
    $writer.Write([Byte]0)                # palette entries: none, this is true colour
    $writer.Write([Byte]0)                # reserved
    $writer.Write([UInt16]1)              # colour planes
    $writer.Write([UInt16]32)             # bits per pixel
    $writer.Write([UInt32]$image.Bytes.Length)
    $writer.Write([UInt32]$offset)

    $offset += $image.Bytes.Length
}

foreach ($image in $images) {
    $writer.Write($image.Bytes)
}

$writer.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
$writer.Dispose()
$out.Dispose()

Write-Output ("作成: {0} ({1} KB, {2} サイズ)" -f $icoPath, [math]::Round((Get-Item -LiteralPath $icoPath).Length / 1KB, 1), $images.Count)
Write-Output ("作成: {0}" -f (Join-Path $assets 'icon.png'))
