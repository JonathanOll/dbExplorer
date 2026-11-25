<#
.SYNOPSIS
Installs, updates or uninstalls dbExplorer from its GitHub releases. One PowerShell line is enough, nothing else to install.

.DESCRIPTION
Per-user installer for Windows 10/11 (Windows PowerShell 5.1 or PowerShell 7), no administrator rights, no .NET, no
GitHub CLI needed: it talks to GitHub over plain HTTPS.

One-line install (or update):
  irm https://raw.githubusercontent.com/JonathanOll/dbExplorer/main/install.ps1 | iex

Install / update (default):
  1. checks the prerequisites (Windows, a writable install folder),
  2. asks GitHub for the release (REST API over HTTPS): the latest one, or -Version,
  3. downloads DbExplorer.zip and verifies its SHA-256 against the digest GitHub gives for the asset (or, when
     there is none, against the DbExplorer.zip.sha256 asset; with neither the install is refused),
  4. extracts it to a staging folder and checks it holds DbExplorer.exe,
  5. closes the running app if needed, then swaps the files in, with a rollback to the previous
     version on failure,
  6. copies this script outside the install folder, adds the Start Menu shortcut (desktop shortcut
     on request) and the "Apps & features" entry.
If the same version (or a newer one) is already installed nothing is done, unless -Force.
An older version is upgraded: run the same command again to update.

The GitHub CLI (gh) is optional: it is only used when HTTPS failed and gh is installed, or when -UseGh is given.
Set $env:GITHUB_TOKEN (or $env:GH_TOKEN) to lift GitHub's anonymous rate limit (60 requests/hour) or to install from a
private repository.

Where things go (defaults):
  App files        %LOCALAPPDATA%\Programs\dbExplorer (-InstallDir; the in-app updater needs it writable)
  Uninstaller      %LOCALAPPDATA%\Programs\dbExplorer-setup\install.ps1, a copy of this script. It lives
                   OUTSIDE the install folder because the in-app updater empties that folder.
                   Run it with -Uninstall (or use Apps & features).
  Start Menu       %APPDATA%\Microsoft\Windows\Start Menu\Programs\dbExplorer.lnk
  Apps & features  HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\dbExplorer
  User data        %APPDATA%\dbExplorer (config, sessions, scripts): never touched, except by
                   -Uninstall -RemoveUserData.

The install folder is owned by the installer: a new install refuses a non-empty folder that holds no
DbExplorer.exe, and an install or update replaces everything in it (like the in-app updater does).

Exit code: 0 on success (or nothing to do), 1 on any error (a cause line and a fix line).
Supports -WhatIf and -Confirm (not through `| iex`, which has no parameters: use the script block form).

.PARAMETER InstallDir
Where to install. Default: %LOCALAPPDATA%\Programs\dbExplorer.

.PARAMETER Version
Release to install: a tag (v1.0.4) or a version (1.0.4). Default: the latest release.

.PARAMETER Repository
GitHub repository (owner/name) to install from. Default: JonathanOll/dbExplorer.

.PARAMETER DesktopShortcut
Also create a desktop shortcut (the Start Menu shortcut is created by default).

.PARAMETER NoStartMenu
Do not create the Start Menu shortcut.

.PARAMETER Launch
Start dbExplorer at the end of the installation.

.PARAMETER Force
Reinstall even when the same (or a newer) version is installed; close a running dbExplorer without asking (politely first,
killed after a timeout); with -Uninstall -RemoveUserData, skip the confirmation.

.PARAMETER Uninstall
Remove dbExplorer: files, shortcuts, "Apps & features" entry and the setup copy. User data is kept.

.PARAMETER RemoveUserData
Only with -Uninstall: also delete the user data folder (%APPDATA%\dbExplorer, or DBEXPLORER_DATA_DIR when set).
Asks for confirmation unless -Force.

.PARAMETER UseGh
Use the GitHub CLI (gh) instead of plain HTTPS to query and download the release. Needs gh installed (winget install --id GitHub.cli).

.PARAMETER Quiet
No banner, steps or summary: only warnings and errors.

.PARAMETER StartMenuDir
Test seam. Folder of the Start Menu shortcut. Default: the user's Programs folder.

.PARAMETER DesktopDir
Test seam. Folder of the desktop shortcut. Default: the user's desktop.

.PARAMETER RegistryKey
Test seam. Registry key of the "Apps & features" entry. Default: HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\dbExplorer.

.PARAMETER SetupDir
Test seam. Folder that receives the setup copy. Default: %LOCALAPPDATA%\Programs\dbExplorer-setup.

.PARAMETER UserDataDir
Test seam. User data folder used by -RemoveUserData. Default: DBEXPLORER_DATA_DIR, else %APPDATA%\dbExplorer.

.PARAMETER CloseTimeoutSec
Seconds to wait for a running dbExplorer to close before it is killed (-Force). Default: 20.

.PARAMETER PauseAtEnd
Wait for Enter at the end (used by the "Apps & features" uninstall command so its window stays readable).

.EXAMPLE
irm https://raw.githubusercontent.com/JonathanOll/dbExplorer/main/install.ps1 | iex
Installs (or updates to) the latest release, straight from GitHub, nothing else needed.

.EXAMPLE
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/JonathanOll/dbExplorer/main/install.ps1))) -Version 1.0.4 -DesktopShortcut -Launch
The same one-liner with parameters: installs v1.0.4, adds a desktop shortcut and starts the app.

.EXAMPLE
.\install.ps1
Installs (or updates to) the latest release for the current user.

.EXAMPLE
.\install.ps1 -Version 1.0.4 -DesktopShortcut -Launch
Installs v1.0.4, adds a desktop shortcut and starts the app.

.EXAMPLE
.\install.ps1 -Uninstall
Removes dbExplorer and keeps the user data.

.EXAMPLE
.\install.ps1 -Uninstall -RemoveUserData -Force
Removes dbExplorer and its user data without asking.

.NOTES
No gh needed. If GitHub answers "rate limit", set a token: $env:GITHUB_TOKEN = '<personal access token>' and run again.
.LINK
https://github.com/JonathanOll/dbExplorer
#>
#Requires -Version 5.1
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '')]
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallDir = '',
    [string]$Version = '',
    [string]$Repository = 'JonathanOll/dbExplorer',
    [switch]$DesktopShortcut,
    [switch]$NoStartMenu,
    [switch]$Launch,
    [switch]$Force,
    [switch]$Uninstall,
    [switch]$RemoveUserData,
    [switch]$UseGh,
    [switch]$Quiet,
    [Parameter(DontShow)][string]$StartMenuDir = '',
    [Parameter(DontShow)][string]$DesktopDir = '',
    [Parameter(DontShow)][string]$RegistryKey = '',
    [Parameter(DontShow)][string]$SetupDir = '',
    [Parameter(DontShow)][string]$UserDataDir = '',
    [Parameter(DontShow)][int]$CloseTimeoutSec = 20,
    [Parameter(DontShow)][switch]$PauseAtEnd
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# What this script knows about itself. Captured here, at script level: inside a function $MyInvocation is the function's.
# $PSCommandPath is empty when the script is not a file (run through a script block or Invoke-Expression).
$script:SelfPath = $PSCommandPath
$script:SelfText = ''
if (-not $script:SelfPath) {
    try { $script:SelfText = $MyInvocation.MyCommand.ScriptBlock.ToString() } catch { $script:SelfText = '' }
}

$script:AppName = 'dbExplorer'
$script:ExeName = 'DbExplorer.exe'
$script:AssetName = 'DbExplorer.zip'
$script:HttpClient = $null
$script:Quiet = $false
$script:LineOpen = $false
$script:StepNo = 0
$script:StepTotal = 0

# ---------------------------------------------------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------------------------------------------------

function Write-Ui {
    param([string]$Text = '', [string]$Color = '', [switch]$NoNewline)
    if ($script:Quiet) { return }
    $p = @{ Object = $Text; NoNewline = [bool]$NoNewline }
    if ($Color) { $p.ForegroundColor = $Color }
    Write-Host @p
}

# Finishes the open "[n/N] text ... " line so a message can start on its own line.
function Close-OpenLine {
    if ($script:LineOpen) { Write-Host ''; $script:LineOpen = $false }
}

function Start-Step {
    param([string]$Text)
    $script:StepNo++
    if ($script:Quiet) { return }
    Write-Ui ('[{0}/{1}] {2} ... ' -f $script:StepNo, $script:StepTotal, $Text) -NoNewline
    $script:LineOpen = $true
}

function Complete-Step {
    param([string]$Result = 'OK', [string]$Color = 'Green')
    if (-not $script:LineOpen) { Write-Ui '      ' -NoNewline }
    Write-Ui $Result $Color
    $script:LineOpen = $false
}

function Write-Detail {
    param([string]$Text)
    Close-OpenLine
    Write-Ui ('      ' + $Text) 'DarkGray'
}

function Write-Warn {
    param([string]$Text)
    Close-OpenLine
    Write-Host ('WARNING: ' + $Text) -ForegroundColor Yellow
}

function Write-Banner {
    param([string]$Title, [string]$Subtitle)
    Write-Ui ''
    Write-Ui ('  ' + $Title) 'Cyan'
    Write-Ui ('  ' + ('=' * $Title.Length)) 'Cyan'
    Write-Ui ('  ' + $Subtitle) 'DarkGray'
    Write-Ui ''
}

function Write-Summary {
    param([string]$Title, [string[]]$Lines)
    Write-Ui ''
    Write-Ui '  +----------------------------------------------------------------' 'Green'
    Write-Ui ('  | ' + $Title) 'Green'
    Write-Ui '  |' 'Green'
    foreach ($l in $Lines) { Write-Ui ('  | ' + $l) }
    Write-Ui '  +----------------------------------------------------------------' 'Green'
    Write-Ui ''
}

# Throws an error the entry point reports as "ERROR: cause" + "Fix: ...".
function Stop-Installer {
    param([string]$Message, [string]$Fix = '')
    $ex = New-Object System.InvalidOperationException $Message
    $ex.Data['DbExplorerFix'] = $Fix
    throw $ex
}

# ---------------------------------------------------------------------------------------------------------------------
# Helpers (pure or nearly so: these are what the tests load by dot-sourcing the script)
# ---------------------------------------------------------------------------------------------------------------------

# Parses "v1.2.3", "1.2.3", "1.2.3-beta.1", "1.2.3+sha"; $null when it is not a semantic version.
function ConvertTo-SemVer {
    param([string]$Text)
    if ($Text -notmatch '^\s*[vV]?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+\S*)?\s*$') { return $null }
    $pre = ''
    if ($Matches.ContainsKey(4)) { $pre = [string]$Matches[4] }
    $core = '{0}.{1}.{2}' -f $Matches[1], $Matches[2], $Matches[3]
    [pscustomobject]@{
        Major = [long]$Matches[1]; Minor = [long]$Matches[2]; Patch = [long]$Matches[3]; Pre = $pre
        Text  = $(if ($pre) { $core + '-' + $pre } else { $core })
    }
}

# -1, 0, 1 following semver 2.0 (a prerelease sorts before its release).
function Compare-SemVer {
    param($A, $B)
    foreach ($f in 'Major', 'Minor', 'Patch') {
        if ($A.$f -ne $B.$f) { return $(if ($A.$f -lt $B.$f) { -1 } else { 1 }) }
    }
    if ($A.Pre -eq $B.Pre) { return 0 }
    if (-not $A.Pre) { return 1 }
    if (-not $B.Pre) { return -1 }
    $xs = $A.Pre.Split('.'); $ys = $B.Pre.Split('.')
    for ($i = 0; $i -lt [Math]::Min($xs.Count, $ys.Count); $i++) {
        $xn = $xs[$i] -match '^\d+$'; $yn = $ys[$i] -match '^\d+$'
        if ($xn -and $yn) { $c = ([decimal]$xs[$i]).CompareTo([decimal]$ys[$i]) }
        elseif ($xn) { $c = -1 }
        elseif ($yn) { $c = 1 }
        else { $c = [string]::CompareOrdinal($xs[$i], $ys[$i]) }
        if ($c -ne 0) { return [Math]::Sign($c) }
    }
    return [Math]::Sign($xs.Count - $ys.Count)
}

# "1.0.4" or "v1.0.4" -> "v1.0.4"; empty -> empty (latest); anything else -> $null.
function ConvertTo-ReleaseTag {
    param([string]$Version)
    if ([string]::IsNullOrWhiteSpace($Version)) { return '' }
    $v = ConvertTo-SemVer $Version
    if ($null -eq $v -or $Version -match '\+') { return $null }
    return 'v' + $v.Text
}

function Test-RepositoryName {
    param([string]$Repository)
    return ($Repository -match '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')
}

# Reads $Object.$Name without failing under strict mode when the property is absent.
function Get-OptionalProperty {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

# $Cmdlet is $null when the script runs through Invoke-Expression (no $PSCmdlet there): then there is no -WhatIf/-Confirm.
function Test-ShouldProcess {
    param($Cmdlet, [string]$Target, [string]$Action)
    if ($null -eq $Cmdlet) { return $true }
    return $Cmdlet.ShouldProcess($Target, $Action)
}

function Get-FirstNonEmpty {
    param([string]$Given, [string]$Default)
    if ($Given) { return $Given }
    return $Default
}

# The release asset to install and what is known about it. $null when the release has no zip.
function Select-ReleaseAsset {
    param($Release)
    $assets = @(Get-OptionalProperty $Release 'assets')
    $asset = $assets | Where-Object { (Get-OptionalProperty $_ 'name') -eq $script:AssetName } | Select-Object -First 1
    if (-not $asset) {
        $asset = $assets | Where-Object { [string](Get-OptionalProperty $_ 'name') -like '*.zip' } | Select-Object -First 1
    }
    if (-not $asset) { return $null }
    $digest = [string](Get-OptionalProperty $asset 'digest')
    $sha = ''
    if ($digest -match '^sha256:([0-9A-Fa-f]{64})$') { $sha = $Matches[1].ToLowerInvariant() }
    $name = [string]$asset.name
    $side = $assets | Where-Object { [string](Get-OptionalProperty $_ 'name') -eq ($name + '.sha256') } | Select-Object -First 1
    [pscustomobject]@{
        Name       = $name
        Size       = [long](Get-OptionalProperty $asset 'size')
        Sha256     = $sha
        Url        = [string](Get-OptionalProperty $asset 'browser_download_url')
        ApiUrl     = [string](Get-OptionalProperty $asset 'url')
        SidecarUrl = [string](Get-OptionalProperty $side 'browser_download_url')
        SidecarApi = [string](Get-OptionalProperty $side 'url')
    }
}

# Maps the stderr of a failed gh call to a one-line cause and a fix.
function Get-GhFailure {
    param([string]$StdErr, [string]$Tag, [string]$Repository)
    $first = ''
    $lines = @($StdErr -split "`n" | Where-Object { $_.Trim() })
    if ($lines.Count -gt 0) { $first = $lines[0].Trim() }
    $login = 'Run `gh auth login` (the repository may be private), then retry.'
    if ($StdErr -match 'rate limit') {
        return @{ Message = 'GitHub rate limit reached.'; Fix = 'Run `gh auth login` to get a higher limit, or retry later.' }
    }
    if ($Tag -and $StdErr -match 'release not found|Not Found|HTTP 404') {
        return @{ Message = "Release $Tag was not found in $Repository."; Fix = "Check the version, see https://github.com/$Repository/releases." }
    }
    if ($StdErr -match 'HTTP 404|Not Found|Could not resolve to a Repository|no releases found') {
        return @{ Message = "No release reachable in $Repository (missing, private, or not logged in)."; Fix = $login }
    }
    if ($StdErr -match 'auth|HTTP 40[13]|log ?in|token|credentials') {
        return @{ Message = 'gh is not authorized to read this repository.'; Fix = $login }
    }
    if ($StdErr -match 'dial tcp|no such host|timeout|timed out|network|connection|unreachable|EOF') {
        return @{ Message = 'GitHub is not reachable (network error).'; Fix = 'Check your connection or proxy and retry.' }
    }
    return @{ Message = "gh failed: $first"; Fix = 'Run the same gh command by hand to see the full error.' }
}

# ---------------------------------------------------------------------------------------------------------------------
# GitHub over plain HTTPS (default: no gh needed)
# ---------------------------------------------------------------------------------------------------------------------

# $env:GITHUB_TOKEN, else $env:GH_TOKEN: lifts the anonymous rate limit and allows private repositories. '' when unset.
function Get-GitHubToken {
    foreach ($n in 'GITHUB_TOKEN', 'GH_TOKEN') {
        $v = [Environment]::GetEnvironmentVariable($n)
        if ($v -and $v.Trim()) { return $v.Trim() }
    }
    return ''
}

function Initialize-Http {
    if ($script:HttpClient) { return }
    # Windows PowerShell 5.1 may default to TLS 1.0/1.1, which GitHub refuses.
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false   # redirects are followed by hand so the token never leaves api.github.com
    try {
        $proxy = [Net.WebRequest]::GetSystemWebProxy()
        $proxy.Credentials = [Net.CredentialCache]::DefaultCredentials
        $handler.Proxy = $proxy
    }
    catch { }
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(60)
    [void]$client.DefaultRequestHeaders.UserAgent.TryParseAdd('dbExplorer-installer')
    $script:HttpClient = $client
}

# GET with redirects followed by hand. Returns the response (the caller disposes it); network problems become installer errors.
function Send-HttpGet {
    param([string]$Url, [string]$Accept = 'application/vnd.github+json')
    Initialize-Http
    $token = Get-GitHubToken
    for ($hop = 0; $hop -le 5; $hop++) {
        $uri = [Uri]$Url
        $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $uri)
        [void]$req.Headers.TryAddWithoutValidation('Accept', $Accept)
        [void]$req.Headers.TryAddWithoutValidation('X-GitHub-Api-Version', '2022-11-28')
        if ($token -and $uri.Host -eq 'api.github.com') { [void]$req.Headers.TryAddWithoutValidation('Authorization', "Bearer $token") }
        try { $resp = $script:HttpClient.SendAsync($req, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult() }
        catch {
            $m = $_.Exception.Message
            if ($_.Exception -is [System.Threading.Tasks.TaskCanceledException] -or $_.Exception -is [OperationCanceledException]) {
                Stop-Installer "GitHub did not answer in time ($($uri.Host))." 'Check your connection, proxy or firewall and retry.'
            }
            Stop-Installer "GitHub is not reachable ($($uri.Host)): $m" 'Check your connection, proxy or firewall (https://api.github.com must be reachable) and retry.'
        }
        $code = [int]$resp.StatusCode
        if ($code -in 301, 302, 303, 307, 308 -and $resp.Headers.Location) {
            $Url = ([Uri]::new($uri, $resp.Headers.Location)).ToString()
            $resp.Dispose()
            continue
        }
        return $resp
    }
    Stop-Installer 'GitHub redirected the request too many times.' 'Retry later.'
}

# Maps a failed HTTP answer to a one-line cause and a fix.
function Get-HttpFailure {
    param($Response, [string]$Tag, [string]$Repository)
    $code = [int]$Response.StatusCode
    $body = ''
    try { $body = $Response.Content.ReadAsStringAsync().GetAwaiter().GetResult() } catch { }
    $remaining = $null
    $vals = $null
    if ($Response.Headers.TryGetValues('X-RateLimit-Remaining', [ref]$vals)) { $remaining = @($vals)[0] }
    if (($code -eq 403 -or $code -eq 429) -and ($remaining -eq '0' -or $code -eq 429 -or $body -match 'rate limit')) {
        return @{ Message = 'GitHub rate limit reached (60 anonymous requests per hour).'; Fix = 'Set a token and retry: $env:GITHUB_TOKEN = ''<personal access token>'' (or wait an hour).' }
    }
    if ($code -eq 404 -and $Tag) {
        return @{ Message = "Release $Tag was not found in $Repository."; Fix = "Check the version, see https://github.com/$Repository/releases." }
    }
    if ($code -eq 404) {
        return @{ Message = "No release found in $Repository (it has none, or the repository is private)."; Fix = 'Check -Repository; for a private repository set $env:GITHUB_TOKEN.' }
    }
    if ($code -eq 401) {
        return @{ Message = 'GitHub rejected the token (HTTP 401).'; Fix = 'Check $env:GITHUB_TOKEN / $env:GH_TOKEN, or remove it.' }
    }
    return @{ Message = "GitHub answered HTTP $code."; Fix = 'Retry later; if it persists, try -UseGh.' }
}

# The release as {tagName, assets[]} (assets keep name, size, digest, url, browser_download_url). Throws installer errors.
function Get-ReleaseHttp {
    param([string]$Repository, [string]$Tag)
    $path = if ($Tag) { "repos/$Repository/releases/tags/$([Uri]::EscapeDataString($Tag))" } else { "repos/$Repository/releases/latest" }
    $resp = Send-HttpGet "https://api.github.com/$path"
    try {
        if (-not $resp.IsSuccessStatusCode) {
            $f = Get-HttpFailure $resp $Tag $Repository
            Stop-Installer $f.Message $f.Fix
        }
        $text = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }
    finally { $resp.Dispose() }
    try { $r = $text | ConvertFrom-Json }
    catch { Stop-Installer 'GitHub answered with something that is not JSON.' 'Retry later, or try -UseGh.' }
    [pscustomobject]@{ tagName = [string](Get-OptionalProperty $r 'tag_name'); assets = @(Get-OptionalProperty $r 'assets') }
}

# Small text file (a .sha256 sidecar, the script itself). $null on any failure.
function Get-HttpText {
    param([string]$Url, [string]$Accept = 'application/octet-stream')
    try {
        $resp = Send-HttpGet $Url $Accept
        try {
            if (-not $resp.IsSuccessStatusCode) { return $null }
            return $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        }
        finally { $resp.Dispose() }
    }
    catch { return $null }
}

# Downloads the release asset to $Directory with a progress bar. Throws installer errors.
function Invoke-HttpDownload {
    param($Asset, [string]$Tag, [string]$Repository, [string]$Directory)
    # With a token (private repository) the API url is the one that accepts it; without, the public url spares the API quota.
    $url = $Asset.Url
    if ((Get-GitHubToken) -and $Asset.ApiUrl) { $url = $Asset.ApiUrl }
    if (-not $url) { $url = $Asset.ApiUrl }
    if (-not $url) { Stop-Installer "GitHub gives no download address for $($Asset.Name)." "Check https://github.com/$Repository/releases/tag/$Tag." }
    $resp = Send-HttpGet $url 'application/octet-stream'
    $file = Join-Path $Directory $Asset.Name
    try {
        if (-not $resp.IsSuccessStatusCode) {
            $f = Get-HttpFailure $resp $Tag $Repository
            Stop-Installer "Download failed: $($f.Message)" $f.Fix
        }
        $total = $Asset.Size
        $len = $resp.Content.Headers.ContentLength
        if ($len) { $total = [long]$len }
        $in = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $out = [IO.File]::Create($file)
        try {
            $buf = New-Object byte[] 262144
            $done = [long]0
            $clock = [Diagnostics.Stopwatch]::StartNew()
            while (($n = $in.Read($buf, 0, $buf.Length)) -gt 0) {
                $out.Write($buf, 0, $n)
                $done += $n
                if (-not $script:Quiet -and $total -gt 0 -and $clock.ElapsedMilliseconds -ge 200) {
                    $pct = [int][Math]::Min(100, [Math]::Floor(100.0 * $done / $total))
                    Write-Progress -Activity "Downloading $($Asset.Name)" -Status ('{0:N1} / {1:N1} MB' -f ($done / 1MB), ($total / 1MB)) -PercentComplete $pct
                    $clock.Restart()
                }
            }
        }
        catch [System.IO.IOException] {
            Stop-Installer "Download interrupted: $($_.Exception.Message)" 'Check your connection and retry.'
        }
        finally {
            $out.Dispose()
            $in.Dispose()
            Write-Progress -Activity "Downloading $($Asset.Name)" -Completed
        }
    }
    catch {
        Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
        throw
    }
    finally { $resp.Dispose() }
}

# The expected SHA-256 of the asset from its .sha256 sidecar asset; '' when there is none.
function Get-Sha256Sidecar {
    param($Asset)
    $url = $Asset.SidecarUrl
    if ((Get-GitHubToken) -and $Asset.SidecarApi) { $url = $Asset.SidecarApi }
    if (-not $url) { return '' }
    $t = Get-HttpText $url
    if ($t -and $t -match '(?m)^\s*([0-9A-Fa-f]{64})\b') { return $Matches[1].ToLowerInvariant() }
    return ''
}

function Test-GhAvailable {
    return [bool](Get-Command gh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)
}

# The release through gh, shaped like Get-ReleaseHttp's answer. Throws installer errors.
function Get-ReleaseGh {
    param([string]$Repository, [string]$Tag)
    $view = @('release', 'view')
    if ($Tag) { $view += $Tag }
    $view += @('--repo', $Repository, '--json', 'tagName,assets')
    $r = Invoke-Gh $view
    if ($r.Code -ne 0) {
        $f = Get-GhFailure $r.Err $Tag $Repository
        Stop-Installer $f.Message $f.Fix
    }
    try { return ($r.Out | ConvertFrom-Json) }
    catch { Stop-Installer 'GitHub answered with something that is not JSON.' 'Retry later; if it persists run `gh release view` by hand.' }
}

function Get-GhPath {
    $cmd = Get-Command gh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $cmd) {
        Stop-Installer 'The GitHub CLI (gh) is not installed or not on PATH.' 'Install it with `winget install --id GitHub.cli`, open a new terminal, then retry.'
    }
    return $cmd.Source
}

# Runs gh, never throws for a non-zero exit: returns Code, Out and Err. stderr is kept apart (no NativeCommandError records).
function Invoke-Gh {
    param([string[]]$Arguments)
    $gh = Get-GhPath
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $raw = @(& $gh @Arguments 2>&1) }
    finally { $ErrorActionPreference = $prev }
    $code = $LASTEXITCODE
    $out = @($raw | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join "`n"
    $err = @($raw | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join "`n"
    [pscustomobject]@{ Code = $code; Out = $out; Err = $err }
}

# Runs `gh release download` in a background job so the size of the growing file can drive a progress bar.
function Invoke-GhDownload {
    param([string]$Tag, [string]$Repository, [string]$AssetName, [string]$Directory, [long]$Size)
    $gh = Get-GhPath
    $ghArgs = [string[]]@('release', 'download', $Tag, '--repo', $Repository, '--pattern', $AssetName, '--dir', $Directory, '--clobber')
    $job = Start-Job -ArgumentList @($gh, $ghArgs) -ScriptBlock {
        param($exe, $a)
        $ErrorActionPreference = 'Continue'
        $o = @(& $exe @a 2>&1)
        [pscustomobject]@{ Code = $LASTEXITCODE; Text = (($o | ForEach-Object { "$_" }) -join "`n") }
    }
    try {
        $file = Join-Path $Directory $AssetName
        while ($job.State -eq 'Running') {
            if (-not $script:Quiet -and $Size -gt 0 -and (Test-Path -LiteralPath $file)) {
                $len = (Get-Item -LiteralPath $file).Length
                $pct = [int][Math]::Min(100, [Math]::Floor(100.0 * $len / $Size))
                Write-Progress -Activity "Downloading $AssetName" -Status ('{0:N1} / {1:N1} MB' -f ($len / 1MB), ($Size / 1MB)) -PercentComplete $pct
            }
            Start-Sleep -Milliseconds 250
        }
        $res = @(Receive-Job -Job $job -ErrorAction SilentlyContinue) | Select-Object -Last 1
        if (-not $res) { return [pscustomobject]@{ Code = 1; Err = 'gh produced no result' } }
        return [pscustomobject]@{ Code = $res.Code; Err = $res.Text }
    }
    finally {
        Write-Progress -Activity "Downloading $AssetName" -Completed
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-WithRetry {
    param([scriptblock]$Action, [int]$Attempts = 5, [int]$DelayMs = 500)
    for ($i = 1; ; $i++) {
        try { & $Action; return }
        catch {
            if ($i -ge $Attempts) { throw }
            Start-Sleep -Milliseconds $DelayMs
        }
    }
}

# Write test on the folder itself when it exists, else on its closest existing parent (nothing is created).
function Test-WritableLocation {
    param([string]$Path)
    $probe = $Path
    while ($probe -and -not (Test-Path -LiteralPath $probe -PathType Container)) { $probe = Split-Path -Parent $probe }
    if (-not $probe) { return $false }
    $file = Join-Path $probe ('.write-test-' + [guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::WriteAllText($file, 'x')
        [IO.File]::Delete($file)   # not Remove-Item: it would honour -WhatIf and leave the probe behind
        return $true
    }
    catch { return $false }
}

# The installed version, read from the exe (the in-app updater replaces the exe but not the registry entry). $null when unknown.
function Get-InstalledVersion {
    param([string]$InstallDir)
    $exe = Join-Path $InstallDir $script:ExeName
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { return $null }
    $v = ConvertTo-SemVer ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion)
    # An unstamped build reports the SDK default 1.0.0: unknown, like in the app.
    if ($null -eq $v -or $v.Text -eq '1.0.0') { return $null }
    return $v
}

# Processes started from the install folder.
function Get-RunningApp {
    param([string]$InstallDir)
    $root = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\') + '\'
    @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($script:ExeName)) -ErrorAction SilentlyContinue | Where-Object {
            try { $_.Path -and $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
        })
}

function Test-Interactive {
    return ([Environment]::UserInteractive -and -not [Console]::IsInputRedirected -and -not $script:Quiet)
}

function Confirm-Action {
    param([string]$Question)
    $a = Read-Host ($Question + ' [y/N]')
    return ($a -match '^(y|yes|o|oui)$')
}

# Closes the running app: asks first unless -Force, closes the window politely, kills it as a last resort (and says so).
function Stop-RunningApp {
    param([string]$InstallDir, [bool]$Force, [int]$TimeoutSec)
    $procs = @(Get-RunningApp $InstallDir)
    if ($procs.Count -eq 0) { return }
    $ids = ($procs | ForEach-Object { $_.Id }) -join ', '
    if (-not $Force) {
        if (Test-Interactive) {
            Close-OpenLine
            if (-not (Confirm-Action "dbExplorer is running (PID $ids). Close it now?")) {
                Stop-Installer 'dbExplorer is running and was not closed.' 'Close it and run the installer again.'
            }
        }
        else {
            Stop-Installer "dbExplorer is running from $InstallDir (PID $ids)." 'Close it and retry, or pass -Force to close it automatically.'
        }
    }
    foreach ($p in $procs) {
        Write-Detail "Closing dbExplorer (PID $($p.Id))..."
        try { [void]$p.CloseMainWindow() } catch { }
        $closed = $false
        try { $closed = $p.WaitForExit($TimeoutSec * 1000) } catch { $closed = $true }
        if (-not $closed) {
            Write-Warn "dbExplorer (PID $($p.Id)) did not close within ${TimeoutSec}s: killing it (unsaved work in an open dialog is lost)."
            try { $p.Kill(); [void]$p.WaitForExit(5000) } catch { }
        }
    }
    if (@(Get-RunningApp $InstallDir).Count -gt 0) {
        Stop-Installer 'dbExplorer is still running.' 'Close it manually and retry.'
    }
}

function Remove-PathRobust {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) {
        Invoke-WithRetry { Remove-Item -LiteralPath $Path -Recurse -Force }
    }
}

function Get-DirectorySizeKb {
    param([string]$Path)
    $sum = (Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) { return 0 }
    return [int][Math]::Ceiling($sum / 1KB)
}

# Replaces the content of $InstallDir by the content of $StageDir (inside $InstallDir, same volume: renames only).
# The old content goes to <InstallDir>\.install-backup first; on any failure it is put back. Retries on transient locks.
function Install-Payload {
    param([string]$StageDir, [string]$InstallDir)
    $backup = Join-Path $InstallDir '.install-backup'
    $reserved = @((Split-Path -Leaf $StageDir), '.install-backup')
    Remove-PathRobust $backup
    [void](New-Item -ItemType Directory -Path $backup -Force)
    $old = @(Get-ChildItem -LiteralPath $InstallDir -Force | Where-Object { $reserved -notcontains $_.Name })
    try {
        foreach ($item in $old) {
            $src = $item.FullName
            Invoke-WithRetry { Move-Item -LiteralPath $src -Destination $backup -Force }
        }
        foreach ($item in @(Get-ChildItem -LiteralPath $StageDir -Force)) {
            $src = $item.FullName
            Invoke-WithRetry { Move-Item -LiteralPath $src -Destination $InstallDir -Force }
        }
    }
    catch {
        $failure = $_.Exception.Message
        try {
            foreach ($item in @(Get-ChildItem -LiteralPath $InstallDir -Force | Where-Object { $reserved -notcontains $_.Name })) {
                Remove-PathRobust $item.FullName
            }
            foreach ($item in @(Get-ChildItem -LiteralPath $backup -Force)) {
                $src = $item.FullName
                Invoke-WithRetry { Move-Item -LiteralPath $src -Destination $InstallDir -Force }
            }
            Remove-PathRobust $backup
        }
        catch {
            Stop-Installer "Could not replace the files ($failure) and the rollback failed: $($_.Exception.Message)" "The previous files are in $backup, move them back by hand."
        }
        Stop-Installer "Could not replace the files: $failure. The previous version was restored." 'Close anything using the install folder (editor, antivirus scan) and retry.'
    }
    try { Remove-PathRobust $backup }
    catch { Write-Warn "Could not delete the backup folder $backup : $($_.Exception.Message)" }
}

function New-AppShortcut {
    param([string]$Path, [string]$Target, [string]$WorkingDirectory)
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { [void](New-Item -ItemType Directory -Path $dir -Force) }
    $shell = New-Object -ComObject WScript.Shell
    try {
        $lnk = $shell.CreateShortcut($Path)
        $lnk.TargetPath = $Target
        $lnk.WorkingDirectory = $WorkingDirectory
        $lnk.IconLocation = "$Target,0"
        $lnk.Description = $script:AppName
        $lnk.Save()
    }
    finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
}

# Command lines for the registry: powershell.exe running the setup copy. Seams the caller overrode are forwarded.
function Get-UninstallCommand {
    param([string]$SetupScript, [string]$InstallDir, [hashtable]$Seams)
    $base = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "{0}" -Uninstall -InstallDir "{1}"' -f $SetupScript, $InstallDir
    foreach ($k in 'RegistryKey', 'StartMenuDir', 'DesktopDir', 'SetupDir', 'UserDataDir') {
        if ($Seams.ContainsKey($k)) { $base += ' -{0} "{1}"' -f $k, $Seams[$k] }
    }
    @{ Uninstall = $base + ' -PauseAtEnd'; Quiet = $base + ' -Quiet -Force' }
}

function Register-App {
    param([string]$Key, [string]$InstallDir, [string]$Version, [string]$Repository, [hashtable]$Commands)
    $exe = Join-Path $InstallDir $script:ExeName
    [void](New-Item -Path $Key -Force)
    $strings = [ordered]@{
        DisplayName          = $script:AppName
        DisplayVersion       = $Version
        Publisher            = $Repository.Split('/')[0]
        InstallLocation      = $InstallDir
        DisplayIcon          = "$exe,0"
        UninstallString      = $Commands.Uninstall
        QuietUninstallString = $Commands.Quiet
        URLInfoAbout         = "https://github.com/$Repository"
    }
    foreach ($name in $strings.Keys) { Set-ItemProperty -Path $Key -Name $name -Value $strings[$name] -Type String }
    Set-ItemProperty -Path $Key -Name EstimatedSize -Value (Get-DirectorySizeKb $InstallDir) -Type DWord
    Set-ItemProperty -Path $Key -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $Key -Name NoRepair -Value 1 -Type DWord
}

# The text of this script, for the setup copy when it is not a file (piped): the script block text when it is the whole
# script, otherwise the file from the repository's main branch (HTTPS, then gh).
function Get-SelfText {
    param([string]$Repository)
    $marker = 'function Invoke-DbExplorerInstaller'
    if ($script:SelfText -and $script:SelfText.Contains($marker)) { return $script:SelfText }
    # Run through `irm | iex`: there is no file and no script block text, so fetch the same file again.
    $t = Get-HttpText "https://raw.githubusercontent.com/$Repository/main/install.ps1" 'text/plain'
    if ($t -and $t.Contains($marker)) { return $t }
    if (-not (Test-GhAvailable)) { throw "could not download install.ps1 from $Repository (https://raw.githubusercontent.com/$Repository/main/install.ps1)" }
    $r = Invoke-Gh @('api', "repos/$Repository/contents/install.ps1", '-H', 'Accept: application/vnd.github.raw')
    if ($r.Code -ne 0 -or -not $r.Out.Contains($marker)) {
        throw "could not fetch install.ps1 from $Repository over HTTPS or through gh ($($r.Err.Trim()))"
    }
    return $r.Out
}

# Copies this script to <SetupDir>\install.ps1 (outside the install folder, which the in-app updater empties).
function Save-SetupCopy {
    param([string]$SetupDir, [string]$Repository)
    $dest = Join-Path $SetupDir 'install.ps1'
    if (-not (Test-Path -LiteralPath $SetupDir)) { [void](New-Item -ItemType Directory -Path $SetupDir -Force) }
    $same = $script:SelfPath -and ([IO.Path]::GetFullPath($script:SelfPath) -eq [IO.Path]::GetFullPath($dest))
    if ($same) { return $dest }
    if ($script:SelfPath -and (Test-Path -LiteralPath $script:SelfPath)) {
        Copy-Item -LiteralPath $script:SelfPath -Destination $dest -Force
    }
    else {
        # UTF-8 with a BOM: the script is plain ASCII, the BOM only makes sure Windows PowerShell 5.1 never guesses an ANSI page.
        [IO.File]::WriteAllText($dest, (Get-SelfText $Repository), (New-Object Text.UTF8Encoding($true)))
    }
    return $dest
}

# Removes the setup copy once this process is gone: a detached cmd waits a moment, deletes the script, then the folder if empty.
function Start-SetupCleanup {
    param([string]$SetupDir)
    $scriptFile = Join-Path $SetupDir 'install.ps1'
    $line = 'ping -n 4 127.0.0.1 >nul & del /f /q "{0}" & rd "{1}"' -f $scriptFile, $SetupDir
    Start-Process -FilePath "$env:SystemRoot\System32\cmd.exe" -ArgumentList @('/d', '/c', $line) -WindowStyle Hidden
}

# Resolves every location, applying the defaults and the test seams.
function Resolve-Locations {
    param([hashtable]$Opt)
    $local = [Environment]::GetEnvironmentVariable('LOCALAPPDATA')
    $roaming = [Environment]::GetEnvironmentVariable('APPDATA')
    if (-not $local) { $local = [Environment]::GetFolderPath('LocalApplicationData') }
    if (-not $roaming) { $roaming = [Environment]::GetFolderPath('ApplicationData') }
    $dataDefault = Get-FirstNonEmpty ([Environment]::GetEnvironmentVariable('DBEXPLORER_DATA_DIR')) (Join-Path $roaming 'dbExplorer')
    $loc = @{
        InstallDir   = Get-FirstNonEmpty $Opt.InstallDir (Join-Path $local 'Programs\dbExplorer')
        SetupDir     = Get-FirstNonEmpty $Opt.SetupDir (Join-Path $local 'Programs\dbExplorer-setup')
        StartMenuDir = Get-FirstNonEmpty $Opt.StartMenuDir (Join-Path $roaming 'Microsoft\Windows\Start Menu\Programs')
        DesktopDir   = Get-FirstNonEmpty $Opt.DesktopDir ([Environment]::GetFolderPath('DesktopDirectory'))
        RegistryKey  = Get-FirstNonEmpty $Opt.RegistryKey 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\dbExplorer'
        UserDataDir  = Get-FirstNonEmpty $Opt.UserDataDir $dataDefault
    }
    foreach ($k in 'InstallDir', 'SetupDir', 'StartMenuDir', 'DesktopDir', 'UserDataDir') {
        $loc[$k] = [IO.Path]::GetFullPath($loc[$k]).TrimEnd('\')
    }
    $loc.Exe = Join-Path $loc.InstallDir $script:ExeName
    return $loc
}

function Test-WindowsHost {
    return ($env:OS -eq 'Windows_NT')
}

# ---------------------------------------------------------------------------------------------------------------------
# Install
# ---------------------------------------------------------------------------------------------------------------------

function Invoke-Install {
    param([hashtable]$Opt, [hashtable]$Seams, $Cmdlet)

    $script:StepNo = 0
    $script:StepTotal = 6
    $tag = ConvertTo-ReleaseTag $Opt.Version
    $repo = $Opt.Repository
    $target = if ($tag) { $tag } else { 'latest release' }
    Write-Banner 'dbExplorer installer' "$repo  -  $target"

    # 1. prerequisites
    Start-Step 'Checking prerequisites'
    if ($null -eq $tag) {
        Stop-Installer "Invalid -Version '$($Opt.Version)'." 'Use a version like 1.0.4 or v1.0.4, or omit it for the latest release.'
    }
    if (-not (Test-RepositoryName $repo)) {
        Stop-Installer "Invalid -Repository '$repo'." 'Use the owner/name form, for example JonathanOll/dbExplorer.'
    }
    if ($Opt.UseGh) { [void](Get-GhPath) }
    $loc = Resolve-Locations $Opt
    if (-not (Test-WritableLocation $loc.InstallDir)) {
        Stop-Installer "The install folder is not writable: $($loc.InstallDir)" 'Choose a folder you can write to with -InstallDir (the in-app updater needs one too).'
    }
    if ((Test-Path -LiteralPath $loc.InstallDir) -and @(Get-ChildItem -LiteralPath $loc.InstallDir -Force).Count -gt 0 -and -not (Test-Path -LiteralPath $loc.Exe)) {
        Stop-Installer "$($loc.InstallDir) is not empty and does not look like a dbExplorer install (no $script:ExeName)." 'Choose an empty or new folder with -InstallDir: an install replaces everything in it.'
    }
    Complete-Step

    # 2. release
    Start-Step "Finding the release ($target)"
    $useGh = [bool]$Opt.UseGh
    $release = $null
    $httpError = $null
    if (-not $useGh) {
        try { $release = Get-ReleaseHttp $repo $tag }
        catch {
            if (-not (Test-GhAvailable)) { throw }
            Write-Detail "HTTPS to GitHub failed ($($_.Exception.Message)): trying gh."
            $useGh = $true
            $httpError = $_
        }
    }
    if ($useGh) {
        try { $release = Get-ReleaseGh $repo $tag }
        catch { if ($httpError) { throw $httpError } else { throw } }
    }
    $relTag = [string](Get-OptionalProperty $release 'tagName')
    $relVer = ConvertTo-SemVer $relTag
    $asset = Select-ReleaseAsset $release
    if ($null -eq $relVer) { Stop-Installer "The release has no usable version tag ('$relTag')." 'Pick another release with -Version.' }
    if ($null -eq $asset) { Stop-Installer "Release $relTag has no .zip asset." "Check https://github.com/$repo/releases/tag/$relTag or choose another -Version." }
    Complete-Step $relTag

    $installed = Get-InstalledVersion $loc.InstallDir
    if ($installed -and -not $Opt.Force) {
        $cmp = Compare-SemVer $installed $relVer
        if ($cmp -ge 0) {
            $what = if ($cmp -eq 0) { "dbExplorer $($installed.Text) is already installed" } else { "dbExplorer $($installed.Text) is installed, newer than $relTag" }
            Write-Ui ('  ' + $what + ' in ' + $loc.InstallDir + '. Use -Force to reinstall.') 'Green'
            Write-Ui ''
            return 0
        }
    }
    $verb = if ($installed) { "Update $($installed.Text) -> $($relVer.Text)" } else { "Install $($relVer.Text)" }
    if (-not (Test-ShouldProcess $Cmdlet $loc.InstallDir "$verb of dbExplorer from $repo")) { return 0 }

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('dbExplorer-install-' + [guid]::NewGuid().ToString('N'))
    $dirExisted = Test-Path -LiteralPath $loc.InstallDir
    $stage = Join-Path $loc.InstallDir '.install-staging'
    $done = $false
    try {
        # 3. download + verify
        Start-Step ('Downloading {0} ({1:N1} MB)' -f $asset.Name, ($asset.Size / 1MB))
        [void](New-Item -ItemType Directory -Path $tmp -Force)
        $zip = Join-Path $tmp $asset.Name
        $fetched = $false
        if (-not $useGh) {
            try { Invoke-HttpDownload $asset $relTag $repo $tmp; $fetched = $true }
            catch {
                if (-not (Test-GhAvailable)) { throw }
                Write-Detail "HTTPS download failed ($($_.Exception.Message)): trying gh."
            }
        }
        if (-not $fetched) {
            $d = Invoke-GhDownload $relTag $repo $asset.Name $tmp $asset.Size
            if ($d.Code -ne 0 -or -not (Test-Path -LiteralPath $zip)) {
                $f = Get-GhFailure $d.Err $relTag $repo
                Stop-Installer "Download failed: $($f.Message)" $f.Fix
            }
        }
        $expected = $asset.Sha256
        $from = 'sha256 verified'
        if (-not $expected) {
            $expected = Get-Sha256Sidecar $asset
            $from = 'sha256 verified from the .sha256 asset'
        }
        if (-not $expected) {
            Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
            Stop-Installer "GitHub gives no SHA-256 digest for $($asset.Name) and the release has no $($asset.Name).sha256 asset: the download cannot be verified, so it is refused." 'Install a release published with a digest (-Version), or ask the maintainer to re-publish it.'
        }
        $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
            Stop-Installer "SHA-256 mismatch for $($asset.Name) (expected $expected, got $actual). The download was deleted." 'Retry; if it persists, the release asset was altered or corrupted: do not install it.'
        }
        Complete-Step "OK ($from)"

        # 4. extract + sanity check
        Start-Step 'Extracting'
        if (-not $dirExisted) { [void](New-Item -ItemType Directory -Path $loc.InstallDir -Force) }
        Remove-PathRobust $stage
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        try { [IO.Compression.ZipFile]::ExtractToDirectory($zip, $stage) }
        catch { Stop-Installer "Could not extract $($asset.Name): $($_.Exception.Message)" 'The download may be corrupted: retry.' }
        if (-not (Test-Path -LiteralPath (Join-Path $stage $script:ExeName) -PathType Leaf)) {
            Stop-Installer "$($asset.Name) does not contain $script:ExeName at its root." 'This is not a dbExplorer release package: nothing was installed.'
        }
        Complete-Step

        # 5. swap
        Start-Step 'Installing files'
        Stop-RunningApp $loc.InstallDir $Opt.Force $Opt.CloseTimeoutSec
        Install-Payload $stage $loc.InstallDir
        $done = $true
        Complete-Step
    }
    finally {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
        if (-not $done -and -not $dirExisted -and (Test-Path -LiteralPath $loc.InstallDir)) {
            Remove-Item -LiteralPath $loc.InstallDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # 6. setup copy, registration, shortcuts (the files are in place: problems here are warnings)
    Start-Step 'Registering and creating shortcuts'
    $setupScript = $null
    try { $setupScript = Save-SetupCopy $loc.SetupDir $repo }
    catch { Write-Warn "Could not store the uninstaller copy in $($loc.SetupDir): $($_.Exception.Message) Apps & features entry skipped." }
    if ($setupScript) {
        try {
            $cmds = Get-UninstallCommand $setupScript $loc.InstallDir $Seams
            Register-App $loc.RegistryKey $loc.InstallDir $relVer.Text $repo $cmds
        }
        catch { Write-Warn "Could not register in Apps & features: $($_.Exception.Message)" }
    }
    $made = @{}
    if (-not $Opt.NoStartMenu) {
        $lnk = Join-Path $loc.StartMenuDir 'dbExplorer.lnk'
        try { New-AppShortcut $lnk $loc.Exe $loc.InstallDir; $made.Start = $lnk }
        catch { Write-Warn "Could not create the Start Menu shortcut: $($_.Exception.Message)" }
    }
    if ($Opt.DesktopShortcut) {
        $lnk = Join-Path $loc.DesktopDir 'dbExplorer.lnk'
        try { New-AppShortcut $lnk $loc.Exe $loc.InstallDir; $made.Desktop = $lnk }
        catch { Write-Warn "Could not create the desktop shortcut: $($_.Exception.Message)" }
    }
    Complete-Step

    $none = '(none)'
    $lines = @(
        ('Version      ' + $relVer.Text),
        ('Install in   ' + $loc.InstallDir),
        ('Start Menu   ' + $(if ($made.ContainsKey('Start')) { $made.Start } else { $none })),
        ('Desktop      ' + $(if ($made.ContainsKey('Desktop')) { $made.Desktop } else { $none }))
    )
    $lines += 'Start        Start Menu > dbExplorer'
    $lines += 'Update       run the install command again (irm .../install.ps1 | iex), or use the in-app update'
    if ($setupScript) {
        $lines += 'Uninstall    Settings > Apps > dbExplorer, or:'
        $lines += ('             powershell -NoProfile -ExecutionPolicy Bypass -File "' + $setupScript + '" -Uninstall')
    }
    $lines += ('User data    ' + $loc.UserDataDir + '  (never touched by install/update)')
    Write-Summary ("dbExplorer $($relVer.Text) " + $(if ($installed) { 'updated' } else { 'installed' })) $lines

    if ($Opt.Launch -and (Test-ShouldProcess $Cmdlet $loc.Exe 'Start')) {
        Start-Process -FilePath $loc.Exe -WorkingDirectory $loc.InstallDir
        Write-Ui '  dbExplorer started.' 'DarkGray'
        Write-Ui ''
    }
    return 0
}

# ---------------------------------------------------------------------------------------------------------------------
# Uninstall
# ---------------------------------------------------------------------------------------------------------------------

function Invoke-Uninstall {
    param([hashtable]$Opt, $Cmdlet)

    $script:StepNo = 0
    $script:StepTotal = 5
    Write-Banner 'dbExplorer uninstaller' $Opt.Repository

    Start-Step 'Looking for the installation'
    $loc = Resolve-Locations $Opt
    $hasExe = Test-Path -LiteralPath $loc.Exe -PathType Leaf
    $hasDir = Test-Path -LiteralPath $loc.InstallDir
    $hasKey = Test-Path -LiteralPath $loc.RegistryKey
    $setupScript = Join-Path $loc.SetupDir 'install.ps1'
    $hasSetup = Test-Path -LiteralPath $setupScript
    $startLnk = Join-Path $loc.StartMenuDir 'dbExplorer.lnk'
    $deskLnk = Join-Path $loc.DesktopDir 'dbExplorer.lnk'
    $hasLnk = (Test-Path -LiteralPath $startLnk) -or (Test-Path -LiteralPath $deskLnk)
    if ($hasDir -and -not $hasExe -and @(Get-ChildItem -LiteralPath $loc.InstallDir -Force).Count -gt 0) {
        Stop-Installer "$($loc.InstallDir) does not look like a dbExplorer install (no $script:ExeName): it is left alone." 'Check -InstallDir; nothing was removed.'
    }
    if (-not ($hasDir -or $hasKey -or $hasSetup -or $hasLnk) -and -not $Opt.RemoveUserData) {
        Complete-Step 'nothing found'
        Write-Ui '  dbExplorer is not installed: nothing to uninstall.' 'Green'
        Write-Ui ''
        return 0
    }
    Complete-Step
    if (-not (Test-ShouldProcess $Cmdlet $loc.InstallDir 'Uninstall dbExplorer')) { return 0 }

    Start-Step 'Closing the running app'
    Set-Location -LiteralPath ([IO.Path]::GetTempPath())
    Stop-RunningApp $loc.InstallDir $Opt.Force $Opt.CloseTimeoutSec
    Complete-Step

    Start-Step 'Removing the files'
    Remove-PathRobust $loc.InstallDir
    Complete-Step

    Start-Step 'Removing shortcuts and registration'
    foreach ($lnk in $startLnk, $deskLnk) {
        if (Test-Path -LiteralPath $lnk) { Remove-Item -LiteralPath $lnk -Force }
    }
    if ($hasKey) { Remove-Item -LiteralPath $loc.RegistryKey -Recurse -Force }
    Complete-Step

    Start-Step 'Removing the setup copy and user data'
    if ($hasSetup) { Start-SetupCleanup $loc.SetupDir }
    $dataNote = 'kept (' + $loc.UserDataDir + ')'
    if ($Opt.RemoveUserData) {
        if (-not (Test-Path -LiteralPath $loc.UserDataDir)) { $dataNote = 'nothing to remove (' + $loc.UserDataDir + ')' }
        else {
            $ok = [bool]$Opt.Force
            if (-not $ok) {
                if (Test-Interactive) { Close-OpenLine; $ok = Confirm-Action "Delete ALL user data in $($loc.UserDataDir) (connections, sessions, scripts)?" }
                else { Write-Warn "User data kept: confirmation needed, pass -Force to delete $($loc.UserDataDir) without asking." }
            }
            if ($ok -and (Test-ShouldProcess $Cmdlet $loc.UserDataDir 'Delete user data')) {
                Remove-PathRobust $loc.UserDataDir
                $dataNote = 'deleted (' + $loc.UserDataDir + ')'
            }
        }
    }
    Complete-Step

    Write-Summary 'dbExplorer uninstalled' @(
        ('Removed      ' + $loc.InstallDir),
        'Shortcuts    removed',
        ('User data    ' + $dataNote),
        ('Reinstall    https://github.com/' + $Opt.Repository)
    )
    return 0
}

# ---------------------------------------------------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------------------------------------------------

# Runs the requested operation and returns the exit code; every expected failure is reported as "ERROR: cause" + "Fix: ...".
function Invoke-DbExplorerInstaller {
    param([hashtable]$Opt, [hashtable]$Seams, $Cmdlet)
    $script:Quiet = [bool]$Opt.Quiet
    $prevPrompt = $env:GH_PROMPT_DISABLED
    $prevColor = $env:NO_COLOR
    $env:GH_PROMPT_DISABLED = '1'   # gh must never wait for input
    $env:NO_COLOR = '1'
    try {
        if (-not (Test-WindowsHost)) { Stop-Installer 'This installer only runs on Windows.' 'Use Windows 10 or 11.' }
        if ($Opt.RemoveUserData -and -not $Opt.Uninstall) {
            Stop-Installer '-RemoveUserData only works together with -Uninstall.' 'Add -Uninstall, or drop -RemoveUserData.'
        }
        if ($Opt.Uninstall) { return (Invoke-Uninstall $Opt $Cmdlet) }
        return (Invoke-Install $Opt $Seams $Cmdlet)
    }
    catch {
        $ex = $_.Exception
        if ($script:LineOpen) { Write-Host 'FAILED' -ForegroundColor Red; $script:LineOpen = $false }
        Write-Host ('ERROR: ' + $ex.Message) -ForegroundColor Red
        if ($ex.Data.Contains('DbExplorerFix')) {
            if ($ex.Data['DbExplorerFix']) { Write-Host ('  Fix: ' + $ex.Data['DbExplorerFix']) -ForegroundColor Yellow }
        }
        else { Write-Host ('  At: ' + $_.InvocationInfo.PositionMessage.Split("`n")[0].Trim()) -ForegroundColor DarkGray }
        return 1
    }
    finally {
        $env:GH_PROMPT_DISABLED = $prevPrompt
        $env:NO_COLOR = $prevColor
    }
}

# Dot-sourcing (`. .\install.ps1`) only loads the functions, which is how the tests use them.
if ($MyInvocation.InvocationName -ne '.') {
    $opt = @{
        InstallDir = $InstallDir; Version = $Version; Repository = $Repository
        DesktopShortcut = [bool]$DesktopShortcut; NoStartMenu = [bool]$NoStartMenu; Launch = [bool]$Launch
        Force = [bool]$Force; Uninstall = [bool]$Uninstall; RemoveUserData = [bool]$RemoveUserData; UseGh = [bool]$UseGh; Quiet = [bool]$Quiet
        StartMenuDir = $StartMenuDir; DesktopDir = $DesktopDir; RegistryKey = $RegistryKey; SetupDir = $SetupDir
        UserDataDir = $UserDataDir; CloseTimeoutSec = $CloseTimeoutSec
    }
    $seams = @{}
    $bound = Get-Variable PSBoundParameters -ValueOnly -ErrorAction SilentlyContinue
    if ($bound) {
        foreach ($k in 'RegistryKey', 'StartMenuDir', 'DesktopDir', 'SetupDir', 'UserDataDir') {
            if ($bound.ContainsKey($k)) { $seams[$k] = $bound[$k] }
        }
    }
    $cmdlet = Get-Variable PSCmdlet -ValueOnly -ErrorAction SilentlyContinue
    $exitCode = Invoke-DbExplorerInstaller -Opt $opt -Seams $seams -Cmdlet $cmdlet
    if ($PauseAtEnd -and (Test-Interactive)) { [void](Read-Host 'Press Enter to close') }
    if ($script:SelfPath) { exit $exitCode }
    # Not a file (script block / Invoke-Expression): `exit` would close the caller's shell, so report through the error stream.
    $global:LASTEXITCODE = $exitCode
    if ($exitCode -ne 0) { throw "dbExplorer installer failed (exit code $exitCode)." }
}
