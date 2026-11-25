#Requires -Version 7.0
<#
.SYNOPSIS
  Measures dbExplorer launch time: process start -> main window visible, plus working set after a settle delay.

.DESCRIPTION
  Safe by construction: every run uses a throw-away --data-dir (with a synthetic 8-connection config.json so the tree
  is realistic) and a synthetic --dbeaver-dir inside it. Nothing of the real %APPDATA%\dbExplorer or DBeaver data is
  read or written. The script never sends keystrokes or clicks: it only enumerates top-level windows of the process it
  started (EnumWindows / IsWindowVisible) and reads its working set, then kills it.

  Run 1 is reported as "first" (cold: first launch after a build/publish, OS file cache not primed by this script);
  runs 2..N are the "warm" series (median / min / max).

.PARAMETER ExePath   Path to DbExplorer.exe (default: the Release build output of this repo).
.PARAMETER Runs      Number of launches (default 10, including the cold first one).
.PARAMETER SettleSeconds  Seconds to wait after the window is visible before sampling the working set (default 3).
.PARAMETER Env       Extra environment variables for the launched process, e.g. @{ DBEXPLORER_RENDERING = 'gpu' }.
.PARAMETER Label     Free text printed in the summary.
.PARAMETER TimeoutSeconds  Per-run limit waiting for the window (default 30).
.PARAMETER SessionTabs  When > 0, the throw-away data dir also gets a session.json with that many table tabs (on the
  synthetic SQL Server connections, which are unreachable: the last one is active, so its background connect attempt is
  part of the run) plus one script tab, to measure the restore of open tabs.
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [int]$Runs = 10,
    [double]$SettleSeconds = 3,
    [hashtable]$Env = @{},
    [string]$Label = '',
    [int]$TimeoutSeconds = 30,
    [int]$SessionTabs = 0
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $ExePath) { $ExePath = Join-Path $repo 'src\DbExplorer.App\bin\Release\net10.0\DbExplorer.exe' }
$ExePath = (Resolve-Path $ExePath).Path

Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

public static class DbxProbe
{
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    // First visible top-level window of the pid that is at least 200x150 (skips tiny helper windows).
    public static IntPtr FindWindow(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            RECT r; if (!GetWindowRect(h, out r)) return true;
            if (r.R - r.L < 200 || r.B - r.T < 150) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }

    // Starts the process and polls (1 ms) until its main window is visible. Returns elapsed ms, or -1 on timeout / early exit.
    public static double LaunchAndWait(Process proc, int timeoutMs, out IntPtr hwnd)
    {
        var sw = Stopwatch.StartNew();
        proc.Start();
        hwnd = IntPtr.Zero;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (proc.HasExited) return -1;
            var h = FindWindow((uint)proc.Id);
            if (h != IntPtr.Zero) { hwnd = h; return sw.Elapsed.TotalMilliseconds; }
            Thread.Sleep(1);
        }
        return -1;
    }
}
'@

function New-SyntheticDataDir {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('dbx-measure-' + [Guid]::NewGuid().ToString('N'))
    $dbeaver = Join-Path $root 'dbeaver'
    New-Item -ItemType Directory -Force -Path $root, $dbeaver | Out-Null
    $fixture = Join-Path $repo 'tests\DbExplorer.Core.Tests\Fixtures\data-sources.json'
    if (Test-Path $fixture) { Copy-Item $fixture (Join-Path $dbeaver 'data-sources.json') }
    $folders = 'Admin', 'Company', 'Keeple', 'Translate'
    $kinds = 'SqlServer', 'SqlServer', 'SqlServer', 'SqlServer', 'SqlServer', 'Postgres', 'Postgres', 'Sqlite'
    $conns = for ($i = 0; $i -lt 8; $i++) {
        [ordered]@{
            Id = [Guid]::NewGuid().ToString(); Name = "Synthetic $i"; Kind = $kinds[$i]; Folder = $folders[$i % 4]
            Host = 'localhost'; Port = $null; Database = "db$i"; User = 'u'; ProtectedPassword = $null
            Encrypt = $false; TrustServerCertificate = $true; ReadOnly = $false
        }
    }
    $cfg = [ordered]@{ Folders = $folders; Connections = @($conns); VirtualKeys = @{} }
    $cfg | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 (Join-Path $root 'config.json')
    if ($SessionTabs -gt 0) {
        $tabs = @(for ($t = 0; $t -lt $SessionTabs; $t++) {
            [ordered]@{
                Kind = 'Table'; ConnectionId = $conns[$t % 5].Id; Schema = 'dbo'; Table = "Table$t"
                Filters = @{ name = '=x' }; RawWhere = 'id > 1'; SortColumn = 'id'; SortDescending = $false; ScriptId = $null; FilePath = $null
            }
        })
        $tabs += [ordered]@{ Kind = 'Script'; ConnectionId = $conns[0].Id; Schema = $null; Table = $null; Filters = $null
            RawWhere = $null; SortColumn = $null; SortDescending = $false; ScriptId = [Guid]::NewGuid().ToString(); FilePath = $null }
        $session = [ordered]@{ ActiveIndex = $SessionTabs - 1; Tabs = $tabs }
        $session | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 (Join-Path $root 'session.json')
    }
    return [pscustomobject]@{ Root = $root; DBeaver = $dbeaver }
}

function Get-Stat($values) {
    $s = @($values | Sort-Object)
    if ($s.Count -eq 0) { return [pscustomobject]@{ Median = [double]::NaN; Min = [double]::NaN; Max = [double]::NaN } }
    $mid = [int][math]::Floor($s.Count / 2)
    $median = if ($s.Count % 2) { $s[$mid] } else { ($s[$mid - 1] + $s[$mid]) / 2 }
    [pscustomobject]@{ Median = $median; Min = $s[0]; Max = $s[-1] }
}

$times = New-Object System.Collections.Generic.List[double]
$wsMb = New-Object System.Collections.Generic.List[double]
$privMb = New-Object System.Collections.Generic.List[double]

for ($i = 1; $i -le $Runs; $i++) {
    $dirs = New-SyntheticDataDir
    $proc = $null
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $ExePath
        $psi.WorkingDirectory = Split-Path -Parent $ExePath
        $psi.UseShellExecute = $false
        foreach ($a in '--data-dir', $dirs.Root, '--dbeaver-dir', $dirs.DBeaver) { $psi.ArgumentList.Add($a) }
        foreach ($k in $Env.Keys) { $psi.Environment[$k] = [string]$Env[$k] }
        $proc = New-Object System.Diagnostics.Process
        $proc.StartInfo = $psi
        $hwnd = [IntPtr]::Zero
        $ms = [DbxProbe]::LaunchAndWait($proc, $TimeoutSeconds * 1000, [ref]$hwnd)
        if ($ms -lt 0) { Write-Warning "run ${i}: no window (timeout or early exit)"; continue }
        Start-Sleep -Milliseconds ([int]($SettleSeconds * 1000))
        $proc.Refresh()
        $ws = $proc.WorkingSet64 / 1MB
        $pv = $proc.PrivateMemorySize64 / 1MB
        $times.Add($ms); $wsMb.Add($ws); $privMb.Add($pv)
        Write-Host ('run {0,2}: {1,7:N0} ms   WS {2,6:N1} MB  private {3,6:N1} MB' -f $i, $ms, $ws, $pv)
    }
    finally {
        if ($proc -and -not $proc.HasExited) { $proc.Kill($true); $proc.WaitForExit(5000) | Out-Null }
        if ($proc) { $proc.Dispose() }
        Start-Sleep -Milliseconds 300
        Remove-Item -Recurse -Force $dirs.Root -ErrorAction SilentlyContinue
    }
}

if ($times.Count -eq 0) { throw 'No successful run.' }
$first = $times[0]
$warmTimes = if ($times.Count -gt 1) { $times.GetRange(1, $times.Count - 1) } else { $times }
$warm = Get-Stat $warmTimes
$wsStat = Get-Stat $wsMb
$pvStat = Get-Stat $privMb
$envText = if ($Env.Count) { ($Env.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ' } else { '-' }
Write-Host ''
Write-Host "== $Label  ($ExePath) env: $envText"
Write-Host ('cold (run 1)      : {0,7:N0} ms' -f $first)
Write-Host ('warm (runs 2..N)  : median {0:N0} ms   min {1:N0}   max {2:N0}   (n={3})' -f $warm.Median, $warm.Min, $warm.Max, $warmTimes.Count)
Write-Host ('working set @{0}s : median {1:N1} MB  min {2:N1}  max {3:N1}' -f $SettleSeconds, $wsStat.Median, $wsStat.Min, $wsStat.Max)
Write-Host ('private bytes     : median {0:N1} MB' -f $pvStat.Median)

[pscustomobject]@{
    Label = $Label; ColdMs = [math]::Round($first); WarmMedianMs = [math]::Round($warm.Median)
    WarmMinMs = [math]::Round($warm.Min); WarmMaxMs = [math]::Round($warm.Max)
    WorkingSetMB = [math]::Round($wsStat.Median, 1); PrivateMB = [math]::Round($pvStat.Median, 1)
}
