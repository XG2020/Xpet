$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$xpetRoot = Split-Path $PSScriptRoot -Parent
$xpetBrand = Join-Path $xpetRoot 'branding'
New-Item -ItemType Directory -Path $xpetBrand -Force | Out-Null
function New-XpetBitmap([int]$size) {
    $bitmap = [Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.ScaleTransform($size/256.0,$size/256.0)
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc(8,8,64,64,180,90); $path.AddArc(184,8,64,64,270,90)
    $path.AddArc(184,184,64,64,0,90); $path.AddArc(8,184,64,64,90,90); $path.CloseFigure()
    $gradient = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.Point]::new(20,10),[Drawing.Point]::new(236,246),[Drawing.ColorTranslator]::FromHtml('#244C60'),[Drawing.ColorTranslator]::FromHtml('#0D1526'))
    $graphics.FillPath($gradient,$path)
    $outline = [Drawing.Pen]::new([Drawing.Color]::FromArgb(70,127,231,212),2)
    $graphics.DrawPath($outline,$path)
    $mint = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#84EFD4'))
    $graphics.FillEllipse($mint,42,71,38,47); $graphics.FillEllipse($mint,85,43,38,52)
    $graphics.FillEllipse($mint,133,43,38,52); $graphics.FillEllipse($mint,176,71,38,47)
    $palm = [Drawing.Drawing2D.GraphicsPath]::new()
    $palm.AddBezier(128,104,107,102,104,123,81,143)
    $palm.AddBezier(81,143,61,161,62,190,82,199)
    $palm.AddBezier(82,199,97,207,112,197,128,197)
    $palm.AddBezier(128,197,144,197,159,207,174,199)
    $palm.AddBezier(174,199,194,190,195,161,175,143)
    $palm.AddBezier(175,143,152,123,149,102,128,104); $palm.CloseFigure()
    $graphics.FillPath($mint,$palm)
    $mark = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#15364A'),12)
    $mark.StartCap = $mark.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($mark,111,146,145,179); $graphics.DrawLine($mark,145,146,111,179)
    $graphics.Dispose(); $path.Dispose(); $palm.Dispose(); $gradient.Dispose(); $outline.Dispose(); $mint.Dispose(); $mark.Dispose()
    return $bitmap
}
$iconImages = foreach ($xpetSize in @(16,24,32,48,64,128,256)) {
    $bitmap = New-XpetBitmap $xpetSize
    $memory = [IO.MemoryStream]::new(); $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
    [pscustomobject]@{size=$xpetSize;data=$memory.ToArray()}
    $bitmap.Dispose(); $memory.Dispose()
}
$output = [IO.File]::Create((Join-Path $xpetBrand 'Xpet.ico'))
$writer = [IO.BinaryWriter]::new($output)
$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$iconImages.Count)
$offset = 6 + 16 * $iconImages.Count
foreach ($iconImage in $iconImages) {
    $dimension = if ($iconImage.size -eq 256) {0} else {$iconImage.size}
    $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
    $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$iconImage.data.Length);$writer.Write([uint32]$offset)
    $offset += $iconImage.data.Length
}
foreach ($iconImage in $iconImages) { $writer.Write([byte[]]$iconImage.data) }
$writer.Dispose(); $output.Dispose()
$preview = New-XpetBitmap 512
$preview.Save((Join-Path $xpetBrand 'Xpet.png'),[Drawing.Imaging.ImageFormat]::Png);$preview.Dispose()
$assetFolder = Join-Path $xpetRoot 'pet-frontend\pet-frontend\Assets\Branding'
New-Item -ItemType Directory -Path $assetFolder -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $xpetBrand 'Xpet.png') -Destination $assetFolder -Force
