<#
.SYNOPSIS
  Builds a multi-size Windows .ico (16, 24, 32, 48, 64, 128, 256; PNG-compressed entries) from a logo.

.DESCRIPTION
  -Source can be
    * an .svg file  : rasterised at every size with headless Edge or Chrome (transparent background), so each size is drawn from the vector, not scaled down;
    * a .png file   : a square master, resampled (high quality) to every size;
    * a folder      : holding pre-made PNGs whose names end in the size, e.g. logo-16.png ... logo-256.png (missing sizes are resampled from the largest).

.EXAMPLE
  # apply a candidate to the app
  pwsh docs/logo/make-ico.ps1 -Source docs/logo/candidate-1.svg -Output src/DbExplorer.App/app.ico

.EXAMPLE
  # dry run elsewhere
  pwsh docs/logo/make-ico.ps1 -Source docs/logo/candidate-3.svg -Output $env:TEMP\test.ico
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Output,
    [int[]]$Sizes = @(16, 24, 32, 48, 64, 128, 256)
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Source = (Resolve-Path -LiteralPath $Source).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("dbx-ico-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null

function ConvertTo-PngBytes([Drawing.Bitmap]$bmp) {
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()
}

function Resize-Png([Drawing.Image]$src, [int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.Clear([Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    $bmp
}

function Get-Browser {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe")
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $found) { throw 'Rasterising an SVG needs Microsoft Edge or Google Chrome (headless). Pass a PNG master or a folder of PNGs instead.' }
    $found
}

$pngs = @{}   # size -> byte[]
try {
    if (Test-Path -LiteralPath $Source -PathType Container) {
        $files = Get-ChildItem -LiteralPath $Source -Filter *.png
        $bySize = @{}
        foreach ($f in $files) { if ($f.BaseName -match '(\d+)$') { $bySize[[int]$Matches[1]] = $f.FullName } }
        if ($bySize.Count -eq 0) { throw "No PNG ending in a size (e.g. logo-16.png) in $Source" }
        $largest = $bySize.Keys | Sort-Object -Descending | Select-Object -First 1
        $master = [Drawing.Image]::FromFile($bySize[$largest])
        foreach ($s in $Sizes) {
            if ($bySize.ContainsKey($s)) { $pngs[$s] = [IO.File]::ReadAllBytes($bySize[$s]) }
            else { $b = Resize-Png $master $s; $pngs[$s] = ConvertTo-PngBytes $b; $b.Dispose() }
        }
        $master.Dispose()
    }
    elseif ($Source -match '\.svg$') {
        $browser = Get-Browser
        $svgUri = ([Uri]$Source).AbsoluteUri
        foreach ($s in $Sizes) {
            # the SVG is drawn at exactly $s px in the corner of a 300 px transparent page, then cropped
            $html = Join-Path $work "r$s.html"
            "<!doctype html><meta charset=utf-8><style>html,body{margin:0;background:transparent}img{display:block;width:${s}px;height:${s}px}</style><img src='$svgUri'>" | Set-Content $html -Encoding utf8
            $shot = Join-Path $work "r$s.png"
            # own profile dir so a running Edge/Chrome of the user is never reused; hard timeout in case the browser hangs
            $profileDir = Join-Path $work 'profile'
            $bargs = @('--headless', '--disable-gpu', '--no-first-run', '--hide-scrollbars', '--force-device-scale-factor=1', '--default-background-color=00000000', '--window-size=300,300', '--allow-file-access-from-files',
                       "--user-data-dir=`"$profileDir`"", "--screenshot=`"$shot`"", "`"$(([Uri]$html).AbsoluteUri)`"")
            $p = Start-Process -FilePath $browser -ArgumentList $bargs -PassThru -WindowStyle Hidden
            if (-not $p.WaitForExit(60000)) { & taskkill /PID $p.Id /T /F | Out-Null }
            if (-not (Test-Path $shot)) { throw "Browser did not produce a screenshot for size $s" }
            $full = [Drawing.Image]::FromFile($shot)
            $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $g = [Drawing.Graphics]::FromImage($bmp)
            $g.Clear([Drawing.Color]::Transparent)
            $g.DrawImage($full, (New-Object Drawing.Rectangle 0, 0, $s, $s), (New-Object Drawing.Rectangle 0, 0, $s, $s), [Drawing.GraphicsUnit]::Pixel)
            $g.Dispose(); $full.Dispose()
            $pngs[$s] = ConvertTo-PngBytes $bmp; $bmp.Dispose()
        }
    }
    else {
        $master = [Drawing.Image]::FromFile($Source)
        foreach ($s in $Sizes) { $b = Resize-Png $master $s; $pngs[$s] = ConvertTo-PngBytes $b; $b.Dispose() }
        $master.Dispose()
    }

    # write the .ico container: ICONDIR + ICONDIRENTRYs + PNG payloads
    $sorted = $Sizes | Sort-Object
    $outDir = Split-Path -Parent ([IO.Path]::GetFullPath($Output))
    if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Force $outDir | Out-Null }
    $fs = [IO.File]::Create([IO.Path]::GetFullPath($Output))
    $bw = New-Object IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sorted.Count)
    $offset = 6 + 16 * $sorted.Count
    foreach ($s in $sorted) {
        $dim = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$s].Length); $bw.Write([uint32]$offset)
        $offset += $pngs[$s].Length
    }
    foreach ($s in $sorted) { $bw.Write($pngs[$s]) }
    $bw.Dispose(); $fs.Dispose()
    Write-Host ("Wrote {0} ({1} bytes): {2}" -f $Output, (Get-Item $Output).Length, ($sorted -join ', '))
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}



