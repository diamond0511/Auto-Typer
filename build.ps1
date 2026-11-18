<#
    Builds AutoTyper.exe using the C# compiler that ships with the .NET Framework,
    so no SDK or toolchain install is required.

    Usage:  powershell -ExecutionPolicy Bypass -File .\build.ps1
            powershell -ExecutionPolicy Bypass -File .\build.ps1 -Run
#>
param(
    [switch]$Run,
    [switch]$Debug
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir  = Join-Path $root 'src'
$distDir = Join-Path $root 'dist'
$icoPath = Join-Path $srcDir 'icon.ico'
$exePath = Join-Path $distDir 'AutoTyper.exe'

# ---------------------------------------------------------------- compiler

$csc = Get-ChildItem 'C:\Windows\Microsoft.NET\Framework64\v4*\csc.exe' -ErrorAction SilentlyContinue |
       Sort-Object FullName | Select-Object -Last 1 -ExpandProperty FullName

if (-not $csc) {
    $csc = Get-ChildItem 'C:\Windows\Microsoft.NET\Framework\v4*\csc.exe' -ErrorAction SilentlyContinue |
           Sort-Object FullName | Select-Object -Last 1 -ExpandProperty FullName
}

if (-not $csc) {
    throw 'Could not find the .NET Framework C# compiler (csc.exe). Install the .NET Framework 4.x developer files or build with the .NET SDK.'
}

Write-Host "Compiler : $csc"

# ---------------------------------------------------------------- icon

function New-RoundedRectPath {
    param($X, $Y, $W, $H, $R)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $R * 2

    if ($d -le 0) {
        $path.AddRectangle((New-Object System.Drawing.RectangleF($X, $Y, $W, $H)))
        return $path
    }

    $path.AddArc($X,            $Y,            $d, $d, 180, 90)
    $path.AddArc($X + $W - $d,  $Y,            $d, $d, 270, 90)
    $path.AddArc($X + $W - $d,  $Y + $H - $d,  $d, $d,   0, 90)
    $path.AddArc($X,            $Y + $H - $d,  $d, $d,  90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [double]$Size

    # Dark rounded plate.
    $plate = New-RoundedRectPath 0.5 0.5 ($s - 1) ($s - 1) ($s * 0.22)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($s, $s)),
        [System.Drawing.Color]::FromArgb(255, 42, 52, 70),
        [System.Drawing.Color]::FromArgb(255, 20, 26, 38))
    $g.FillPath($brush, $plate)
    $brush.Dispose()
    $plate.Dispose()

    # Two "text lines" plus an accent caret: reads as typing.
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(236, 245, 250, 255))
    $accent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 56, 189, 248))

    $barH = [Math]::Max(1.0, $s * 0.095)
    $radius = $barH / 2.0

    $bar1 = New-RoundedRectPath ($s * 0.22) ($s * 0.30) ($s * 0.56) $barH $radius
    $g.FillPath($white, $bar1)
    $bar1.Dispose()

    $bar2 = New-RoundedRectPath ($s * 0.22) ($s * 0.49) ($s * 0.34) $barH $radius
    $g.FillPath($white, $bar2)
    $bar2.Dispose()

    $caretW = [Math]::Max(1.0, $s * 0.10)
    $caret = New-RoundedRectPath ($s * 0.62) ($s * 0.44) $caretW ($s * 0.26) ($caretW / 2.0)
    $g.FillPath($accent, $caret)
    $caret.Dispose()

    $white.Dispose()
    $accent.Dispose()
    $g.Dispose()

    return $bmp
}

<#
    Produces a classic DIB icon image: BITMAPINFOHEADER with a doubled height,
    bottom-up 32bpp BGRA pixels, then a 1bpp AND mask. PNG-compressed icon entries
    are rejected by the .NET Framework Win32 resource writer, so DIB it is.
#>
function New-IconDib {
    param([int]$Size)

    $bmp = New-IconBitmap -Size $Size

    $rect = New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)
    $locked = $bmp.LockBits($rect,
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    $stride = $locked.Stride
    $pixels = New-Object byte[] ($stride * $Size)
    [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($locked)
    $bmp.Dispose()

    $rowBytes  = $Size * 4
    $maskRow   = [int]([Math]::Floor(($Size + 31) / 32)) * 4
    $out       = New-Object System.IO.MemoryStream
    $bw        = New-Object System.IO.BinaryWriter($out)

    # BITMAPINFOHEADER
    $bw.Write([uint32]40)            # biSize
    $bw.Write([int32]$Size)          # biWidth
    $bw.Write([int32]($Size * 2))    # biHeight: colour data + mask
    $bw.Write([uint16]1)             # biPlanes
    $bw.Write([uint16]32)            # biBitCount
    $bw.Write([uint32]0)             # biCompression: BI_RGB
    $bw.Write([uint32]0)             # biSizeImage
    $bw.Write([int32]0)              # biXPelsPerMeter
    $bw.Write([int32]0)              # biYPelsPerMeter
    $bw.Write([uint32]0)             # biClrUsed
    $bw.Write([uint32]0)             # biClrImportant

    # Colour data, bottom-up.
    for ($y = $Size - 1; $y -ge 0; $y--) {
        $bw.Write($pixels, $y * $stride, $rowBytes)
    }

    # AND mask: all zero, since the 32bpp alpha channel carries transparency.
    $bw.Write((New-Object byte[] ($maskRow * $Size)))

    $bw.Flush()
    [byte[]]$bytes = $out.ToArray()
    $bw.Dispose()
    $out.Dispose()

    # The leading comma stops PowerShell unrolling the array into the pipeline.
    return ,$bytes
}

function Write-IcoFile {
    param([string]$Path, [int[]]$Sizes)

    $images = @()
    foreach ($size in $Sizes) {
        $images += ,@{ Size = $size; Bytes = [byte[]](New-IconDib -Size $size) }
    }

    $fs = [System.IO.File]::Create($Path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        $bw.Write([uint16]0)                  # reserved
        $bw.Write([uint16]1)                  # type: icon
        $bw.Write([uint16]$images.Count)

        $offset = 6 + (16 * $images.Count)
        foreach ($img in $images) {
            $dim = $img.Size
            if ($dim -ge 256) { $dim = 0 }    # 0 means 256 in the ICO directory

            $bw.Write([byte]$dim)             # width
            $bw.Write([byte]$dim)             # height
            $bw.Write([byte]0)                # palette colours
            $bw.Write([byte]0)                # reserved
            $bw.Write([uint16]1)              # colour planes
            $bw.Write([uint16]32)             # bits per pixel
            $bw.Write([uint32]$img.Bytes.Length)
            $bw.Write([uint32]$offset)
            $offset += $img.Bytes.Length
        }

        foreach ($img in $images) { $bw.Write([byte[]]$img.Bytes, 0, $img.Bytes.Length) }
    }
    finally {
        $bw.Dispose()
        $fs.Dispose()
    }
}

if (-not (Test-Path $icoPath)) {
    Add-Type -AssemblyName System.Drawing
    Write-Host 'Icon     : generating src\icon.ico'
    Write-IcoFile -Path $icoPath -Sizes @(16, 32, 48, 64, 128)
} else {
    Write-Host 'Icon     : src\icon.ico (existing)'
}

# ---------------------------------------------------------------- compile

if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }

$sources = Get-ChildItem (Join-Path $srcDir '*.cs') | Sort-Object Name | Select-Object -ExpandProperty FullName
if (-not $sources) { throw "No .cs files found in $srcDir" }

Write-Host "Sources  : $($sources.Count) file(s)"

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/warn:4'
    '/define:TRACE'
    "/out:$exePath"
    "/win32icon:$icoPath"
    "/win32manifest:$(Join-Path $srcDir 'app.manifest')"
    '/reference:System.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
)

if ($Debug) { $cscArgs += '/debug+'; $cscArgs += '/optimize-' }
else        { $cscArgs += '/optimize+' }

$cscArgs += $sources

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }

$built = Get-Item $exePath
Write-Host ''
Write-Host ("Built    : {0}  ({1:N0} bytes)" -f $built.FullName, $built.Length) -ForegroundColor Green

if ($Run) {
    Write-Host 'Starting : AutoTyper.exe'
    Start-Process -FilePath $exePath
}
