# VoidLaunch

VoidLaunch is a Windows game launcher I made because I wanted one clean place for games that are not all tied to the same store or launcher.

You give it one or more game folders and it scans them, groups the games together, and tries to pick the right executable. If it picks the wrong one, open the game's page and choose the EXE you actually want it to start.

[![Build and release](https://github.com/Vvoidddd/VoidLaunch/actions/workflows/release.yml/badge.svg)](https://github.com/Vvoidddd/VoidLaunch/actions/workflows/release.yml)
[![Latest version](https://img.shields.io/github/v/release/Vvoidddd/VoidLaunch)](https://github.com/Vvoidddd/VoidLaunch/releases/latest)

## Download

Get `VoidLaunch.exe` from the [latest release](https://github.com/Vvoidddd/VoidLaunch/releases/latest).

There is no installer and you do not need to install .NET. Put the EXE wherever you want and open it.

## What it can do

- Scan multiple folders for games.
- Keep different EXEs from the same game on one game page.
- Let you choose exactly which EXE the Play button opens.
- Launch an EXE through Windows the same way as double-clicking it.
- Filter out installers, uninstallers, crash reporters, mod tools, and other junk where possible.
- Keep favorites and recently played games.
- Use the icon from an EXE when there is no cover image.
- Show game output in a log window that can dock to the side of the launcher.
- Change the whole launcher with built-in themes or custom theme colors.
- Check GitHub for updates and install a newer release.
- Show a themed in-app update prompt with Update now and Later choices.
- Browse every GitHub release and download an older version without replacing the installed one.
- Keep a Coming Soon page for the future VoidLaunch download website.

It is still a work in progress, so the scanner will not guess every game perfectly. That is why the manual EXE picker is there.

## Updates

VoidLaunch checks the releases from this repository. Before replacing itself, it makes sure the download is named `VoidLaunch.exe` and that its SHA-256 hash matches the digest GitHub published for it.

## Building it

You need Windows and either Visual Studio 2022 with the .NET desktop workload or the .NET 8 SDK.

```powershell
dotnet build VoidLaunch.sln -c Release
```

To make the same single-file build used by the releases:

```powershell
dotnet publish .\VoidLaunch\VoidLaunch.csproj -c Release -p:PublishProfile=FolderProfile
```

## Making a release

Every push to `main` runs the release workflow. It builds one self-contained `VoidLaunch.exe`, checks the embedded version, creates a tag, and puts the EXE in a new GitHub Release.

The major and minor numbers come from `<Version>` in `VoidLaunch.csproj`. The last number is the GitHub Actions run number, so a project version of `1.0.0` on run 8 becomes `1.0.8`. The workflow can also be started manually with an exact version number.

## Privacy

Game folders, EXE choices, favorites, history, and theme settings stay in the local VoidLaunch app-data folder. There are no ads, accounts, analytics, miners, tracking SDKs, or hidden telemetry. The only automatic internet access is the update check against this GitHub repository.

## License

The source is public so people can inspect it, use it, and change their own copy. You cannot reupload, mirror, sell, repackage, or distribute VoidLaunch or a modified version of it. Read the full [VoidLaunch Source-Available License](LICENSE) before using the code.

Made by [Vvoidddd](https://github.com/Vvoidddd).
