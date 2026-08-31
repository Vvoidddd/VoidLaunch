# VoidLaunch

VoidLaunch is an open-source Windows game library and launcher built with WPF and .NET 8.

## Release build

Publish the `FolderProfile` profile in Visual Studio, or run:

```powershell
dotnet publish .\VoidLaunch\VoidLaunch.csproj -c Release -p:PublishProfile=FolderProfile
```

The configured local profile writes one self-contained `VoidLaunch.exe` to:

```text
C:\Users\tolik\OneDrive\Desktop\BUILDS
```

## Updates

The app checks the latest public GitHub Release from `Vvoidddd/VoidLaunch`. Releases must attach an asset named exactly `VoidLaunch.exe`. Release tags use semantic versions such as `v1.0.1`.

## Privacy

VoidLaunch stores its library and preferences locally. It does not contain analytics, advertising, account tracking, or hidden telemetry. The only automatic network request is the public GitHub Releases update check.
