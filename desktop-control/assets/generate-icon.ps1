[CmdletBinding()]
param(
    [string]$OutputDirectory = $PSScriptRoot,

    [ValidateRange(2, 8)]
    [int]$Supersample = 4
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param(
        [single]$X,
        [single]$Y,
        [single]$Width,
        [single]$Height,
        [single]$Radius
    )

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-AppIconPng {
    param(
        [int]$Size,
        [int]$Scale
    )

    $renderSize = $Size * $Scale
    $renderBitmap = [System.Drawing.Bitmap]::new(
        $renderSize,
        $renderSize,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($renderBitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $factor = [single]($renderSize / 256.0)
        $graphics.ScaleTransform($factor, $factor)

        $outerPath = New-RoundedRectanglePath 8 8 240 240 56
        $outerBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.ColorTranslator]::FromHtml("#0F172A"))
        try {
            $graphics.FillPath($outerBrush, $outerPath)
        } finally {
            $outerBrush.Dispose()
            $outerPath.Dispose()
        }

        $bubblePath = New-RoundedRectanglePath 42 50 172 136 30
        $tailPoints = [System.Drawing.PointF[]]@(
            [System.Drawing.PointF]::new(76, 164),
            [System.Drawing.PointF]::new(76, 222),
            [System.Drawing.PointF]::new(134, 178)
        )
        $bubbleBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.ColorTranslator]::FromHtml("#F8FAFC"))
        try {
            $graphics.FillPolygon($bubbleBrush, $tailPoints)
            $graphics.FillPath($bubbleBrush, $bubblePath)
        } finally {
            $bubbleBrush.Dispose()
            $bubblePath.Dispose()
        }

        $promptPen = [System.Drawing.Pen]::new(
            [System.Drawing.ColorTranslator]::FromHtml("#2563EB"),
            18)
        try {
            $promptPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $promptPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $promptPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            $graphics.DrawLines($promptPen, [System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new(78, 91),
                [System.Drawing.PointF]::new(109, 120),
                [System.Drawing.PointF]::new(78, 149)
            ))
        } finally {
            $promptPen.Dispose()
        }

        $statusPath = New-RoundedRectanglePath 124 141 53 17 8.5
        $statusBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.ColorTranslator]::FromHtml("#16A34A"))
        try {
            $graphics.FillPath($statusBrush, $statusPath)
        } finally {
            $statusBrush.Dispose()
            $statusPath.Dispose()
        }
    } finally {
        $graphics.Dispose()
    }

    $bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $downsample = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $downsample.Clear([System.Drawing.Color]::Transparent)
        $downsample.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $downsample.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $downsample.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $downsample.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $downsample.DrawImage(
            $renderBitmap,
            [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
            0,
            0,
            $renderSize,
            $renderSize,
            [System.Drawing.GraphicsUnit]::Pixel)
    } finally {
        $downsample.Dispose()
        $renderBitmap.Dispose()
    }

    $pngStream = [System.IO.MemoryStream]::new()
    try {
        $bitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$pngStream.ToArray()
    } finally {
        $bitmap.Dispose()
        $pngStream.Dispose()
    }
}

[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$entries = foreach ($size in $sizes) {
    [PSCustomObject]@{
        Size = $size
        Bytes = [byte[]](New-AppIconPng -Size $size -Scale $Supersample)
    }
}

$iconPath = Join-Path $OutputDirectory "ai-cli-feishu.ico"
$iconStream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($iconStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$entries.Count)

    $offset = 6 + (16 * $entries.Count)
    foreach ($entry in $entries) {
        $dimension = if ($entry.Size -eq 256) { [byte]0 } else { [byte]$entry.Size }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$entry.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $entry.Bytes.Length
    }
    foreach ($entry in $entries) {
        $writer.Write([byte[]]$entry.Bytes)
    }
} finally {
    $writer.Dispose()
    $iconStream.Dispose()
}

$previewPath = Join-Path $OutputDirectory "ai-cli-feishu.png"
$preview = $entries | Where-Object Size -eq 256 | Select-Object -First 1
[System.IO.File]::WriteAllBytes($previewPath, [byte[]]$preview.Bytes)

Write-Output "Generated $iconPath"
Write-Output "Generated $previewPath"
