param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "AppIcon.ico")
)

Add-Type -AssemblyName System.Drawing

$size = 256
$bitmap = [System.Drawing.Bitmap]::new($size, $size)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

$blue = [System.Drawing.Color]::FromArgb(255, 37, 99, 235)
$lightBlue = [System.Drawing.Color]::FromArgb(255, 96, 165, 250)
$white = [System.Drawing.Color]::White

$backgroundPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
$backgroundPath.AddArc(12, 12, 48, 48, 180, 90)
$backgroundPath.AddArc(196, 12, 48, 48, 270, 90)
$backgroundPath.AddArc(196, 196, 48, 48, 0, 90)
$backgroundPath.AddArc(12, 196, 48, 48, 90, 90)
$backgroundPath.CloseFigure()
$backgroundBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.Point]::new(32, 24),
    [System.Drawing.Point]::new(224, 232),
    $lightBlue,
    $blue)
$graphics.FillPath($backgroundBrush, $backgroundPath)

$ringPen = [System.Drawing.Pen]::new($white, 22)
$ringPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$ringPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$graphics.DrawArc($ringPen, 63, 61, 130, 130, 205, 274)

$arrowBrush = [System.Drawing.SolidBrush]::new($white)
$arrow = [System.Drawing.Point[]]@(
    [System.Drawing.Point]::new(192, 43),
    [System.Drawing.Point]::new(197, 99),
    [System.Drawing.Point]::new(145, 77)
)
$graphics.FillPolygon($arrowBrush, $arrow)

$downloadPen = [System.Drawing.Pen]::new($white, 18)
$downloadPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$downloadPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$graphics.DrawLine($downloadPen, 128, 102, 128, 169)
$downloadArrow = [System.Drawing.Point[]]@(
    [System.Drawing.Point]::new(91, 151),
    [System.Drawing.Point]::new(128, 191),
    [System.Drawing.Point]::new(165, 151)
)
$graphics.FillPolygon($arrowBrush, $downloadArrow)

$pngStream = [System.IO.MemoryStream]::new()
$bitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $pngStream.ToArray()

$outputDirectory = [System.IO.Path]::GetDirectoryName($OutputPath)
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$file = [System.IO.File]::Create($OutputPath)
$writer = [System.IO.BinaryWriter]::new($file)

# ICO header with one 256x256 PNG image.
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]1)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([uint16]1)
$writer.Write([uint16]32)
$writer.Write([uint32]$pngBytes.Length)
$writer.Write([uint32]22)
$writer.Write($pngBytes)

$writer.Dispose()
$file.Dispose()
$pngStream.Dispose()
$downloadPen.Dispose()
$arrowBrush.Dispose()
$ringPen.Dispose()
$backgroundBrush.Dispose()
$backgroundPath.Dispose()
$graphics.Dispose()
$bitmap.Dispose()

Write-Output "Generated $OutputPath"
