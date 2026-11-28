# Relay script of the auto-update (embedded in DbExplorer.exe, written to a temp dir and run by Windows PowerShell 5.1,
# so keep it 5.1-compatible). The running app cannot overwrite its own exe/dlls: it starts this script and exits.
# Steps: wait for the app to exit, extract the zip to a staging dir, check it holds DbExplorer.exe, move the current files
# to <InstallDir>\.update-backup (.update-backup-<pid> while an older one is in use), copy the new files in. On any failure
# the backup is restored and the OLD exe relaunched;
# on success the new exe is relaunched and backup, staging, zip and this script's dir are deleted.
# User data (%APPDATA%\dbExplorer) lives outside InstallDir and is never touched.
# Exit codes: 0 updated, 1 failed (old version relaunched), 2 the app did not exit in time (nothing touched).
param(
    [Parameter(Mandatory)][string]$ZipPath,
    [Parameter(Mandatory)][string]$InstallDir,
    [Parameter(Mandatory)][string]$ExePath,
    [int]$ProcessId = 0,
    [string]$ArgsFile = '',                 # relaunch arguments, one per line
    [int]$WaitTimeoutSec = 60,
    [int]$RetryCount = 5,                   # Defender may briefly lock freshly written files
    [int]$RetryDelayMs = 500,
    [string]$ExeName = 'DbExplorer.exe',     # the app's exe in the zip and in InstallDir (parameter only so tests can use a fake)
    [int]$TestFailAfterCopy = 0             # test only: throw after copying this many top-level items
)

$ErrorActionPreference = 'Stop'
$work = $PSScriptRoot
$log = Join-Path $work 'update.log'
$staging = Join-Path $work 'staging'
$backup = Join-Path $InstallDir '.update-backup'
$newExe = Join-Path $InstallDir $ExeName
Set-Location -LiteralPath ([IO.Path]::GetTempPath())

function Write-Log([string]$Message) {
    try { Add-Content -LiteralPath $log -Value ('{0:s} {1}' -f (Get-Date), $Message) } catch { }
}

function Invoke-Retry([scriptblock]$Action, [string]$What) {
    for ($i = 1; ; $i++) {
        try { & $Action; return }
        catch {
            if ($i -ge $RetryCount) { throw }
            Write-Log "retry $i ($What): $($_.Exception.Message)"
            Start-Sleep -Milliseconds $RetryDelayMs
        }
    }
}

# Quotes one argument for a Windows command line.
function ConvertTo-ArgText([string]$Text) {
    if ($Text -ne '' -and $Text -notmatch '[\s"]') { return $Text }
    $s = $Text -replace '(\*)"', '$1$1\"'
    $s = $s -replace '(\+)$', '$1$1'
    return '"' + $s + '"'
}

function Start-App([string]$Exe) {
    try {
        $relaunch = @()
        if ($ArgsFile -and (Test-Path -LiteralPath $ArgsFile)) { $relaunch = @([IO.File]::ReadAllLines($ArgsFile)) }
        $line = (@($relaunch | ForEach-Object { ConvertTo-ArgText $_ })) -join ' '
        $p = @{ FilePath = $Exe; WorkingDirectory = $InstallDir }
        if ($line) { $p.ArgumentList = $line }
        Start-Process @p | Out-Null
        Write-Log "started $Exe"
    }
    catch { Write-Log "could not start ${Exe}: $($_.Exception.Message)" }
}

function Get-InstallItems { @(Get-ChildItem -LiteralPath $InstallDir -Force | Where-Object { $_.Name -notlike '.update-backup*' }) }

# $RemoveNew: also delete what is in InstallDir now (new files copied before the failure).
function Restore-Backup([bool]$RemoveNew) {
    if ($RemoveNew) {
        foreach ($item in Get-InstallItems) {
            Invoke-Retry { Remove-Item -LiteralPath $item.FullName -Recurse -Force } "remove $($item.Name)"
        }
    }
    foreach ($item in @(Get-ChildItem -LiteralPath $backup -Force)) {
        Invoke-Retry { Move-Item -LiteralPath $item.FullName -Destination $InstallDir -Force } "restore $($item.Name)"
    }
    Remove-Item -LiteralPath $backup -Recurse -Force
    Write-Log 'backup restored'
}

function Remove-Quietly([string]$Path) {
    try { if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force } }
    catch { Write-Log "could not delete ${Path}: $($_.Exception.Message)" }
}

Write-Log "update start: zip=$ZipPath install=$InstallDir pid=$ProcessId"

# 1. wait for the app to exit
if ($ProcessId -gt 0) {
    $proc = $null
    try { $proc = [Diagnostics.Process]::GetProcessById($ProcessId) } catch { }
    if ($proc -and -not $proc.WaitForExit($WaitTimeoutSec * 1000)) {
        Write-Log "process $ProcessId still running after $WaitTimeoutSec s: aborted, nothing touched"
        exit 2
    }
}

$phase = 'none'     # none -> backup (moving old files out) -> copy (copying new files in)
try {
    # A backup left by an interrupted run: restore it when the install is broken, otherwise drop it. A process started from
    # the old exe may still run (Claude Code keeps its "--mcp" server open across an update: a running exe can be moved,
    # not deleted), so a backup that cannot be deleted stays and this run uses another folder.
    $left = @(Get-ChildItem -LiteralPath $InstallDir -Force -Directory -Filter '.update-backup*' | Sort-Object CreationTimeUtc)
    if ($left.Count -gt 0) {
        if (Test-Path -LiteralPath $newExe) { foreach ($old in $left) { Remove-Quietly $old.FullName } }
        else {
            Write-Log 'broken install, restoring the backup of an interrupted update'
            $backup = $left[-1].FullName
            Restore-Backup $true
            $backup = Join-Path $InstallDir '.update-backup'
        }
        if (Test-Path -LiteralPath $backup) { $backup = Join-Path $InstallDir ".update-backup-$PID" }
    }

    # 2. extract + sanity check
    if (-not (Test-Path -LiteralPath $ZipPath)) { throw "zip not found: $ZipPath" }
    Remove-Quietly $staging
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $staging)
    if (-not (Test-Path -LiteralPath (Join-Path $staging $ExeName))) { throw "$ExeName not found in the zip" }
    Write-Log 'extracted'

    # 3. swap
    New-Item -ItemType Directory -Path $backup | Out-Null
    $phase = 'backup'
    foreach ($item in Get-InstallItems) {
        Invoke-Retry { Move-Item -LiteralPath $item.FullName -Destination $backup -Force } "backup $($item.Name)"
    }
    $phase = 'copy'
    $copied = 0
    foreach ($item in @(Get-ChildItem -LiteralPath $staging -Force)) {
        Invoke-Retry { Copy-Item -LiteralPath $item.FullName -Destination $InstallDir -Recurse -Force } "copy $($item.Name)"
        $copied++
        if ($TestFailAfterCopy -gt 0 -and $copied -ge $TestFailAfterCopy) { throw 'simulated copy failure' }
    }
    if (-not (Test-Path -LiteralPath $newExe)) { throw "$ExeName missing after the copy" }
    Write-Log 'swapped'
}
catch {
    Write-Log "FAILED: $($_.Exception.Message)"
    try { if ($phase -ne 'none') { Restore-Backup ($phase -eq 'copy') } }
    catch { Write-Log "RESTORE FAILED, backup kept in ${backup}: $($_.Exception.Message)" }
    Remove-Quietly $staging
    Start-App $ExePath
    exit 1
}

# 4. success: relaunch, then clean up (best effort; the log dir is removed last)
Start-App $newExe
Remove-Quietly $backup
Remove-Quietly $staging
Remove-Quietly $ZipPath
Write-Log 'done'
Remove-Quietly $work
exit 0
