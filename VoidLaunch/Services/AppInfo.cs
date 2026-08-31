using System;
using System.Reflection;

namespace VoidLaunch.Services
{
    public static class AppInfo
    {
        public const string GitHubOwner = "Vvoidddd";
        public const string GitHubRepository = "VoidLaunch";
        public const string ReleaseAssetName = "VoidLaunch.exe";
        public const string DeveloperUrl = "https://github.com/Vvoidddd";
        public const string RepositoryUrl = "https://github.com/Vvoidddd/VoidLaunch";
        public const string ReleasesUrl = RepositoryUrl + "/releases";
        public const string ReleasesApiUrl =
            "https://api.github.com/repos/Vvoidddd/VoidLaunch/releases";
        public const string LatestReleaseApiUrl = ReleasesApiUrl + "/latest";

        public static Version CurrentVersion =>
            Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0, 0);

        public static string DisplayVersion =>
            $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{Math.Max(CurrentVersion.Build, 0)}";
    }
}
