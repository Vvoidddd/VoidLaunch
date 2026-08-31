using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace VoidLaunch.Services
{
    public sealed class UpdateService
    {
        private static readonly HttpClient Client = CreateClient();

        public async Task<UpdateCheckResult> CheckAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                using HttpResponseMessage response = await Client.GetAsync(
                    AppInfo.LatestReleaseApiUrl,
                    cancellationToken);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return UpdateCheckResult.Failed(
                        "No GitHub release exists yet. Publish the first release to enable updates.");
                }

                response.EnsureSuccessStatusCode();

                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                GitHubRelease? release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
                    stream,
                    cancellationToken: cancellationToken);

                if (release is null || !TryParseReleaseVersion(release.TagName, out Version? latestVersion))
                    return UpdateCheckResult.Failed("The latest GitHub release has an invalid version tag.");

                GitHubAsset? asset = release.Assets.FirstOrDefault(
                    item => string.Equals(
                        item.Name,
                        AppInfo.ReleaseAssetName,
                        StringComparison.OrdinalIgnoreCase));

                bool updateAvailable = latestVersion > NormalizeVersion(AppInfo.CurrentVersion);

                if (asset is null)
                {
                    return new UpdateCheckResult(
                        false,
                        updateAvailable,
                        latestVersion,
                        release.TagName,
                        release.HtmlUrl,
                        null,
                        "The release exists, but it does not contain VoidLaunch.exe.");
                }

                var updateAsset = new UpdateAsset(
                    asset.BrowserDownloadUrl,
                    asset.Digest,
                    asset.Size);

                return new UpdateCheckResult(
                    true,
                    updateAvailable,
                    latestVersion,
                    release.TagName,
                    release.HtmlUrl,
                    updateAsset,
                    updateAvailable
                        ? $"VoidLaunch {latestVersion!.ToString(3)} is available."
                        : "VoidLaunch is up to date.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return UpdateCheckResult.Failed($"Update check failed: {ex.Message}");
            }
        }

        public async Task<ReleaseHistoryResult> GetReleaseHistoryAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                const int pageSize = 100;
                var releases = new List<GitHubRelease>();

                for (int page = 1; ; page++)
                {
                    string url = $"{AppInfo.ReleasesApiUrl}?per_page={pageSize}&page={page}";
                    using HttpResponseMessage response = await Client.GetAsync(url, cancellationToken);

                    if (response.StatusCode == HttpStatusCode.NotFound)
                        return ReleaseHistoryResult.Failed("The GitHub releases page could not be found.");

                    response.EnsureSuccessStatusCode();

                    await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    GitHubRelease[] pageReleases =
                        await JsonSerializer.DeserializeAsync<GitHubRelease[]>(
                            stream,
                            cancellationToken: cancellationToken)
                        ?? Array.Empty<GitHubRelease>();

                    releases.AddRange(pageReleases.Where(release => !release.Draft));

                    if (pageReleases.Length < pageSize)
                        break;
                }

                List<ReleaseHistoryItem> history = releases
                    .Select(release =>
                    {
                        _ = TryParseReleaseVersion(release.TagName, out Version? version);
                        GitHubAsset? asset = release.Assets.FirstOrDefault(
                            item => string.Equals(
                                item.Name,
                                AppInfo.ReleaseAssetName,
                                StringComparison.OrdinalIgnoreCase));

                        return new ReleaseHistoryItem(
                            version,
                            release.TagName,
                            string.IsNullOrWhiteSpace(release.Name)
                                ? release.TagName
                                : release.Name,
                            release.HtmlUrl,
                            release.Body,
                            release.PublishedAt,
                            release.Prerelease,
                            asset is null
                                ? null
                                : new UpdateAsset(
                                    asset.BrowserDownloadUrl,
                                    asset.Digest,
                                    asset.Size),
                            asset?.DownloadCount ?? 0);
                    })
                    .OrderByDescending(release => release.Version ?? new Version(0, 0, 0, 0))
                    .ThenByDescending(release => release.PublishedAt)
                    .ToList();

                return ReleaseHistoryResult.Success(history);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ReleaseHistoryResult.Failed(
                    $"Could not load the version history: {ex.Message}");
            }
        }

        public async Task<string> DownloadAsync(
            UpdateAsset asset,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out Uri? uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("GitHub returned an untrusted update URL.");
            }

            if (string.IsNullOrWhiteSpace(asset.Digest) ||
                !asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The GitHub release does not provide a SHA-256 digest, so the update was not installed.");
            }

            string updateDirectory = Path.Combine(
                Path.GetTempPath(),
                "VoidLaunch",
                "updates",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(updateDirectory);
            string downloadPath = Path.Combine(updateDirectory, AppInfo.ReleaseAssetName);

            try
            {
                using HttpResponseMessage response = await Client.GetAsync(
                    uri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? asset.Size;
                long downloadedBytes = 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(
                    downloadPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 128,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                byte[] buffer = new byte[1024 * 128];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read);
                    downloadedBytes += read;

                    if (totalBytes > 0)
                        progress?.Report((int)Math.Min(100, downloadedBytes * 100 / totalBytes));
                }

                await output.FlushAsync(cancellationToken);

                if (asset.Size > 0 && downloadedBytes != asset.Size)
                    throw new InvalidDataException("The downloaded update size does not match the GitHub release.");

                string actualDigest = Convert.ToHexString(hash.GetHashAndReset());
                string expectedDigest = asset.Digest["sha256:".Length..].Trim();

                if (!string.Equals(actualDigest, expectedDigest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded update failed SHA-256 verification.");

                progress?.Report(100);
                return downloadPath;
            }
            catch
            {
                try
                {
                    Directory.Delete(updateDirectory, true);
                }
                catch
                {
                    // Best-effort cleanup only.
                }

                throw;
            }
        }

        public void ScheduleReplacementAndRestart(string downloadedExecutable)
        {
            string currentExecutable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine the running executable path.");

            if (!File.Exists(downloadedExecutable))
                throw new FileNotFoundException("The downloaded update could not be found.", downloadedExecutable);

            string scriptPath = Path.Combine(
                Path.GetDirectoryName(downloadedExecutable)!,
                "install-update.ps1");

            const string script = """
                param(
                    [Parameter(Mandatory=$true)][int]$TargetProcessId,
                    [Parameter(Mandatory=$true)][string]$CurrentPath,
                    [Parameter(Mandatory=$true)][string]$NewPath,
                    [Parameter(Mandatory=$true)][string]$ScriptPath
                )

                $ErrorActionPreference = 'Stop'
                $backupPath = $CurrentPath + '.previous'

                try {
                    Wait-Process -Id $TargetProcessId -Timeout 60 -ErrorAction SilentlyContinue

                    for ($attempt = 1; $attempt -le 20; $attempt++) {
                        try {
                            if (Test-Path -LiteralPath $backupPath) {
                                Remove-Item -LiteralPath $backupPath -Force
                            }

                            Copy-Item -LiteralPath $CurrentPath -Destination $backupPath -Force
                            Copy-Item -LiteralPath $NewPath -Destination $CurrentPath -Force
                            Start-Process -FilePath $CurrentPath -WorkingDirectory (Split-Path -Parent $CurrentPath)
                            Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
                            break
                        }
                        catch {
                            if ($attempt -eq 20) {
                                if (Test-Path -LiteralPath $backupPath) {
                                    Copy-Item -LiteralPath $backupPath -Destination $CurrentPath -Force
                                }
                                throw
                            }
                            Start-Sleep -Milliseconds 500
                        }
                    }
                }
                finally {
                    Remove-Item -LiteralPath $NewPath -Force -ErrorAction SilentlyContinue
                    Remove-Item -LiteralPath $ScriptPath -Force -ErrorAction SilentlyContinue
                }
                """;

            File.WriteAllText(scriptPath, script);

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-WindowStyle");
            startInfo.ArgumentList.Add("Hidden");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-TargetProcessId");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("-CurrentPath");
            startInfo.ArgumentList.Add(currentExecutable);
            startInfo.ArgumentList.Add("-NewPath");
            startInfo.ArgumentList.Add(downloadedExecutable);
            startInfo.ArgumentList.Add("-ScriptPath");
            startInfo.ArgumentList.Add(scriptPath);

            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the update installer.");
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("VoidLaunch", AppInfo.DisplayVersion));
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            return client;
        }

        private static bool TryParseReleaseVersion(string tag, out Version? version)
        {
            string value = tag.Trim().TrimStart('v', 'V');
            value = value.Split('-', '+')[0];

            if (!Version.TryParse(value, out Version? parsed))
            {
                version = null;
                return false;
            }

            version = NormalizeVersion(parsed);
            return true;
        }

        private static Version NormalizeVersion(Version version)
        {
            return new Version(
                Math.Max(version.Major, 0),
                Math.Max(version.Minor, 0),
                Math.Max(version.Build, 0),
                Math.Max(version.Revision, 0));
        }

        private sealed class GitHubRelease
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("tag_name")]
            public string TagName { get; set; } = string.Empty;

            [JsonPropertyName("html_url")]
            public string HtmlUrl { get; set; } = string.Empty;

            [JsonPropertyName("body")]
            public string Body { get; set; } = string.Empty;

            [JsonPropertyName("draft")]
            public bool Draft { get; set; }

            [JsonPropertyName("prerelease")]
            public bool Prerelease { get; set; }

            [JsonPropertyName("published_at")]
            public DateTimeOffset? PublishedAt { get; set; }

            [JsonPropertyName("assets")]
            public GitHubAsset[] Assets { get; set; } = Array.Empty<GitHubAsset>();
        }

        private sealed class GitHubAsset
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("browser_download_url")]
            public string BrowserDownloadUrl { get; set; } = string.Empty;

            [JsonPropertyName("digest")]
            public string Digest { get; set; } = string.Empty;

            [JsonPropertyName("size")]
            public long Size { get; set; }

            [JsonPropertyName("download_count")]
            public int DownloadCount { get; set; }
        }
    }

    public sealed record UpdateAsset(string DownloadUrl, string Digest, long Size);

    public sealed record ReleaseHistoryItem(
        Version? Version,
        string TagName,
        string Name,
        string ReleaseUrl,
        string Notes,
        DateTimeOffset? PublishedAt,
        bool IsPrerelease,
        UpdateAsset? Asset,
        int DownloadCount);

    public sealed record ReleaseHistoryResult(
        bool Succeeded,
        IReadOnlyList<ReleaseHistoryItem> Releases,
        string ErrorMessage)
    {
        public static ReleaseHistoryResult Success(IReadOnlyList<ReleaseHistoryItem> releases) =>
            new(true, releases, string.Empty);

        public static ReleaseHistoryResult Failed(string message) =>
            new(false, Array.Empty<ReleaseHistoryItem>(), message);
    }

    public sealed record UpdateCheckResult(
        bool CheckSucceeded,
        bool UpdateAvailable,
        Version? LatestVersion,
        string ReleaseTag,
        string ReleaseUrl,
        UpdateAsset? Asset,
        string Message)
    {
        public static UpdateCheckResult Failed(string message) =>
            new(false, false, null, string.Empty, AppInfo.ReleasesUrl, null, message);
    }
}
