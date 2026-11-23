#Requires -Version 7.0
<#
.SYNOPSIS
  Publishes dbExplorer into .\dist (fastest-launching layout) and measures its startup.

.DESCRIPTION
  Default: self-contained, win-x64, ReadyToRun composite of the app, its dependencies AND the .NET runtime (about 160 MB,
  launches in about 0.28 s; no installed .NET needed). -FrameworkDependent gives a ~60 MB folder that needs the .NET 10
  Desktop/Core runtime and launches in about 0.33 s. Trimming is never used (Avalonia bindings use reflection).

  The script deletes dist\ and the obj\ folders of the two projects first: ReadyToRun composite leaves rewritten assemblies
  in obj\, and a later non-composite publish that reuses them produces an exe that fail-fasts at launch.
  The last step runs scripts\measure-startup.ps1 on the published exe with a throw-away --data-dir (first launch of a
  fresh publish is slow, several seconds, because Windows Defender scans every new file once; later launches are not).

.PARAMETER FrameworkDependent  Publish without the runtime (smaller, ~50 ms slower).
.PARAMETER Runs                Launches for the startup measurement (default 5, at least 1). The run fails if no window appears.
#>
[CmdletBinding()]
param(
    [switch]$FrameworkDependent,
    [int]$Runs = 5
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$proj = Join-Path $root 'src\DbExplorer.App'

if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
foreach ($p in 'src\DbExplorer.App', 'src\DbExplorer.Core') {
    $obj = Join-Path $root "$p\obj"
    if (Test-Path $obj) { Remove-Item -Recurse -Force $obj }
}

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
Write-Host "Publishing (self-contained=$selfContained, ReadyToRun composite) to $dist ..."
dotnet publish $proj -c Release -r win-x64 --self-contained $selfContained -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=true -p:UseSharedCompilation=false -o $dist
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $dist 'DbExplorer.exe'
if (-not (Test-Path $exe)) { throw "Missing $exe after publish." }
$total = (Get-ChildItem $dist -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ''
Write-Host "exe  : $exe ($([math]::Round((Get-Item $exe).Length / 1KB)) KB)"
Write-Host "dist : $([math]::Round($total / 1MB, 1)) MB in $((Get-ChildItem $dist -Recurse -File).Count) files"
Write-Host ''

& (Join-Path $root 'scripts\measure-startup.ps1') -ExePath $exe -Runs ([math]::Max($Runs, 1)) -Label 'published exe'
