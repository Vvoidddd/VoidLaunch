# VoidLaunch

VoidLaunch is an open-source Windows game library and launcher built with WPF and .NET 8. It scans local game folders, organizes installed games into a themed library, lets each game use a manually selected executable, and launches games through the Windows shell.

[![Publish VoidLaunch](https://github.com/Vvoidddd/VoidLaunch/actions/workflows/release.yml/badge.svg)](https://github.com/Vvoidddd/VoidLaunch/actions/workflows/release.yml)
[![Latest release](https://img.shields.io/github/v/release/Vvoidddd/VoidLaunch)](https://github.com/Vvoidddd/VoidLaunch/releases/latest)

## Features

- Scans configured folders for playable Windows executables.
- Filters installers, crash reporters, uninstallers, redistributables, modding tools, and other utility programs.
- Merges multiple executables from one installation into a single game card.
- Provides a game-details page for choosing the exact executable to launch.
- Starts games through Windows Shell, like double-clicking the executable.
- Tracks favorites and recently played games locally.
- Extracts executable artwork when no cover image is available.
- Includes detachable, side-docking game log windows.
- Includes built-in themes and a safe editable theme-color format.
- Includes Developer, About/Health, Update, and Privacy pages.
- Checks GitHub Releases for application updates automatically.
- Verifies update downloads using GitHub's published SHA-256 asset digest.

## Download

Download `VoidLaunch.exe` from the [latest GitHub Release](https://github.com/Vvoidddd/VoidLaunch/releases/latest), place it in any writable folder, and run it.

The release is one self-contained Windows x64 executable. The user does not need to install .NET separately.

## Automatic updates

VoidLaunch checks this repository's latest public GitHub Release at startup. An update is accepted only when:

1. The release version is newer than the running assembly version.
2. The release contains an asset named exactly `VoidLaunch.exe`.
3. The asset is downloaded over HTTPS from GitHub.
4. The downloaded file matches GitHub's published SHA-256 digest.

After verification, VoidLaunch closes, replaces its executable, and restarts. A temporary backup is restored if replacement fails.

## Versioning and releases

Every push to `main` runs [.github/workflows/release.yml](.github/workflows/release.yml). The workflow:

1. Chooses a version in `MAJOR.MINOR.GITHUB_RUN_NUMBER` format.
2. Builds a self-contained Windows x64 single-file executable.
3. Verifies that the output contains only `VoidLaunch.exe`.
4. Verifies the EXE's embedded file version.
5. Creates a matching Git tag and GitHub Release.
6. Uploads the single EXE to that release.

The base major/minor version comes from `<Version>` in [VoidLaunch.csproj](VoidLaunch/VoidLaunch.csproj). For example, with a base of `1.0.0`, workflow run 7 publishes `v1.0.7`. Change the project version to `1.1.0` when beginning the 1.1 release line.

You can also run the workflow manually and enter an exact version such as `1.2.0`.

## Local release build

The Visual Studio `FolderProfile` publishes to:

```text
C:\Users\tolik\OneDrive\Desktop\BUILDS\VoidLaunch.exe
```

Command-line equivalent:

```powershell
dotnet publish .\VoidLaunch\VoidLaunch.csproj -c Release -p:PublishProfile=FolderProfile
```

## Local data and privacy

VoidLaunch stores game folders, executable choices, favorites, play history, and theme preferences locally under the current Windows user's application-data folder.

VoidLaunch contains no analytics, advertisements, accounts, tracking SDKs, cryptocurrency miners, or hidden telemetry. It does not upload the game library or personal files. Its automatic network access is limited to checking and downloading public releases from this GitHub repository.

## Development

Requirements:

- Windows 10 or Windows 11
- Visual Studio 2022 with the .NET desktop development workload, or the .NET 8 SDK

Build:

```powershell
dotnet build VoidLaunch.sln -c Release
```

## Developer

Created and maintained by [Vvoidddd](https://github.com/Vvoidddd).

## License

The source is publicly available for inspection and development. Add a formal `LICENSE` file before accepting outside contributions or redistributing modified builds under explicit terms.
