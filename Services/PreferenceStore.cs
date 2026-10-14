using System;
using System.IO;
using DbManager.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace DbManager.Services
{
    /// <summary>
    /// SQLite-backed preferences, living in the SAME file as the saved-query
    /// library (<c>QueryStore:Path</c>, default <c>~/.db-manager/queries.db</c>).
    /// The <c>settings</c> table is plaintext and needs no vault key, so the
    /// theme can be read while the vault is still locked and before the first
    /// paint.
    ///
    /// Singleton: the current theme is cached in memory so reads never hit disk;
    /// writes go straight through. Both this store and <see cref="QueryStore"/>
    /// open the file in WAL mode with a busy timeout so the two writers coexist.
    /// </summary>
    public class PreferenceStore : IPreferenceStore
    {
        private const string ThemeKey = "theme";
        private const string DefaultTheme = "light";

        // Guards the cached value against concurrent circuit writes.
        private readonly object _gate = new();
        private readonly string _dbPath;
        private bool _schemaReady;
        private string _theme;

        public PreferenceStore(IConfiguration configuration)
        {
            var configured = configuration["QueryStore:Path"] ?? "~/.db-manager/queries.db";
            _dbPath = ExpandPath(configured);
            _theme = Load(ThemeKey) ?? DefaultTheme;
        }

        public string Theme
        {
            get { lock (_gate) return _theme; }
            set
            {
                var next = value == "dark" ? "dark" : "light";
                lock (_gate)
                {
                    Save(ThemeKey, next);
                    _theme = next;
                }
            }
        }

        /// <summary>Reads a value, or null when the table/file does not exist yet or is unreadable.</summary>
        private string? Load(string key)
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Value FROM settings WHERE Key = $key";
                cmd.Parameters.AddWithValue("$key", key);
                return cmd.ExecuteScalar() as string;
            }
            catch (Exception ex) when (ex is SqliteException or IOException)
            {
                // A missing/locked/corrupt file must never block startup.
                return null;
            }
        }

        private void Save(string key, string value)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            // Upsert: SQLite's ON CONFLICT gives us insert-or-update in one shot.
            cmd.CommandText =
                "INSERT INTO settings (Key, Value) VALUES ($key, $value) " +
                "ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
        }

        private SqliteConnection Open()
        {
            EnsureSchema();
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
            conn.Open();
            using var pragma = conn.CreateCommand();
            // WAL + a busy timeout let this store and the scoped QueryStore write
            // the same file without tripping SQLITE_BUSY under overlap.
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
            return conn;
        }

        private void EnsureSchema()
        {
            if (_schemaReady)
                return;

            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS settings (" +
                "Key TEXT PRIMARY KEY, " +
                "Value TEXT NOT NULL)";
            cmd.ExecuteNonQuery();

            _schemaReady = true;
        }

        private static string ExpandPath(string path)
        {
            if (!path.StartsWith("~/", StringComparison.Ordinal))
                return path;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path[2..]);
        }
    }
}
