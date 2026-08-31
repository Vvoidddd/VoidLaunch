using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using VoidLaunch.Models;

namespace VoidLaunch.Services
{
    public sealed class LibraryService
    {
        private readonly string _directory;
        private readonly string _file;

        private readonly JsonSerializerOptions _options =
            new JsonSerializerOptions
            {
                WriteIndented = true
            };

        public LibraryService()
        {
            _directory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData),
                "VoidLaunch");

            _file = Path.Combine(
                _directory,
                "library.json");
        }

        public string DataFilePath => _file;

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
        }
    }
}   
