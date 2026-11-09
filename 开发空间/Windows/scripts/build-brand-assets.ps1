<#
.SYNOPSIS
Rebuild Qingyue's Windows icon using only Windows WPF and System.Drawing.
.EXAMPLE
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File .\scripts\build-brand-assets.ps1
.NOTES
Qingyue.xaml is the vector master. Default output is src/Qingyue.Windows/Assets:
Qingyue.png (256), Qingyue-512.png, Qingyue.ico and Qingyue-preview.png.
ICO uses 32-bit DIB frames at 16/24/32/48/64/128 and a PNG frame at 256.
No network, external tools, fonts in the icon, or installed packages are needed.
#>
[CmdletBinding()]
param(
    [string]$SourcePath,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$windowsRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = Join-Path $windowsRoot 'src\Qingyue.Windows\Assets\Qingyue.xaml'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $windowsRoot 'src\Qingyue.Windows\Assets'
}
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'WPF rendering requires STA. Run powershell.exe -NoProfile -STA -File build-brand-assets.ps1.'
}
Add-Type -AssemblyName WindowsBase, PresentationCore, PresentationFramework, System.Drawing

$source = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText((Resolve-Path -LiteralPath $SourcePath).Path))
if ($source -isnot [Windows.Media.DrawingImage] -or $source.Width -ne 512 -or $source.Height -ne 512) {
    throw 'The vector source must be a DrawingImage with a 512 x 512 artboard.'
}
$source.Freeze()
$null = [IO.Directory]::CreateDirectory($OutputDirectory)
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

function New-BrandBitmap([int]$Size) {
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    try {
        $context.DrawImage($source, (New-Object Windows.Rect 0, 0, $Size, $Size))
    } finally {
        $context.Close()
    }
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $Size, $Size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $bitmap.Freeze()
    return $bitmap
}

function ConvertTo-PngBytes([Windows.Media.Imaging.BitmapSource]$Bitmap) {
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))
    $stream = New-Object IO.MemoryStream
    try {
        $encoder.Save($stream)
        return ,$stream.ToArray()
    } finally {
        $stream.Dispose()
    }
}

function ConvertTo-IconDib([Windows.Media.Imaging.BitmapSource]$Bitmap) {
    $size = $Bitmap.PixelWidth
    # ICO stores straight BGRA, bottom-up rows and a DWORD-aligned transparency mask.
    $straight = New-Object Windows.Media.Imaging.FormatConvertedBitmap $Bitmap, ([Windows.Media.PixelFormats]::Bgra32), $null, 0
    $stride = $size * 4
    $pixels = New-Object byte[] ($stride * $size)
    $straight.CopyPixels($pixels, $stride, 0)
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $stream = New-Object IO.MemoryStream
    $writer = New-Object IO.BinaryWriter $stream
    try {
        $writer.Write([uint32]40)
        $writer.Write([int32]$size)
        $writer.Write([int32]($size * 2))
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]0)
        $writer.Write([uint32](($stride + $maskStride) * $size))
        $writer.Write([int32]0)
        $writer.Write([int32]0)
        $writer.Write([uint32]0)
        $writer.Write([uint32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            $writer.Write($pixels, $y * $stride, $stride)
        }
        for ($y = $size - 1; $y -ge 0; $y--) {
            $mask = New-Object byte[] $maskStride
            for ($x = 0; $x -lt $size; $x++) {
                if ($pixels[$y * $stride + $x * 4 + 3] -eq 0) {
                    $index = [int][Math]::Floor($x / 8.0)
                    $mask[$index] = $mask[$index] -bor (0x80 -shr ($x % 8))
                }
            }
            $writer.Write($mask)
        }
        $writer.Flush()
        return ,$stream.ToArray()
    } finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @{}
$payloads = @{}
foreach ($size in $sizes) {
    $frames[$size] = New-BrandBitmap $size
    $payloads[$size] = if ($size -eq 256) { ConvertTo-PngBytes $frames[$size] } else { ConvertTo-IconDib $frames[$size] }
}
[IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'Qingyue.png'), (ConvertTo-PngBytes $frames[256]))
[IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'Qingyue-512.png'), (ConvertTo-PngBytes (New-BrandBitmap 512)))

$iconPath = Join-Path $OutputDirectory 'Qingyue.ico'
$iconStream = [IO.File]::Create($iconPath)
$iconWriter = New-Object IO.BinaryWriter $iconStream
try {
    $iconWriter.Write([uint16]0)
    $iconWriter.Write([uint16]1)
    $iconWriter.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    foreach ($size in $sizes) {
        $dimension = if ($size -eq 256) { 0 } else { $size }
        $iconWriter.Write([byte]$dimension)
        $iconWriter.Write([byte]$dimension)
        $iconWriter.Write([byte]0)
        $iconWriter.Write([byte]0)
        $iconWriter.Write([uint16]1)
        $iconWriter.Write([uint16]32)
        $iconWriter.Write([uint32]$payloads[$size].Length)
        $iconWriter.Write([uint32]$offset)
        $offset += $payloads[$size].Length
    }
    foreach ($size in $sizes) { $iconWriter.Write([byte[]]$payloads[$size]) }
} finally {
    $iconWriter.Dispose()
    $iconStream.Dispose()
}

# The proof sheet uses decoded ICO frames, including actual-size samples on both
# desktop tones. Nearest-neighbour enlargements expose the 16/32 px edge quality.
$proof = New-Object Drawing.Bitmap 1056, 640, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [Drawing.Graphics]::FromImage($proof)
$titleFont = New-Object Drawing.Font 'Segoe UI', 18, ([Drawing.FontStyle]::Bold)
$labelFont = New-Object Drawing.Font 'Segoe UI', 11
$darkBrush = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#172C45'))
$lightBrush = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#E2EDF7'))
$panelBrush = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#101C2D'))
$iconInput = [IO.File]::OpenRead($iconPath)
try {
    $graphics.Clear([Drawing.ColorTranslator]::FromHtml('#E9EEF5'))
    $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.DrawString('QINGYUE / WINDOWS ICON', $titleFont, $darkBrush, 32, 24)
    $graphics.DrawString('WPF vector master / 32-bit alpha / actual-size ICO frames', $labelFont, $darkBrush, 34, 64)
    $graphics.FillRectangle([Drawing.Brushes]::White, 24, 104, 320, 512)
    $graphics.FillRectangle([Drawing.Brushes]::White, 364, 104, 668, 236)
    $graphics.FillRectangle($panelBrush, 364, 360, 668, 256)
    $large = [Drawing.Image]::FromFile((Join-Path $OutputDirectory 'Qingyue.png'))
    try { $graphics.DrawImageUnscaled($large, 56, 144) } finally { $large.Dispose() }
    $graphics.DrawString('256 px / transparent PNG', $labelFont, $darkBrush, 56, 422)
    $decoder = New-Object Windows.Media.Imaging.IconBitmapDecoder $iconInput, ([Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat), ([Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    # WPF can reorder ICO frames; select by decoded dimensions, never index.
    $decodedFrames = @{}
    foreach ($frame in $decoder.Frames) { $decodedFrames[$frame.PixelWidth] = $frame }
    $centers = @(400, 468, 548, 640, 752, 920)
    for ($i = 0; $i -lt 6; $i++) {
        $size = $sizes[$i]
        $pngStream = New-Object IO.MemoryStream (,(ConvertTo-PngBytes $decodedFrames[$size]))
        $sample = [Drawing.Image]::FromStream($pngStream)
        try {
            $x = [int]($centers[$i] - $size / 2)
            $graphics.DrawImageUnscaled($sample, $x, [int](216 - $size / 2))
            $graphics.DrawImageUnscaled($sample, $x, [int](482 - $size / 2))
            $graphics.DrawString([string]$size, $labelFont, $darkBrush, $centers[$i] - 10, 300)
            $graphics.DrawString([string]$size, $labelFont, $lightBrush, $centers[$i] - 10, 568)
            if ($size -eq 16 -or $size -eq 32) {
                $zoomX = if ($size -eq 16) { 56 } else { 204 }
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
                $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::Half
                $graphics.DrawImage($sample, (New-Object Drawing.Rectangle $zoomX, 466, 96, 96), 0, 0, $size, $size, ([Drawing.GraphicsUnit]::Pixel))
                $graphics.DrawString(('{0} px / zoom' -f $size), $labelFont, $darkBrush, $zoomX, 580)
            }
        } finally {
            $sample.Dispose()
            $pngStream.Dispose()
        }
    }
    $proof.Save((Join-Path $OutputDirectory 'Qingyue-preview.png'), [Drawing.Imaging.ImageFormat]::Png)
} finally {
    $iconInput.Dispose()
    $graphics.Dispose()
    $proof.Dispose()
    $titleFont.Dispose()
    $labelFont.Dispose()
    $darkBrush.Dispose()
    $lightBrush.Dispose()
    $panelBrush.Dispose()
}

Get-Item -LiteralPath $iconPath, (Join-Path $OutputDirectory 'Qingyue.png'), (Join-Path $OutputDirectory 'Qingyue-512.png'), (Join-Path $OutputDirectory 'Qingyue-preview.png') |
    Select-Object FullName, Length
