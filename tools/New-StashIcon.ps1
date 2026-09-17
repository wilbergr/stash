<#
.SYNOPSIS
    Generates src/Stash/Assets/Stash.ico.

.DESCRIPTION
    The icon is a stack of cards on a stash: an accent-gradient rounded square
    with two offset white cards and a baseline. Every size in the .ico is drawn
    natively at its own resolution rather than downscaled from one large bitmap,
    because a 16px downscale of a detailed mark turns to mush in the tray.

    The generator is committed instead of the binary alone so the mark can be
    tweaked and regenerated rather than being an opaque asset.

.NOTES
    Run:  powershell -ExecutionPolicy Bypass -File tools\New-StashIcon.ps1
#>
[CmdletBinding()]
param(
    # Resolved in the body, not here: $PSScriptRoot is not yet bound during
    # parameter default evaluation in Windows PowerShell 5.1.
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $OutputPath = Join-Path $root '..\src\Stash\Assets\Stash.ico'
}

Add-Type -AssemblyName System.Drawing

# Sizes Windows asks for across the tray, taskbar, Alt+Tab and Explorer views.
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

function New-RoundedPath {
    param(
        [float] $X, [float] $Y, [float] $W, [float] $H, [float] $Radius
    )

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $Radius * 2

    if ($d -le 0) {
        $path.AddRectangle((New-Object System.Drawing.RectangleF $X, $Y, $W, $H))
        return $path
    }

    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc(($X + $W - $d), $Y, $d, $d, 270, 90)
    $path.AddArc(($X + $W - $d), ($Y + $H - $d), $d, $d, 0, 90)
    $path.AddArc($X, ($Y + $H - $d), $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-StashBitmap {
    param([int] $Size)

    $bmp = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.Clear([System.Drawing.Color]::Transparent)

        $s = [float] $Size

        # --- Accent-gradient rounded square -------------------------------
        $inset = [Math]::Max(0.5, $s * 0.045)
        $bodyW = $s - ($inset * 2)
        $radius = $s * 0.235

        $body = New-RoundedPath -X $inset -Y $inset -W $bodyW -H $bodyW -Radius $radius
        try {
            $rect = New-Object System.Drawing.RectangleF $inset, $inset, $bodyW, $bodyW
            $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                $rect,
                [System.Drawing.Color]::FromArgb(255, 64, 156, 255),
                [System.Drawing.Color]::FromArgb(255, 26, 92, 214),
                45.0)
            try { $g.FillPath($brush, $body) } finally { $brush.Dispose() }
        }
        finally { $body.Dispose() }

        # --- Two offset cards ---------------------------------------------
        # A copy mark: a solid card with a ghost card peeking out behind it,
        # offset up and to the right. The pair is centred on the tile as a whole
        # rather than each card individually, so the composition sits square.
        #
        # The ghost is more opaque at small sizes: below about 24px a 150-alpha
        # white over the accent blue loses too much contrast to read as a second
        # card at all.
        $cardRadius = [Math]::Max(0.6, $s * 0.055)
        $white = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
        $ghostAlpha = if ($Size -lt 24) { 190 } else { 150 }
        $ghost = [System.Drawing.Color]::FromArgb($ghostAlpha, 255, 255, 255)

        $cardW = $s * 0.44
        $cardH = $s * 0.36
        $offset = $s * 0.10

        # Combined bounds span cardW + offset, centred on the tile.
        $left = ($s - ($cardW + $offset)) / 2
        $top = ($s - ($cardH + $offset)) / 2

        # Back card: up and to the right, translucent.
        $backPath = New-RoundedPath -X ($left + $offset) -Y $top -W $cardW -H $cardH -Radius $cardRadius
        try {
            $b = New-Object System.Drawing.SolidBrush $ghost
            try { $g.FillPath($b, $backPath) } finally { $b.Dispose() }
        }
        finally { $backPath.Dispose() }

        # Front card: down and to the left, solid.
        $frontPath = New-RoundedPath -X $left -Y ($top + $offset) -W $cardW -H $cardH -Radius $cardRadius
        try {
            $b = New-Object System.Drawing.SolidBrush $white
            try { $g.FillPath($b, $frontPath) } finally { $b.Dispose() }
        }
        finally { $frontPath.Dispose() }
    }
    finally {
        $g.Dispose()
    }

    return $bmp
}

function ConvertTo-IcoDib {
    <#
        Encodes a bitmap as a classic ICO DIB frame: BITMAPINFOHEADER, 32bpp BGRA
        pixels bottom-up, then a zeroed AND mask.

        Frames are written as DIB rather than PNG for everything below 256px
        because System.Drawing.Icon — and therefore the tray, WinForms and various
        older shells — cannot decode PNG-compressed frames. The height field is
        doubled by the ICO format's convention of counting the mask.
    #>
    param([System.Drawing.Bitmap] $Bitmap)

    $w = $Bitmap.Width
    $h = $Bitmap.Height

    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $locked = $Bitmap.LockBits($rect,
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $locked.Stride
        $pixels = New-Object 'byte[]' ($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
    }
    finally {
        $Bitmap.UnlockBits($locked)
    }

    # Mask rows are padded to a 4-byte boundary even though we leave them empty
    # (32bpp frames carry their own alpha).
    $maskRowBytes = [int][Math]::Floor(($w + 31) / 32) * 4
    $maskSize = $maskRowBytes * $h
    $xorSize = $w * $h * 4

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    try {
        $bw.Write([UInt32] 40)            # biSize
        $bw.Write([Int32] $w)             # biWidth
        $bw.Write([Int32] ($h * 2))       # biHeight (image + mask)
        $bw.Write([UInt16] 1)             # biPlanes
        $bw.Write([UInt16] 32)            # biBitCount
        $bw.Write([UInt32] 0)             # biCompression = BI_RGB
        $bw.Write([UInt32] ($xorSize + $maskSize))
        $bw.Write([Int32] 0)              # biXPelsPerMeter
        $bw.Write([Int32] 0)              # biYPelsPerMeter
        $bw.Write([UInt32] 0)             # biClrUsed
        $bw.Write([UInt32] 0)             # biClrImportant

        # Colour data, bottom-up.
        for ($y = $h - 1; $y -ge 0; $y--) {
            $bw.Write($pixels, ($y * $stride), ($w * 4))
        }

        $bw.Write((New-Object 'byte[]' $maskSize))
        $bw.Flush()

        # The unary comma stops PowerShell unrolling the byte[] into the pipeline,
        # which would hand the caller an object[] of boxed bytes instead.
        return , $ms.ToArray()
    }
    finally {
        $bw.Dispose()
        $ms.Dispose()
    }
}

# Render every size.
# A list of records, not an ordered hashtable: an OrderedDictionary indexed with
# an integer resolves by position rather than by key, so $frames[16] would mean
# "the 17th entry".
$frames = New-Object System.Collections.Generic.List[object]
foreach ($size in $sizes) {
    $bmp = New-StashBitmap -Size $size
    try {
        if ($size -ge 256) {
            # 256px is conventionally PNG-compressed to keep the file small;
            # nothing that reads only DIB frames asks for this size.
            $ms = New-Object System.IO.MemoryStream
            try {
                $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
                [byte[]] $bytes = $ms.ToArray()
            }
            finally { $ms.Dispose() }
        }
        else {
            [byte[]] $bytes = ConvertTo-IcoDib -Bitmap $bmp
        }

        $frames.Add([pscustomobject]@{ Size = $size; Bytes = $bytes })
    }
    finally { $bmp.Dispose() }
}

# --- Assemble the ICO container -------------------------------------------
# Layout: ICONDIR header, then one 16-byte ICONDIRENTRY per image, then the
# PNG payloads. PNG-compressed entries are valid in .ico from Vista onward.
$outFull = [System.IO.Path]::GetFullPath($OutputPath)
$outDir = [System.IO.Path]::GetDirectoryName($outFull)
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

$stream = [System.IO.File]::Create($outFull)
$writer = New-Object System.IO.BinaryWriter $stream
try {
    $count = $frames.Count

    $writer.Write([UInt16] 0)      # reserved
    $writer.Write([UInt16] 1)      # type: 1 = icon
    $writer.Write([UInt16] $count)

    $offset = 6 + (16 * $count)

    foreach ($entry in $frames) {
        $bytes = $entry.Bytes
        # 256 is encoded as 0 in the single-byte width/height fields.
        $dim = if ($entry.Size -ge 256) { 0 } else { $entry.Size }

        $writer.Write([Byte] $dim)          # width
        $writer.Write([Byte] $dim)          # height
        $writer.Write([Byte] 0)             # palette size (0 = no palette)
        $writer.Write([Byte] 0)             # reserved
        $writer.Write([UInt16] 1)           # colour planes
        $writer.Write([UInt16] 32)          # bits per pixel
        $writer.Write([UInt32] $bytes.Length)
        $writer.Write([UInt32] $offset)

        $offset += $bytes.Length
    }

    foreach ($entry in $frames) {
        $writer.Write($entry.Bytes)
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

Write-Host "Wrote $outFull ($([Math]::Round((Get-Item $outFull).Length / 1KB, 1)) KB, $($frames.Count) sizes)"
