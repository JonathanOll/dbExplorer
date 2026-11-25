#Requires -Version 5.1
<#
.SYNOPSIS
Smoke test of install.ps1: runs the real script against throw-away locations (nothing of the user's real install is touched).

.DESCRIPTION
Needs network (plain HTTPS to the GitHub API: the main scenarios run with gh NOT on PATH) and, for the -UseGh / fallback
scenarios, the GitHub CLI (it installs the real latest release several times). Every location is a seam of
install.ps1 pointing into a temp sandbox: install dir, setup dir, Start Menu, desktop, user data, and a registry key
HKCU:\Software\dbExplorer-test-<guid>. Everything is deleted at the end, also on failure, and the test then checks that
the real Start Menu entry, "Apps & features" key, default install dir and temp folders did not change.

Scenarios: fresh install, already installed, -Force, upgrade from an older version, running app (refused, then closed
with -Force), in-app-update simulation + uninstall through the registry command, idempotent uninstall, -RemoveUserData,
-WhatIf, piped script block / Invoke-Expression forms, gh missing, bad version, unknown repository, SHA-256 mismatch
(fake gh), foreign non-empty folder, plus unit checks of the helpers (dot-sourced) and a Windows PowerShell 5.1 parse check.

.PARAMETER Engine
Which PowerShell runs install.ps1: powershell (Windows PowerShell 5.1, default, what the Apps & features command uses) or pwsh.

.EXAMPLE
pwsh .\scripts\test-install.ps1
pwsh .\scripts\test-install.ps1 -Engine pwsh
#>
[CmdletBinding()]
param(
    [ValidateSet('powershell', 'pwsh')][string]$Engine = 'powershell'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$install = Join-Path $repoRoot 'install.ps1'
$engineExe = (Get-Command $(if ($Engine -eq 'pwsh') { 'pwsh' } else { 'powershell' }) -CommandType Application | Select-Object -First 1).Source
$guid = [guid]::NewGuid().ToString('N').Substring(0, 12)
$sandbox = Join-Path ([IO.Path]::GetTempPath()) "dbExplorer-install-test-$guid"
$loc = @{
    App = Join-Path $sandbox 'app'; Setup = Join-Path $sandbox 'setup'; Start = Join-Path $sandbox 'startmenu'
    Desk = Join-Path $sandbox 'desktop'; Data = Join-Path $sandbox 'userdata'; Shim = Join-Path $sandbox 'shim'
}
$regKey = "HKCU:\Software\dbExplorer-test-$guid"
$seamArgs = @('-InstallDir', $loc.App, '-SetupDir', $loc.Setup, '-StartMenuDir', $loc.Start, '-DesktopDir', $loc.Desk,
    '-RegistryKey', $regKey, '-UserDataDir', $loc.Data)

$script:Failures = New-Object System.Collections.Generic.List[string]
$script:Passed = 0
$startedProcesses = New-Object System.Collections.Generic.List[object]

function Assert-That {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { $script:Passed++; Write-Host "  ok    $Message" -ForegroundColor DarkGreen }
    else { $script:Failures.Add($Message); Write-Host "  FAIL  $Message" -ForegroundColor Red }
}

function Write-Scenario { param([string]$Title) Write-Host "`n== $Title" -ForegroundColor Cyan }

# Runs a command with the given process environment overrides; returns Out (text) and Code.
function Invoke-Child {
    param([string[]]$Arguments, [hashtable]$Env = @{})
    $saved = @{}
    foreach ($k in $Env.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $Env[$k]) }
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = (& $engineExe @Arguments 2>&1 | ForEach-Object { "$_" }) -join "`n" }
    finally {
        $ErrorActionPreference = $prev
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }
    [pscustomobject]@{ Out = $out; Code = $LASTEXITCODE }
}

function Invoke-Installer {
    param([string[]]$Extra = @(), [hashtable]$Env = @{}, [switch]$NoSeams)
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $install) + $Extra
    # The fake gh only exists to be called: those scenarios ask for gh explicitly (plain HTTPS is the default).
    if ($Env.ContainsKey('FAKE_GH_MODE') -and $Extra -notcontains '-UseGh') { $a += '-UseGh' }
    if (-not $NoSeams) { $a += $seamArgs }
    Invoke-Child -Arguments $a -Env $Env
}

function Get-RegistryEntry { if (Test-Path -LiteralPath $regKey) { Get-ItemProperty -LiteralPath $regKey } else { $null } }
function Get-ShortcutTarget { param([string]$Path) $sh = New-Object -ComObject WScript.Shell; try { $sh.CreateShortcut($Path).TargetPath } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($sh) } }
function Get-ExeVersion { [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $loc.App 'DbExplorer.exe')).ProductVersion }

function Wait-Until {
    param([scriptblock]$Condition, [int]$TimeoutSec = 20)
    $end = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $end) { if (& $Condition) { return $true }; Start-Sleep -Milliseconds 300 }
    return [bool](& $Condition)
}

function Reset-Sandbox {
    foreach ($d in $loc.App, $loc.Setup, $loc.Start, $loc.Desk, $loc.Data) { if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force } }
    if (Test-Path -LiteralPath $regKey) { Remove-Item -LiteralPath $regKey -Recurse -Force }
}

# A tiny exe carrying an old version stamp: stands for an older install, and (it sleeps) for a running dbExplorer.
function New-StubExe {
    param([string]$Path, [string]$Version)
    $src = Join-Path $sandbox 'stub.cs'
    Set-Content -LiteralPath $src -Encoding ASCII -Value @"
[assembly: System.Reflection.AssemblyInformationalVersion("$Version")]
class P { static void Main() { System.Threading.Thread.Sleep(120000); } }
"@
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $o = & $csc /nologo /target:exe "/out:$Path" $src 2>&1
    if (-not (Test-Path -LiteralPath $Path)) { throw "could not compile the stub exe: $o" }
}

function New-GhShim {
    New-Item -ItemType Directory -Path $loc.Shim -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $loc.Shim 'gh.cmd') -Encoding ASCII -Value '@powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gh-shim.ps1" %*'
    Set-Content -LiteralPath (Join-Path $loc.Shim 'gh-shim.ps1') -Encoding ASCII -Value @'
# Fake gh. FAKE_GH_MODE: badhash (release with a wrong digest), cachedzip (real `release view`, `release download` copies
# FAKE_GH_ZIP), serve-script (`api` returns the file FAKE_GH_FILE). Anything else goes to the real gh (FAKE_GH_REAL).
$a = @($args)
switch ($env:FAKE_GH_MODE) {
    'badhash' {
        if ($a[0] -eq 'release' -and $a[1] -eq 'view') {
            '{"tagName":"v9.9.9","assets":[{"name":"DbExplorer.zip","size":4,"digest":"sha256:' + ('0' * 64) + '"}]}'
            exit 0
        }
        if ($a[0] -eq 'release' -and $a[1] -eq 'download') {
            [IO.File]::WriteAllText((Join-Path $a[($a.IndexOf('--dir') + 1)] 'DbExplorer.zip'), 'junk')
            exit 0
        }
    }
    'cachedzip' {
        if ($a[0] -eq 'release' -and $a[1] -eq 'download') {
            Copy-Item -LiteralPath $env:FAKE_GH_ZIP -Destination (Join-Path $a[($a.IndexOf('--dir') + 1)] 'DbExplorer.zip') -Force
            exit 0
        }
    }
    'serve-script' {
        if ($a[0] -eq 'api') { [IO.File]::ReadAllText($env:FAKE_GH_FILE); exit 0 }
    }
}
& $env:FAKE_GH_REAL @a
exit $LASTEXITCODE
'@
}

# ---------------------------------------------------------------------------------------------------------------------
$realRegKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\dbExplorer'
$realInstall = Join-Path $env:LOCALAPPDATA 'Programs\dbExplorer'
$realSetup = Join-Path $env:LOCALAPPDATA 'Programs\dbExplorer-setup'
$realLnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'dbExplorer.lnk'
$realData = Join-Path $env:APPDATA 'dbExplorer'
$realState = { @{ Key = (Test-Path -LiteralPath $realRegKey); Install = (Test-Path -LiteralPath $realInstall); Setup = (Test-Path -LiteralPath $realSetup); Lnk = (Test-Path -LiteralPath $realLnk); Data = (Test-Path -LiteralPath $realData) } }
$realBefore = & $realState
$tempBefore = @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Filter 'dbExplorer-install-*' -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike 'dbExplorer-install-test-*' } | ForEach-Object { $_.Name })
$ghReal = (Get-Command gh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)
if (-not $ghReal) { throw 'The GitHub CLI (gh) is needed to run this test.' }
$latestTag = ((& gh release view --repo JonathanOll/dbExplorer --json tagName | ConvertFrom-Json).tagName)

Write-Host "install.ps1 smoke test, engine: $Engine ($engineExe), latest release: $latestTag, sandbox: $sandbox"
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null

try {
    New-GhShim
    $shimEnv = @{ PATH = "$($loc.Shim);$env:PATH"; FAKE_GH_REAL = $ghReal.Source }

    Write-Scenario 'Static checks: pure ASCII, Windows PowerShell 5.1 parse, help'
    $bytes = [IO.File]::ReadAllBytes($install)
    Assert-That (@($bytes | Where-Object { $_ -gt 127 }).Count -eq 0) 'install.ps1 is pure ASCII'
    $ps51 = (Get-Command powershell -CommandType Application | Select-Object -First 1).Source
    $e51 = & $ps51 -NoProfile -Command "`$e=`$null;`$t=`$null;[void][System.Management.Automation.Language.Parser]::ParseFile('$install',[ref]`$t,[ref]`$e);`$e.Count"
    Assert-That ("$e51".Trim() -eq '0') 'install.ps1 parses with Windows PowerShell 5.1 (0 errors)'
    $ps7 = Get-Command pwsh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($ps7) {
        $e7 = & $ps7.Source -NoProfile -Command "`$e=`$null;`$t=`$null;[void][System.Management.Automation.Language.Parser]::ParseFile('$install',[ref]`$t,[ref]`$e);`$e.Count"
        Assert-That ("$e7".Trim() -eq '0') 'install.ps1 parses with PowerShell 7 (0 errors)'
    }
    $help = & $ps51 -NoProfile -ExecutionPolicy Bypass -Command "Get-Help '$install' -Full | Out-String"
    $helpText = $help -join "`n"   # section titles are localised: match the content
    Assert-That ($helpText -match 'Installs, updates or uninstalls' -and $helpText -match 'RemoveUserData' -and $helpText -match 'Installs v1\.0\.4, adds a desktop shortcut') 'Get-Help renders synopsis, parameters and examples'

    Write-Scenario 'Helpers (dot-sourced: nothing is installed)'
    & {
        . $install
        Assert-That ((ConvertTo-ReleaseTag '1.0.4') -eq 'v1.0.4' -and (ConvertTo-ReleaseTag 'v1.2.3-beta.1') -eq 'v1.2.3-beta.1' -and (ConvertTo-ReleaseTag '') -eq '') 'ConvertTo-ReleaseTag accepts 1.0.4 / v1.0.4 / empty'
        Assert-That ($null -eq (ConvertTo-ReleaseTag 'abc') -and $null -eq (ConvertTo-ReleaseTag '1.0') -and $null -eq (ConvertTo-ReleaseTag '1.0.4+x')) 'ConvertTo-ReleaseTag rejects garbage'
        $c = @((Compare-SemVer (ConvertTo-SemVer '1.0.4') (ConvertTo-SemVer 'v1.0.10')), (Compare-SemVer (ConvertTo-SemVer '2.0.0') (ConvertTo-SemVer '1.9.9')),
            (Compare-SemVer (ConvertTo-SemVer '1.0.0-beta.2') (ConvertTo-SemVer '1.0.0')), (Compare-SemVer (ConvertTo-SemVer '1.0.0-beta.2') (ConvertTo-SemVer '1.0.0-beta.10')),
            (Compare-SemVer (ConvertTo-SemVer '1.0.4+abc') (ConvertTo-SemVer '1.0.4')))
        Assert-That (($c -join ',') -eq '-1,1,-1,-1,0') "Compare-SemVer orders versions per semver ($($c -join ','))"
        $rel = '{"tagName":"v1.2.3","assets":[{"name":"notes.txt"},{"name":"DbExplorer.zip","size":10,"digest":"sha256:' + ('AB' * 32) + '"}]}' | ConvertFrom-Json
        $asset = Select-ReleaseAsset $rel
        Assert-That ($asset.Name -eq 'DbExplorer.zip' -and $asset.Sha256 -eq ('ab' * 32) -and $asset.Size -eq 10) 'Select-ReleaseAsset picks the zip and lower-cases its digest'
        Assert-That ($null -eq (Select-ReleaseAsset ('{"tagName":"v1","assets":[{"name":"a.txt"}]}' | ConvertFrom-Json))) 'Select-ReleaseAsset returns nothing without a zip'
        Assert-That ((Select-ReleaseAsset ('{"tagName":"v1","assets":[{"name":"DbExplorer.zip","size":1}]}' | ConvertFrom-Json)).Sha256 -eq '') 'Select-ReleaseAsset tolerates a missing digest'
        Assert-That ((Get-GhFailure 'HTTP 403: API rate limit exceeded' '' 'a/b').Message -match 'rate limit') 'Get-GhFailure recognises the rate limit'
        Assert-That ((Get-GhFailure 'release not found' 'v9.9.9' 'a/b').Message -match 'v9.9.9') 'Get-GhFailure recognises an unknown tag'
        $script:SelfText = ''
        $env:FAKE_GH_MODE = 'serve-script'; $env:FAKE_GH_FILE = $install; $env:FAKE_GH_REAL = $ghReal.Source
        $oldPath = $env:PATH; $env:PATH = "$($loc.Shim);$env:PATH"
        try { $t = Get-SelfText 'JonathanOll/dbExplorer' } finally { $env:PATH = $oldPath; $env:FAKE_GH_MODE = $null; $env:FAKE_GH_FILE = $null }
        Assert-That ($t.Contains('function Invoke-DbExplorerInstaller')) 'Get-SelfText falls back to `gh api` when the script is not a file'
    }

    Write-Scenario 'Preflight errors (nothing installed)'
    $r = Invoke-Installer -Extra @('-Version', 'banana')
    Assert-That ($r.Code -ne 0 -and $r.Out -match "Invalid -Version 'banana'" -and $r.Out -match 'Fix:') "bad -Version: exit $($r.Code), cause + fix"
    Assert-That (-not (Test-Path -LiteralPath $loc.App)) 'bad -Version creates nothing'
    $sys = "$env:SystemRoot\System32;$env:SystemRoot"
    $r = Invoke-Installer -Extra @('-UseGh') -Env @{ PATH = $sys }
    Assert-That ($r.Code -ne 0 -and $r.Out -match 'GitHub CLI \(gh\) is not installed' -and $r.Out -match 'winget install --id GitHub\.cli') "-UseGh without gh: exit $($r.Code), clear error with the winget hint"
    Assert-That (-not (Test-Path -LiteralPath $loc.App)) '-UseGh without gh creates nothing'
    $r = Invoke-Installer -Extra @('-Version', '99.0.0') -Env @{ PATH = $sys }
    Assert-That ($r.Code -ne 0 -and $r.Out -match 'v99\.0\.0 was not found' -and $r.Out -match 'Fix:') "unknown release over HTTPS, no gh: exit $($r.Code), 'not found' message"
    $r = Invoke-Installer -Extra @('-Version', '99.0.0', '-UseGh')
    Assert-That ($r.Code -ne 0 -and $r.Out -match 'v99\.0\.0 was not found') "unknown release through gh: exit $($r.Code), 'not found' message"
    $r = Invoke-Installer -Extra @('-Repository', 'JonathanOll/dbexplorer-does-not-exist-xyz') -Env @{ PATH = $sys }
    Assert-That ($r.Code -ne 0 -and $r.Out -match 'ERROR:' -and $r.Out -match 'GITHUB_TOKEN') "unreachable repository over HTTPS: exit $($r.Code), points to GITHUB_TOKEN"
    $r = Invoke-Installer -Extra @('-Repository', 'JonathanOll/dbexplorer-does-not-exist-xyz', '-UseGh')
    Assert-That ($r.Code -ne 0 -and $r.Out -match 'ERROR:' -and $r.Out -match 'gh auth login') "unreachable repository through gh: exit $($r.Code), points to gh auth login"
    $r = Invoke-Installer -Env @{ PATH = $sys; GITHUB_TOKEN = 'ghp_invalid_token_for_the_test' }
    Assert-That ($r.Code -ne 0 -and $r.Out -match 'rejected the token' -and $r.Out -match 'GITHUB_TOKEN') "bad token, no gh: exit $($r.Code), says so with a fix"
    Assert-That (-not (Test-Path -LiteralPath $loc.App)) 'bad token creates nothing'
    $r = Invoke-Installer -Extra @('-Repository', 'not a repo')
    Assert-That ($r.Code -ne 0 -and $r.Out -match 'Invalid -Repository') 'bad -Repository is refused'
    $r = Invoke-Installer -Extra @('-RemoveUserData')
    Assert-That ($r.Code -ne 0 -and $r.Out -match 'only works together with -Uninstall') '-RemoveUserData without -Uninstall is refused'
    New-Item -ItemType Directory -Path $loc.App -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $loc.App 'precious.txt') -Value 'keep me'
    $r = Invoke-Installer
    Assert-That ($r.Code -ne 0 -and $r.Out -match 'does not look like a dbExplorer install' -and (Test-Path -LiteralPath (Join-Path $loc.App 'precious.txt'))) 'a foreign non-empty folder is refused and left intact'
    $r = Invoke-Installer -Extra @('-Uninstall')
    Assert-That ($r.Code -ne 0 -and (Test-Path -LiteralPath (Join-Path $loc.App 'precious.txt'))) 'uninstall refuses a foreign folder too'
    Reset-Sandbox

    Write-Scenario 'SHA-256 mismatch (fake gh)'
    $r = Invoke-Installer -Env ($shimEnv + @{ FAKE_GH_MODE = 'badhash' })
    Assert-That ($r.Code -ne 0 -and $r.Out -match 'SHA-256 mismatch') "mismatch aborts: exit $($r.Code)"
    Assert-That (-not (Test-Path -LiteralPath $loc.App) -and -not (Test-Path -LiteralPath $regKey)) 'mismatch installs nothing'

    Write-Scenario '-WhatIf'
    $r = Invoke-Installer -Extra @('-WhatIf')
    Assert-That ($r.Code -eq 0 -and $r.Out -match 'What ?if') "-WhatIf reports and exits 0 (exit $($r.Code))"
    Assert-That (-not (Test-Path -LiteralPath $loc.App) -and -not (Test-Path -LiteralPath $regKey) -and -not (Test-Path -LiteralPath $loc.Setup)) '-WhatIf changes nothing'

    Write-Scenario 'Fresh install of the latest release over plain HTTPS, gh NOT on PATH'
    $r = Invoke-Installer -Extra @('-DesktopShortcut') -Env @{ PATH = $sys }
    Assert-That ($r.Out -match 'sha256 verified') 'the download was verified against the GitHub digest'
    Write-Host ($r.Out -split "`n" | Select-Object -First 14 | ForEach-Object { "    | $_" }) -Separator "`n" -ForegroundColor DarkGray
    $exe = Join-Path $loc.App 'DbExplorer.exe'
    Assert-That ($r.Code -eq 0) "exit 0 (got $($r.Code))"
    Assert-That (Test-Path -LiteralPath $exe) 'DbExplorer.exe is in the install folder'
    Assert-That ((Get-ExeVersion) -like ($latestTag.TrimStart('v') + '*')) "the exe is the latest release ($(Get-ExeVersion))"
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $loc.App '.install-staging')) -and -not (Test-Path -LiteralPath (Join-Path $loc.App '.install-backup'))) 'no staging / backup folder left behind'
    Assert-That ((Get-ShortcutTarget (Join-Path $loc.Start 'dbExplorer.lnk')) -eq $exe) 'Start Menu shortcut targets the exe'
    Assert-That ((Get-ShortcutTarget (Join-Path $loc.Desk 'dbExplorer.lnk')) -eq $exe) 'desktop shortcut (opt-in) targets the exe'
    $reg = Get-RegistryEntry
    Assert-That ($null -ne $reg) 'the Apps & features key exists'
    if ($reg) {
        Assert-That ($reg.DisplayName -eq 'dbExplorer' -and $reg.DisplayVersion -eq $latestTag.TrimStart('v') -and $reg.Publisher -eq 'JonathanOll') 'DisplayName / DisplayVersion / Publisher'
        Assert-That ($reg.InstallLocation -eq $loc.App -and $reg.DisplayIcon -eq "$exe,0") 'InstallLocation / DisplayIcon'
        Assert-That ($reg.NoModify -eq 1 -and $reg.NoRepair -eq 1 -and $reg.EstimatedSize -gt 1000 -and $reg.URLInfoAbout -eq 'https://github.com/JonathanOll/dbExplorer') 'NoModify / NoRepair / EstimatedSize / URLInfoAbout'
        Assert-That ($reg.UninstallString -match '^powershell\.exe -NoProfile -ExecutionPolicy Bypass -File ".*install\.ps1" -Uninstall' -and $reg.QuietUninstallString -match '-Quiet -Force') 'UninstallString / QuietUninstallString call the setup copy with -Uninstall'
    }
    $setupScript = Join-Path $loc.Setup 'install.ps1'
    Assert-That ((Test-Path -LiteralPath $setupScript) -and -not $setupScript.StartsWith($loc.App)) 'the uninstaller copy exists outside the install folder'
    Assert-That (-not (Test-Path -LiteralPath $loc.Data)) 'the user data folder was not created or touched'
    Assert-That (@(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Filter 'dbExplorer-install-*' -Directory | Where-Object { $_.Name -notlike 'dbExplorer-install-test-*' -and $tempBefore -notcontains $_.Name }).Count -eq 0) 'no temp folder left behind'

    # The remaining scenarios reuse one downloaded zip (fake gh serving it) instead of downloading 70 MB again each time.
    $cache = Join-Path $sandbox 'cache'
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    & gh release download $latestTag --repo JonathanOll/dbExplorer --pattern DbExplorer.zip --dir $cache --clobber 2>&1 | Out-Null
    $cachedEnv = $shimEnv + @{ FAKE_GH_MODE = 'cachedzip'; FAKE_GH_ZIP = (Join-Path $cache 'DbExplorer.zip') }

    Write-Scenario 'Run again: already installed'
    $stamp = (Get-Item -LiteralPath $exe).LastWriteTimeUtc
    $r = Invoke-Installer
    Assert-That ($r.Code -eq 0 -and $r.Out -match 'already installed') "exit 0 and 'already installed' (exit $($r.Code))"
    Assert-That ((Get-Item -LiteralPath $exe).LastWriteTimeUtc -eq $stamp) 'nothing was touched'

    Write-Scenario '-Force reinstall'
    Set-Content -LiteralPath (Join-Path $loc.App 'leftover.txt') -Value 'x'
    $r = Invoke-Installer -Extra @('-Force') -Env $cachedEnv
    Assert-That ($r.Code -eq 0 -and $r.Out -match 'installed|updated') "-Force reinstalls (exit $($r.Code))"
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $loc.App 'leftover.txt')) -and (Test-Path -LiteralPath $exe)) 'the folder content was replaced'

    Write-Scenario 'Upgrade from an older version (stub exe stamped 0.9.0)'
    Remove-Item -LiteralPath $loc.App -Recurse -Force
    New-Item -ItemType Directory -Path $loc.App | Out-Null
    New-StubExe $exe '0.9.0'
    Set-Content -LiteralPath (Join-Path $loc.App 'old.dll') -Value 'old'
    Assert-That ((Get-ExeVersion) -like '0.9.0*') 'the stub reports 0.9.0'
    $r = Invoke-Installer -Env $cachedEnv
    Assert-That ($r.Code -eq 0 -and $r.Out -match 'Update 0\.9\.0 -> |updated') "older version is upgraded (exit $($r.Code))"
    Assert-That ((Get-ExeVersion) -like ($latestTag.TrimStart('v') + '*') -and -not (Test-Path -LiteralPath (Join-Path $loc.App 'old.dll'))) 'the new files replaced the old ones'
    Assert-That ((Get-RegistryEntry).DisplayVersion -eq $latestTag.TrimStart('v')) 'the registry version follows'

    Write-Scenario 'Running app'
    Remove-Item -LiteralPath $loc.App -Recurse -Force
    New-Item -ItemType Directory -Path $loc.App | Out-Null
    New-StubExe $exe '0.9.0'
    $proc = Start-Process -FilePath $exe -WorkingDirectory $loc.App -WindowStyle Hidden -PassThru
    $startedProcesses.Add($proc)
    Start-Sleep -Milliseconds 800
    $r = Invoke-Installer -Env $cachedEnv
    Assert-That ($r.Code -ne 0 -and $r.Out -match 'is running' -and $r.Out -match '-Force') "running app without -Force: refused (exit $($r.Code))"
    Assert-That ((-not $proc.HasExited) -and (Get-ExeVersion) -like '0.9.0*') 'the app was not killed and the files are untouched'
    $r = Invoke-Installer -Extra @('-Force', '-CloseTimeoutSec', '3') -Env $cachedEnv
    $proc.Refresh()
    Assert-That ($r.Code -eq 0 -and $proc.HasExited) "-Force closes the app and installs (exit $($r.Code))"
    Assert-That ($r.Out -match 'Closing dbExplorer' -and $r.Out -match 'killing it') 'the close and the kill were announced, never silent'
    Assert-That ((Get-ExeVersion) -like ($latestTag.TrimStart('v') + '*')) 'the new version is installed'

    Write-Scenario 'In-app update simulation, then uninstall through the registry command'
    # The in-app updater empties the install folder and puts the zip content back: the setup copy must survive it.
    Get-ChildItem -LiteralPath $loc.App -Force | Remove-Item -Recurse -Force
    New-StubExe $exe '1.0.5'
    New-Item -ItemType Directory -Path $loc.Data -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $loc.Data 'config.json') -Value '{"keep":true}'
    Assert-That (Test-Path -LiteralPath (Join-Path $loc.Setup 'install.ps1')) 'the setup copy survived the emptied install folder'
    $quiet = (Get-RegistryEntry).QuietUninstallString
    $proc = Start-Process -FilePath "$env:SystemRoot\System32\cmd.exe" -ArgumentList "/c `"$quiet`"" -Wait -PassThru -WindowStyle Hidden
    Assert-That ($proc.ExitCode -eq 0) "QuietUninstallString runs and exits 0 (got $($proc.ExitCode))"
    Assert-That (-not (Test-Path -LiteralPath $loc.App)) 'install folder removed'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $loc.Start 'dbExplorer.lnk')) -and -not (Test-Path -LiteralPath (Join-Path $loc.Desk 'dbExplorer.lnk'))) 'shortcuts removed'
    Assert-That (-not (Test-Path -LiteralPath $regKey)) 'registry key removed'
    Assert-That (Wait-Until { -not (Test-Path -LiteralPath $loc.Setup) }) 'the setup copy deleted itself after exit'
    Assert-That ((Get-Content -LiteralPath (Join-Path $loc.Data 'config.json') -Raw) -match 'keep') 'user data untouched'

    Write-Scenario 'Idempotent uninstall'
    $r = Invoke-Installer -Extra @('-Uninstall')
    Assert-That ($r.Code -eq 0 -and $r.Out -match 'not installed') "nothing installed: exit 0 and a clear message (exit $($r.Code))"

    Write-Scenario '-Uninstall -RemoveUserData'
    $r = Invoke-Installer -Extra @('-Uninstall', '-RemoveUserData')
    Assert-That ($r.Code -eq 0 -and (Test-Path -LiteralPath $loc.Data)) 'without -Force and without a console: the data is kept (confirmation required)'
    $r = Invoke-Installer -Extra @('-Uninstall', '-RemoveUserData', '-Force')
    Assert-That ($r.Code -eq 0 -and -not (Test-Path -LiteralPath $loc.Data)) '-Force deletes the user data folder (test folder only)'

    Write-Scenario 'Piped forms: script block with parameters (real install), then Invoke-Expression'
    $sbArgs = "-UseGh -InstallDir '$($loc.App)' -SetupDir '$($loc.Setup)' -StartMenuDir '$($loc.Start)' -DesktopDir '$($loc.Desk)' -RegistryKey '$regKey' -UserDataDir '$($loc.Data)'"
    $cmd = "& ([scriptblock]::Create([IO.File]::ReadAllText('$install'))) -Version '$latestTag' $sbArgs"
    $r = Invoke-Child -Arguments @('-NoProfile', '-Command', $cmd) -Env $cachedEnv
    Assert-That ($r.Code -eq 0 -and (Test-Path -LiteralPath $exe)) "script block form installs with parameters (exit $($r.Code))"
    $copy = Join-Path $loc.Setup 'install.ps1'
    Assert-That ((Test-Path -LiteralPath $copy) -and ([IO.File]::ReadAllText($copy).Replace("`r", '').TrimEnd() -eq [IO.File]::ReadAllText($install).Replace("`r", '').TrimEnd())) 'the setup copy written from the script block text equals install.ps1'
    $r = Invoke-Child -Arguments (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $copy, '-Uninstall', '-Quiet', '-Force') + $seamArgs) -Env @{}
    Assert-That ($r.Code -eq 0 -and -not (Test-Path -LiteralPath $loc.App) -and -not (Test-Path -LiteralPath $regKey)) 'uninstall through the setup copy written from the script block works'
    Assert-That (Wait-Until { -not (Test-Path -LiteralPath $loc.Setup) }) 'setup folder gone'
    # `| iex` cannot take seams: run it where it fails before touching anything. A bad token makes the HTTPS path fail (401),
    # then the fake gh (wrong digest) is the fallback: proves it parses, runs, falls back and reports an exit code.
    $r = Invoke-Child -Arguments @('-NoProfile', '-Command', "[IO.File]::ReadAllText('$install') | Out-String | iex") -Env ($shimEnv + @{ FAKE_GH_MODE = 'badhash'; GITHUB_TOKEN = 'ghp_invalid_token_for_the_test' })
    Assert-That ($r.Code -ne 0 -and $r.Out -match 'SHA-256 mismatch' -and $r.Out -match 'trying gh') "Invoke-Expression form runs, falls back to gh and fails with a non-zero exit (exit $($r.Code))"

    Write-Scenario 'Piped script block over HTTPS with parameters, gh NOT on PATH (-Version of the latest, sandbox seams)'
    $sbArgs2 = "-InstallDir '$($loc.App)' -SetupDir '$($loc.Setup)' -StartMenuDir '$($loc.Start)' -DesktopDir '$($loc.Desk)' -RegistryKey '$regKey' -UserDataDir '$($loc.Data)'"
    $r = Invoke-Child -Arguments @('-NoProfile', '-Command', "& ([scriptblock]::Create([IO.File]::ReadAllText('$install'))) -Version '$latestTag' -WhatIf $sbArgs2") -Env @{ PATH = $sys }
    Assert-That ($r.Code -eq 0 -and $r.Out -match 'What ?if' -and -not (Test-Path -LiteralPath $loc.App)) "HTTPS script block form with -WhatIf (exit $($r.Code))"
}
finally {
    Write-Scenario 'Cleanup and leak check'
    foreach ($p in $startedProcesses) { try { if (-not $p.HasExited) { $p.Kill() } } catch { } }
    Get-Process -Name DbExplorer -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -like "$sandbox*" } catch { $false } } | ForEach-Object { try { $_.Kill() } catch { } }
    Start-Sleep -Milliseconds 500
    if (Test-Path -LiteralPath $regKey) { Remove-Item -LiteralPath $regKey -Recurse -Force -ErrorAction SilentlyContinue }
    $gone = Wait-Until { try { if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction Stop }; $true } catch { $false } } 15
    Assert-That $gone 'the sandbox folder was deleted'
    Assert-That (-not (Test-Path -LiteralPath $regKey)) 'the test registry key was deleted'
    $realAfter = & $realState
    foreach ($k in $realBefore.Keys) {
        Assert-That ($realBefore[$k] -eq $realAfter[$k]) "real $k state unchanged ($($realBefore[$k]))"
    }
    $tempAfter = @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Filter 'dbExplorer-install-*' -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    Assert-That (@($tempAfter | Where-Object { $tempBefore -notcontains $_ }).Count -eq 0) 'no dbExplorer-install-* temp folder left behind'
}

Write-Host ''
if ($script:Failures.Count -eq 0) {
    Write-Host "ALL $($script:Passed) CHECKS PASSED ($Engine)" -ForegroundColor Green
    exit 0
}
Write-Host "$($script:Failures.Count) FAILED, $($script:Passed) passed ($Engine):" -ForegroundColor Red
$script:Failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
