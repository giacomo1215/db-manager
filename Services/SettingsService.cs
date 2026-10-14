using System;
using System.IO;
using System.Text.Json;
using DbManager.Abstractions;
using DbManager.Models;
using Microsoft.Extensions.Configuration;

namespace DbManager.Services
{
    /// <summary>
    /// File-backed implementation of the user preferences. Unlike the connection
    /// vault this is a plain JSON document — the values here are not secret, and
    /// the editor reads them while the vault may still be locked. The loaded
    /// values are cached so the query hot path never hits disk.
    /// </summary>
    public class SettingsService : ISettingsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly string _path;
        private AppSettings _current;

        public SettingsService(IConfiguration configuration)
        {
            var configured = configuration["Settings:Path"] ?? "~/.db-manager/settings.json";
            _path = ExpandPath(configured);
            _current = Read();
        }

        public AppSettings Current => _current;

        public void Save(AppSettings settings)
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);

            // Atomic: a half-written preferences file must never exist.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, _path, overwrite: true);

            _current = settings;
        }

        /// <summary>
        /// Loads the file, falling back to defaults on any read/parse failure —
        /// a missing or corrupted settings file must never block the app.
        /// </summary>
        private AppSettings Read()
        {
            try
            {
                if (!File.Exists(_path))
                    return new AppSettings();
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions)
                    ?? new AppSettings();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new AppSettings();
            }
        }

        /// <summary>Expands a leading ~/ to the user's home directory.</summary>
        private static string ExpandPath(string path)
        {
            if (!path.StartsWith("~/", StringComparison.Ordinal))
                return path;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path[2..]);
        }
    }
}
