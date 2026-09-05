using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VoidLaunch.Models;

namespace VoidLaunch.Services
{
    public sealed class LibraryService
    {
        private readonly string _directory;
        private readonly string _file;
        private readonly string _defaultDirectory;
        private readonly string _portableDirectory;
        private readonly string _portableMarker;
        private readonly SemaphoreSlim _saveGate = new SemaphoreSlim(1, 1);

        private readonly JsonSerializerOptions _options =
            new JsonSerializerOptions
            {
                WriteIndented = true
            };

        public LibraryService()
        {
            _defaultDirectory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData),
                "VoidLaunch");

            string executableDirectory = Path.GetDirectoryName(Environment.ProcessPath)
                ?? AppContext.BaseDirectory;
            _portableDirectory = Path.Combine(executableDirectory, "VoidLaunchData");
            _portableMarker = Path.Combine(executableDirectory, "VoidLaunch.portable");
            _directory = File.Exists(_portableMarker)
                ? _portableDirectory
                : _defaultDirectory;

            _file = Path.Combine(
                _directory,
                "library.json");
        }

        public string DataFilePath => _file;
        public string DataDirectory => _directory;
        public bool IsPortableMode => string.Equals(
            _directory,
            _portableDirectory,
            StringComparison.OrdinalIgnoreCase);

        public async Task<LibraryData> LoadAsync()
        {
            try
            {
                if (!File.Exists(_file))
                    return new LibraryData();

                string json =
                    await File.ReadAllTextAsync(_file);

                return JsonSerializer.Deserialize<LibraryData>(
                           json,
                           _options)
                       ?? new LibraryData();
            }
            catch
            {
                return new LibraryData();
            }
        }

        public async Task SaveAsync(LibraryData data)
        {
            await _saveGate.WaitAsync();
            try
            {
                Directory.CreateDirectory(_directory);

                string json =
                    JsonSerializer.Serialize(
                        data,
                        _options);

                string tempFile =
                    _file + ".tmp";

                await File.WriteAllTextAsync(
                    tempFile,
                    json);

                File.Move(
                    tempFile,
                    _file,
                    true);
            }
            catch
            {
                // Don't crash the launcher if the library
                // cannot be saved.
            }
            finally
            {
                _saveGate.Release();
            }
        }

        public async Task<string> ImportCoverAsync(string gameId, string sourcePath)
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("The selected cover could not be found.", sourcePath);

            string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            string[] allowedExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
            if (!allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a JPG, PNG, BMP, or WEBP image.");

            string coverDirectory = Path.Combine(_directory, "CoverCache");
            Directory.CreateDirectory(coverDirectory);
            string destination = Path.Combine(coverDirectory, gameId + extension);

            if (string.Equals(
                    Path.GetFullPath(sourcePath),
                    Path.GetFullPath(destination),
                    StringComparison.OrdinalIgnoreCase))
            {
                return destination;
            }

            await using FileStream source = File.OpenRead(sourcePath);
            await using FileStream output = new FileStream(
                destination,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                true);
            await source.CopyToAsync(output);
            return destination;
        }

        public async Task CreateBackupAsync(string destinationPath, LibraryData data)
        {
            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            await using FileStream output = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                true);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, false);

            ZipArchiveEntry libraryEntry = archive.CreateEntry("library.json", CompressionLevel.Optimal);
            await using (Stream libraryStream = libraryEntry.Open())
                await JsonSerializer.SerializeAsync(libraryStream, data, _options);

            foreach (GameEntry game in data.Games.Where(game =>
                         game.ImageManuallySelected && File.Exists(game.ImagePath)))
            {
                string extension = Path.GetExtension(game.ImagePath).ToLowerInvariant();
                ZipArchiveEntry coverEntry = archive.CreateEntry(
                    $"covers/{game.Id}{extension}",
                    CompressionLevel.Optimal);
                await using Stream coverOutput = coverEntry.Open();
                await using FileStream coverInput = File.OpenRead(game.ImagePath);
                await coverInput.CopyToAsync(coverOutput);
            }
        }

        public async Task<LibraryData> RestoreBackupAsync(string backupPath)
        {
            await using FileStream input = File.OpenRead(backupPath);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, false);
            ZipArchiveEntry libraryEntry = archive.GetEntry("library.json")
                ?? throw new InvalidDataException("This backup does not contain library.json.");

            LibraryData restored;
            await using (Stream libraryStream = libraryEntry.Open())
            {
                restored = await JsonSerializer.DeserializeAsync<LibraryData>(libraryStream, _options)
                    ?? throw new InvalidDataException("The library data in this backup is invalid.");
            }

            string coverDirectory = Path.Combine(_directory, "CoverCache");
            Directory.CreateDirectory(coverDirectory);
            foreach (GameEntry game in restored.Games.Where(game => game.ImageManuallySelected))
            {
                ZipArchiveEntry? coverEntry = archive.Entries.FirstOrDefault(entry =>
                    entry.FullName.StartsWith($"covers/{game.Id}.", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entry.Name, Path.GetFileName(entry.FullName), StringComparison.Ordinal));
                if (coverEntry is null)
                {
                    if (!File.Exists(game.ImagePath))
                        game.ImageManuallySelected = false;
                    continue;
                }

                string extension = Path.GetExtension(coverEntry.Name).ToLowerInvariant();
                string[] allowedExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
                if (!allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    continue;

                string coverPath = Path.Combine(coverDirectory, game.Id + extension);
                await using Stream coverInput = coverEntry.Open();
                await using FileStream coverOutput = new FileStream(
                    coverPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    true);
                await coverInput.CopyToAsync(coverOutput);
                game.ImagePath = coverPath;
            }

            return restored;
        }

        public async Task SetPortableModeAsync(bool enabled, LibraryData data)
        {
            string targetDirectory = enabled ? _portableDirectory : _defaultDirectory;
            Directory.CreateDirectory(targetDirectory);

            string sourceLogs = Path.Combine(_directory, "Logs");
            string targetLogs = Path.Combine(targetDirectory, "Logs");
            if (Directory.Exists(sourceLogs) &&
                !string.Equals(
                    Path.GetFullPath(sourceLogs),
                    Path.GetFullPath(targetLogs),
                    StringComparison.OrdinalIgnoreCase))
            {
                CopyDirectory(sourceLogs, targetLogs);
            }

            foreach (GameEntry game in data.Games.Where(game =>
                         game.ImageManuallySelected && File.Exists(game.ImagePath)))
            {
                string coverDirectory = Path.Combine(targetDirectory, "CoverCache");
                Directory.CreateDirectory(coverDirectory);
                string destination = Path.Combine(
                    coverDirectory,
                    game.Id + Path.GetExtension(game.ImagePath).ToLowerInvariant());
                if (!string.Equals(
                        Path.GetFullPath(game.ImagePath),
                        Path.GetFullPath(destination),
                        StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(game.ImagePath, destination, true);
                }
                game.ImagePath = destination;
            }

            string targetFile = Path.Combine(targetDirectory, "library.json");
            string temporaryFile = targetFile + ".tmp";
            await File.WriteAllTextAsync(temporaryFile, JsonSerializer.Serialize(data, _options));
            File.Move(temporaryFile, targetFile, true);

            if (enabled)
                await File.WriteAllTextAsync(_portableMarker, "VoidLaunch portable mode");
            else if (File.Exists(_portableMarker))
                File.Delete(_portableMarker);
        }

        private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);
            foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                File.Copy(
                    file,
                    Path.Combine(destinationDirectory, Path.GetFileName(file)),
                    true);
            }

            foreach (string directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    continue;

                CopyDirectory(
                    directory,
                    Path.Combine(destinationDirectory, Path.GetFileName(directory)));
            }
        }
    }
}   
