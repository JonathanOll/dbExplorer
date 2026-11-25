# Install and update

dbExplorer installs for the current user, with one PowerShell line. No administrator rights, no .NET (the release is
self-contained), no GitHub CLI. `install.ps1` runs in Windows PowerShell 5.1 and in PowerShell 7 on Windows 10/11, talks to
GitHub over plain HTTPS and verifies the SHA-256 of the download.

## Install

```powershell
irm https://raw.githubusercontent.com/JonathanOll/dbExplorer/main/install.ps1 | iex
```

On a bare Windows PowerShell 5.1 that answers with a TLS error, enable TLS 1.2 first:

```powershell
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; irm https://raw.githubusercontent.com/JonathanOll/dbExplorer/main/install.ps1 | iex
```

`| iex` passes no parameters. To pass some, use the script block form (add them after the closing parentheses):

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/JonathanOll/dbExplorer/main/install.ps1))) -Version 1.0.4 -DesktopShortcut -Launch
```

Or download the script, then run it (`-ExecutionPolicy Bypass` because many machines block local scripts):

```powershell
irm https://raw.githubusercontent.com/JonathanOll/dbExplorer/main/install.ps1 -OutFile install.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -Version 1.0.4 -DesktopShortcut
```

Piping the script into `powershell -Command -` does not work (a script with a `param` block is not accepted on stdin).

## Installer switches

| Parameter | Effect |
| --- | --- |
| `-InstallDir <path>` | Where to install (default `%LOCALAPPDATA%\Programs\dbExplorer`) |
| `-Version <tag>` | `1.0.4` or `v1.0.4` (default: the latest release) |
| `-Repository <owner/name>` | Default `JonathanOll/dbExplorer` |
| `-DesktopShortcut` / `-NoStartMenu` | Add a desktop shortcut / skip the Start Menu shortcut (created by default) |
| `-Launch` | Start the app at the end |
| `-Force` | Reinstall the same (or a newer) version; close a running dbExplorer without asking (window first, killed after 20 s) |
| `-Uninstall` | Remove the app (see below) |
| `-RemoveUserData` | With `-Uninstall`: also delete `%APPDATA%\dbExplorer` (or `DBEXPLORER_DATA_DIR`), after a confirmation unless `-Force` |
| `-UseGh` | Query and download the release with the GitHub CLI instead of HTTPS (optional, needs `gh`) |
| `-Quiet` | Only warnings and errors |
| `-WhatIf` / `-Confirm` | Standard PowerShell switches (not available through `\| iex`: use the script block form) |

Errors print a cause and a fix, and exit with a non-zero code.

## Where things go

- the app in `%LOCALAPPDATA%\Programs\dbExplorer` (a writable folder, as the in-app updater needs); the installer replaces
  everything in it and refuses a non-empty folder that is not a dbExplorer install;
- a copy of the script in `%LOCALAPPDATA%\Programs\dbExplorer-setup\install.ps1`: it is the uninstaller, kept outside the
  install folder because the in-app updater empties that folder;
- the Start Menu shortcut `dbExplorer.lnk` (and the desktop one on request);
- an "Apps & features" entry (`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\dbExplorer`, per user).

`%APPDATA%\dbExplorer` (config, sessions, scripts) is never touched by an install or an update.

## Update

Run the same command again. The same version is left alone (`already installed`), an older one is upgraded. A running
dbExplorer makes the installer stop (close it, or pass `-Force`). The app also [updates itself](install.md#automatic-updates) in the
background; after an in-app update, running `install.ps1` sees the new version (it reads it from the exe) and does nothing.
Only the version shown in "Apps & features" keeps the one that was installed by the script.

## Uninstall

Settings > Apps > dbExplorer, or

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\dbExplorer-setup\install.ps1" -Uninstall
```

It removes the files, the shortcuts, the entry and the setup copy (deleted a few seconds after it exits). `%APPDATA%\dbExplorer`
is kept unless `-RemoveUserData` is given.

## Rate limit, private repository, offline

- GitHub allows 60 anonymous API requests per hour. If the installer reports the rate limit, set a token and run it again:
  `$env:GITHUB_TOKEN = '<personal access token>'` (`GH_TOKEN` works too). The token also allows a private repository, and is
  only sent to `api.github.com`.
- The GitHub CLI is an optional fallback: it is used only when HTTPS failed and `gh` is installed, or with `-UseGh`
  (`winget install --id GitHub.cli`; `gh auth login` if the repository is private).
- Manual or offline install: download `DbExplorer.zip` (a plain self-contained `win-x64` publish) from the GitHub release on
  a machine with access, copy it over, and extract it to a writable folder; run `DbExplorer.exe`. The installer itself
  needs network access to GitHub. It checks the SHA-256 against the digest GitHub gives for the asset (or the
  `DbExplorer.zip.sha256` asset), and refuses the install when it has neither.

## Automatic updates

A published copy checks GitHub for a newer release a few seconds after the window opened, on a background thread (the
startup path is untouched, see [Startup performance](development.md#startup-performance)). When one exists, a band appears
above the window: **Update and restart** or **Later** (hides it until the next start).

How it works:

1. The release workflow ([`.github/workflows/release.yml`](../.github/workflows/release.yml)) stamps the version (from the
   tag, via GitVersion) and the `owner/name` of the repository into the exe, and attaches `DbExplorer.zip` (a plain
   self-contained `win-x64` publish) to the GitHub release.
2. The check asks the GitHub REST API over HTTPS for the latest release of that repository (when HTTPS fails and the GitHub
   CLI is installed, it asks `gh` instead); a version higher than the running one offers the update. A failed check
   (offline, rate limit...) is silent: no dialog, at most a trace line. `GITHUB_TOKEN` / `GH_TOKEN` lifts the anonymous rate
   limit here too.
3. **Update and restart** downloads the zip into a fresh temp folder (the band shows the progress) and verifies the
   SHA-256 against the digest GitHub gives for the asset; a mismatch deletes the file and installs nothing.
4. Unsaved work is handled as when you close the window: a table tab with staged edits asks Save / Discard / Cancel (Cancel
   keeps the app open and the download for the next click); unsaved scripts are kept by their autosave and come back, with the
   tabs, at the next start.
5. A hidden Windows PowerShell relay waits for the app to exit, moves the current files to `.update-backup` inside the install
   folder, copies the new ones and starts the new exe with the same arguments. On any failure it puts the backup back and
   starts the old version again; the backup, the zip and the relay are deleted after a success.

Requirement: the app runs from its install folder, which must be writable ([`install.ps1`](#install) creates one). Nothing is
offered for development runs (`dotnet run`, `bin\Debug`, `bin\Release`), for a build without a stamped version (v1.0.0 is
the SDK default and counts as "unknown", so builds from before this feature and local builds never self-update), or when
`DbExplorer.exe` is not what is running. `%APPDATA%\dbExplorer` (config, sessions, scripts, cache) is outside the install
folder and is never touched.

To turn it off: untick **Check for updates automatically** in Settings > Updates (or the gear button / Help menu) (saved in
`config.json` as `AutoUpdateCheck`; a config without it means on), or start with `--no-update-check` /
`DBEXPLORER_NO_UPDATE_CHECK=1` for one run. The check is also skipped when the config could not be read at startup.

Limits: the release zip is a plain self-contained `win-x64` publish made by CI (not by `publish.ps1`, and unsigned); only the
latest release GitHub reports is considered; the only integrity check is the SHA-256 GitHub gives for the asset; there is no
"check now" action. A build stamped `1.0.0` (the SDK default) is treated as "unknown": it never self-updates, and a `v1.0.0`
release can only be updated to, not from.
