using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VoidLaunch.Models;

namespace VoidLaunch.Services
{
    public sealed class GameScanner
    {
        private static readonly HashSet<string> IgnoredDirectories =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "_CommonRedist",
                "CommonRedist",
                "redist",
                "Redist",
                "Redistributables",

                "DirectX",
                "DotNet",
                "Windows",
                "System32",

                "CrashReportClient",
                "CrashReporter",
                "EasyAntiCheat",
                "EasyAntiCheat_EOS",
                "BattlEye",

                "Engine",

                "__Installer",
                "Installer",

                // Game SDKs and bundled utilities are not playable games.
                "tools",
                "tool",
                "SDK",

                "Temp",
                "Cache",
                "Caches",
                "Logs",

                "__pycache__",

                ".git",
                ".vs",
                "node_modules"
            };

        private static readonly string[] IgnoredExecutableNames =
        {
            "unins",
            "uninstall",
            "setup",
            "installer",

            "crashreport",
            "crashreporter",
            "crashhandler",

            "redprelauncher",
            "prelauncher",

            "redmod",

            "7za",
            "7zip",
            "cleanup",
            "touchup",
            "dlc-toggler",
            "dlctoggler",
            "language-changer",
            "languagechanger",

            "launcher",

            "updater",
            "update",

            "steam",
            "steamservice",
            "steamwebhelper",

            "epicgameslauncher",
            "epicwebhelper",

            "galaxyclient",

            "dxsetup",
            "vc_redist",
            "vcredist",

            "easyanticheat",
            "eac",

            "battleye",

            "cef",
            "unitycrashhandler"
        };

        private static readonly string[] ArtworkNames =
        {
            // Highest priority.
            "cover.jpg",
            "cover.jpeg",
            "cover.png",
            "cover.webp",

            "game.jpg",
            "game.jpeg",
            "game.png",
            "game.webp",

            "poster.jpg",
            "poster.jpeg",
            "poster.png",
            "poster.webp",

            "banner.jpg",
            "banner.jpeg",
            "banner.png",
            "banner.webp",

            "header.jpg",
            "header.jpeg",
            "header.png",
            "header.webp",

            "thumbnail.jpg",
            "thumbnail.jpeg",
            "thumbnail.png",
            "thumbnail.webp",

            "library.jpg",
            "library.jpeg",
            "library.png",
            "library.webp",

            "capsule.jpg",
            "capsule.jpeg",
            "capsule.png",
            "capsule.webp",

            "icon.jpg",
            "icon.jpeg",
            "icon.png",
            "icon.webp"
        };

        private static readonly HashSet<string> ImageExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".jpg",
                ".jpeg",
                ".png",
                ".bmp",
                ".webp"
            };

        private const int MaxArtworkDepth = 4;

        public async Task<List<GameEntry>> ScanAsync(
            string root,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(root))
                return new List<GameEntry>();

            if (!Directory.Exists(root))
                return new List<GameEntry>();

            return await Task.Run(
                () => ScanInternal(
                    root,
                    progress,
                    cancellationToken),
                cancellationToken);
        }

        private static List<GameEntry> ScanInternal(
            string root,
            IProgress<int>? progress,
            CancellationToken cancellationToken)
        {
            var result =
                new List<GameEntry>();

            var executables =
                new List<string>();

            CollectExecutables(
                root,
                executables,
                cancellationToken);

            if (executables.Count == 0)
                return result;

            var groups =
                executables
                    .GroupBy(
                        x => GetGameRoot(x, root),
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();

            int total =
                groups.Count;

            int processed = 0;

            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                List<string> candidates =
                    group
                        .Distinct(
                            StringComparer.OrdinalIgnoreCase)
                        .ToList();

                string? executable =
                    ChooseBestExecutable(candidates);

                if (string.IsNullOrWhiteSpace(executable))
                    continue;

                string gameRoot =
                    group.Key;

                string name =
                    GameNameFormatter.FromGameDirectory(
                        gameRoot,
                        executable);

                string artwork = FindBestArtwork(gameRoot, executable);

                result.Add(
                    new GameEntry
                    {
                        Name = name,

                        ExecutablePath =
                            Path.GetFullPath(executable),

                        ExecutablePaths =
                            candidates
                                .OrderByDescending(GetExecutableScore)
                                .Select(Path.GetFullPath)
                                .ToList(),

                        InstallDirectory =
                            Path.GetFullPath(gameRoot),

                        ImagePath =
                            artwork,

                        DateAdded =
                            DateTime.Now
                    });

                processed++;

                progress?.Report(
                    Math.Min(
                        100,
                        processed * 100 /
                        Math.Max(1, total)));
            }

            return result
                .OrderBy(
                    x => x.Name,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void CollectExecutables(
            string root,
            List<string> results,
            CancellationToken cancellationToken)
        {
            var directories =
                new Stack<(string Path, int Depth)>();

            directories.Push(
                (Path.GetFullPath(root), 0));

            while (directories.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item =
                    directories.Pop();

                string current =
                    item.Path;

                int depth =
                    item.Depth;

                IEnumerable<string> files;

                try
                {
                    files =
                        Directory.EnumerateFiles(
                            current,
                            "*.exe",
                            SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                foreach (string file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (IsPotentialGameExecutable(file))
                    {
                        results.Add(
                            Path.GetFullPath(file));
                    }
                }

                IEnumerable<string> children;

                try
                {
                    children =
                        Directory.EnumerateDirectories(
                            current,
                            "*",
                            SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                foreach (string child in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (depth >= 32)
                        continue;

                    try
                    {
                        DirectoryInfo info =
                            new(child);

                        if ((info.Attributes &
                             FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        if (IgnoredDirectories.Contains(
                                info.Name))
                        {
                            continue;
                        }

                        directories.Push(
                            (child, depth + 1));
                    }
                    catch
                    {
                        // Ignore inaccessible directories.
                    }
                }
            }
        }

        internal static bool IsPotentialGameExecutable(
            string path)
        {
            string filename =
                Path.GetFileNameWithoutExtension(path);

            if (string.IsNullOrWhiteSpace(filename))
                return false;

            string lower =
                filename.ToLowerInvariant();

            string[] pathParts = path.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            if (pathParts.Any(IgnoredDirectories.Contains))
                return false;

            foreach (string ignored in IgnoredExecutableNames)
            {
                if (lower.Contains(ignored))
                    return false;
            }

            try
            {
                FileInfo info =
                    new(path);

                if (!info.Exists)
                    return false;

                // Ignore tiny helper executables.
                if (info.Length < 512 * 1024)
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        public async Task<string> GetFolderSignatureAsync(
            string root,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(
                () => GetFolderSignature(root, cancellationToken),
                cancellationToken);
        }

        public static string FindBestArtwork(string gameDirectory, string executablePath)
        {
            string artwork = FindArtwork(gameDirectory);
            return string.IsNullOrWhiteSpace(artwork)
                ? ExtractExecutableIcon(executablePath)
                : artwork;
        }

        private static string GetFolderSignature(string root, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(root))
                return string.Empty;

            var entries = new List<string>();
            var directories = new Stack<(string Path, int Depth)>();
            string fullRoot = Path.GetFullPath(root);
            directories.Push((fullRoot, 0));

            while (directories.Count > 0 && entries.Count < 10000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string current, int depth) = directories.Pop();

                try
                {
                    var info = new DirectoryInfo(current);
                    entries.Add($"D|{Path.GetRelativePath(fullRoot, current)}|{info.LastWriteTimeUtc.Ticks}");

                    foreach (string executable in Directory.EnumerateFiles(current, "*.exe", SearchOption.TopDirectoryOnly))
                    {
                        var file = new FileInfo(executable);
                        entries.Add(
                            $"E|{Path.GetRelativePath(fullRoot, executable)}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
                    }

                    if (depth >= 2)
                        continue;

                    foreach (string child in Directory.EnumerateDirectories(current))
                    {
                        var childInfo = new DirectoryInfo(child);
                        if ((childInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
                            IgnoredDirectories.Contains(childInfo.Name))
                        {
                            continue;
                        }

                        directories.Push((child, depth + 1));
                    }
                }
                catch
                {
                    // Inaccessible folders do not prevent other folders from being fingerprinted.
                }
            }

            entries.Sort(StringComparer.OrdinalIgnoreCase);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries)));
            return Convert.ToHexString(hash);
        }

        private static string? ChooseBestExecutable(
            List<string> executables)
        {
            return executables
                .OrderByDescending(
                    GetExecutableScore)
                .FirstOrDefault();
        }

        internal static int GetExecutableScore(
            string path)
        {
            string name =
                Path.GetFileNameWithoutExtension(path)
                    .ToLowerInvariant();

            int score = 0;

            if (name.Contains("win64"))
                score += 100;

            if (name.Contains("win32"))
                score += 90;

            if (name.Contains("shipping"))
                score += 80;

            if (name.Contains("game"))
                score += 40;

            if (name.Contains("client"))
                score += 20;

            if (name.Contains("x64"))
                score += 35;

            if (name.Contains("dx9"))
                score -= 15;

            if (name.Contains("fpb"))
                score -= 20;

            if (name.Contains("launch"))
                score -= 100;

            if (name.Contains("launcher"))
                score -= 150;

            if (name.Contains("server"))
                score -= 100;

            if (name.Contains("editor"))
                score -= 100;

            if (name.Contains("test"))
                score -= 50;

            return score;
        }

        private static string GetGameRoot(
            string executable,
            string scanRoot)
        {
            string fullExe =
                Path.GetFullPath(executable);

            string fullRoot =
                Path.GetFullPath(scanRoot)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);

            string? current =
                Path.GetDirectoryName(fullExe);

            if (string.IsNullOrWhiteSpace(current))
                return fullRoot;

            string? previous =
                current;

            while (!string.IsNullOrWhiteSpace(current))
            {
                string normalizedCurrent =
                    current.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);

                if (string.Equals(
                        normalizedCurrent,
                        fullRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                previous =
                    current;

                string? parent =
                    Path.GetDirectoryName(current);

                if (string.IsNullOrWhiteSpace(parent))
                    break;

                string normalizedParent =
                    parent.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);

                if (string.Equals(
                        normalizedParent,
                        fullRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                current =
                    parent;
            }

            return previous ?? fullRoot;
        }

        private static string FindArtwork(
            string directory)
        {
            if (!Directory.Exists(directory))
                return string.Empty;

            // First pass:
            // look specifically for well-known artwork names.
            foreach (string artworkName in ArtworkNames)
            {
                string? result =
                    FindFileByName(
                        directory,
                        artworkName,
                        MaxArtworkDepth);

                if (!string.IsNullOrWhiteSpace(result))
                    return result;
            }

            // Second pass:
            // find a good-looking image anywhere nearby.
            var candidates =
                new List<(string Path, int Score)>();

            CollectImageCandidates(
                directory,
                0,
                candidates);

            return candidates
                .OrderByDescending(x => x.Score)
                .ThenBy(
                    x => x.Path.Length)
                .Select(x => x.Path)
                .FirstOrDefault()
                ?? string.Empty;
        }

        private static string? FindFileByName(
            string root,
            string filename,
            int maxDepth)
        {
            var directories =
                new Stack<(string Path, int Depth)>();

            directories.Push((root, 0));

            while (directories.Count > 0)
            {
                var current =
                    directories.Pop();

                try
                {
                    string candidate =
                        Path.Combine(
                            current.Path,
                            filename);

                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                    // Ignore.
                }

                if (current.Depth >= maxDepth)
                    continue;

                IEnumerable<string> children;

                try
                {
                    children =
                        Directory.EnumerateDirectories(
                            current.Path,
                            "*",
                            SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                foreach (string child in children)
                {
                    try
                    {
                        DirectoryInfo info =
                            new(child);

                        if ((info.Attributes &
                             FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        if (IgnoredDirectories.Contains(
                                info.Name))
                        {
                            continue;
                        }

                        directories.Push(
                            (child,
                             current.Depth + 1));
                    }
                    catch
                    {
                        // Ignore.
                    }
                }
            }

            return null;
        }

        private static void CollectImageCandidates(
            string directory,
            int depth,
            List<(string Path, int Score)> results)
        {
            if (depth > MaxArtworkDepth)
                return;

            IEnumerable<string> files;

            try
            {
                files =
                    Directory.EnumerateFiles(
                        directory,
                        "*.*",
                        SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return;
            }

            foreach (string file in files)
            {
                string extension =
                    Path.GetExtension(file);

                if (!ImageExtensions.Contains(extension))
                    continue;

                int score =
                    ScoreArtworkFile(file, depth);

                results.Add(
                    (file, score));
            }

            if (depth >= MaxArtworkDepth)
                return;

            IEnumerable<string> children;

            try
            {
                children =
                    Directory.EnumerateDirectories(
                        directory,
                        "*",
                        SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return;
            }

            foreach (string child in children)
            {
                try
                {
                    DirectoryInfo info =
                        new(child);

                    if ((info.Attributes &
                         FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if (IgnoredDirectories.Contains(
                            info.Name))
                    {
                        continue;
                    }

                    CollectImageCandidates(
                        child,
                        depth + 1,
                        results);
                }
                catch
                {
                    // Ignore inaccessible directories.
                }
            }
        }

        private static int ScoreArtworkFile(
            string path,
            int depth)
        {
            string filename =
                Path.GetFileNameWithoutExtension(path)
                    .ToLowerInvariant();

            int score =
                Math.Max(
                    0,
                    50 - depth * 10);

            if (filename.Contains("cover"))
                score += 100;

            if (filename.Contains("poster"))
                score += 95;

            if (filename.Contains("banner"))
                score += 90;

            if (filename.Contains("header"))
                score += 85;

            if (filename.Contains("capsule"))
                score += 85;

            if (filename.Contains("library"))
                score += 80;

            if (filename.Contains("thumbnail"))
                score += 75;

            if (filename.Contains("game"))
                score += 60;

            if (filename.Contains("art"))
                score += 40;

            if (filename.Contains("logo"))
                score += 30;

            if (filename.Contains("icon"))
                score += 20;

            // Very tiny images are usually UI assets.
            try
            {
                FileInfo info =
                    new(path);

                if (info.Length < 10 * 1024)
                    score -= 30;

                if (info.Length > 50 * 1024)
                    score += 10;
            }
            catch
            {
                // Ignore.
            }

            return score;
        }

        private static string ExtractExecutableIcon(
            string executablePath)
        {
            try
            {
                string cacheDirectory =
                    Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.LocalApplicationData),
                        "VoidLaunch",
                        "IconCache");

                Directory.CreateDirectory(
                    cacheDirectory);

                string safeName =
                    Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(
                                Path.GetFullPath(executablePath)
                                    .ToLowerInvariant())))
                        .Substring(0, 24);

                string output =
                    Path.Combine(
                        cacheDirectory,
                        safeName + ".png");

                if (File.Exists(output))
                    return output;

                using Icon? icon =
                    Icon.ExtractAssociatedIcon(
                        executablePath);

                if (icon == null)
                    return string.Empty;

                using Bitmap bitmap =
                    icon.ToBitmap();

                bitmap.Save(
                    output,
                    ImageFormat.Png);

                return output;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
