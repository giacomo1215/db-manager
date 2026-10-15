using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DbManager.Abstractions;
using DbManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace DbManager.Services
{
    /// <summary>
    /// SQLite-backed rolling query history, living in the SAME file as the
    /// saved-query library (default <c>~/.db-manager/queries.db</c>, override with
    /// <c>QueryStore:Path</c>). Only the run summary (connection, timing, outcome)
    /// is stored in the clear; each statement's SQL is AES-GCM-encrypted under the
    /// session vault key. Old rows are trimmed to <see cref="RetentionLimit"/> so
    /// the file stays small without a manual "clear" ever being required.
    /// </summary>
    public class QueryHistoryStore : IQueryHistoryStore
    {
        /// <summary>Newest N statements are kept; older ones are dropped on each insert.</summary>
        private const int RetentionLimit = 500;

        private readonly ISessionVault _session;
        private readonly string _dbPath;
        private bool _schemaReady;

        public QueryHistoryStore(IConfiguration configuration, ISessionVault session)
        {
            _session = session;
            var configured = configuration["QueryStore:Path"] ?? "~/.db-manager/queries.db";
            _dbPath = ExpandPath(configured);
        }

        public bool IsAvailable => _session.DerivedKey is not null;

        public void Add(QueryHistoryEntry entry)
        {
            // Never-throw contract: history is a convenience, not part of the run.
            // A locked vault or a busy file must not surface as a query error.
            try
            {
                var key = _session.DerivedKey;
                if (key is null)
                    return;

                var blob = VaultCrypto.Encrypt(key, entry.Sql);
                using var conn = Open();
                using var tx = conn.BeginTransaction();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "INSERT INTO history (Id, ProfileId, ProfileName, EngineKey, Nonce, SqlCipher, " +
                        "ExecutedUtc, ElapsedMs, RowCount, Succeeded, Error) " +
                        "VALUES ($id, $profile, $name, $engine, $nonce, $cipher, $executed, $elapsed, $rows, $ok, $error)";
                    cmd.Parameters.AddWithValue("$id", entry.Id.ToString());
                    cmd.Parameters.AddWithValue("$profile", (object?)entry.ProfileId?.ToString() ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$name", entry.ProfileName);
                    cmd.Parameters.AddWithValue("$engine", entry.EngineKey);
                    cmd.Parameters.AddWithValue("$nonce", blob.Nonce);
                    cmd.Parameters.AddWithValue("$cipher", blob.Ciphertext);
                    cmd.Parameters.AddWithValue("$executed", entry.ExecutedUtc.ToString("o", CultureInfo.InvariantCulture));
                    cmd.Parameters.AddWithValue("$elapsed", entry.ElapsedMs);
                    cmd.Parameters.AddWithValue("$rows", entry.RowCount);
                    cmd.Parameters.AddWithValue("$ok", entry.Succeeded ? 1 : 0);
                    cmd.Parameters.AddWithValue("$error", (object?)entry.Error ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }

                // Retention: drop everything below the newest N by timestamp.
                using (var trim = conn.CreateCommand())
                {
                    trim.Transaction = tx;
                    trim.CommandText =
                        "DELETE FROM history WHERE Id NOT IN (" +
                        "SELECT Id FROM history ORDER BY ExecutedUtc DESC LIMIT $limit)";
                    trim.Parameters.AddWithValue("$limit", RetentionLimit);
                    trim.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
            {
                // Silently ignore: the run already happened; history is best-effort.
            }
        }

        public List<QueryHistoryEntry> GetRecent(int limit)
        {
            var key = RequireKey();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT Id, ProfileId, ProfileName, EngineKey, Nonce, SqlCipher, ExecutedUtc, " +
                "ElapsedMs, RowCount, Succeeded, Error FROM history ORDER BY ExecutedUtc DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);

            var result = new List<QueryHistoryEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string sql;
                try
                {
                    sql = VaultCrypto.Decrypt(key, new VaultBlob
                    {
                        Nonce = reader.GetString(4),
                        Ciphertext = reader.GetString(5)
                    });
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    continue; // written under a key we no longer hold — skip the row
                }

                result.Add(new QueryHistoryEntry
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    ProfileId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                    ProfileName = reader.GetString(2),
                    EngineKey = reader.GetString(3),
                    Sql = sql,
                    ExecutedUtc = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                    ElapsedMs = reader.GetInt64(7),
                    RowCount = reader.GetInt32(8),
                    Succeeded = reader.GetInt32(9) != 0,
                    Error = reader.IsDBNull(10) ? null : reader.GetString(10)
                });
            }
            return result;
        }

        public void Clear()
        {
            _ = RequireKey();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM history";
            cmd.ExecuteNonQuery();
        }

        // --- internals -----------------------------------------------------------

        private byte[] RequireKey() =>
            _session.DerivedKey ?? throw new InvalidOperationException("The vault is locked.");

        private SqliteConnection Open()
        {
            EnsureSchema();
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
            conn.Open();
            using var pragma = conn.CreateCommand();
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
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "CREATE TABLE IF NOT EXISTS history (" +
                    "Id TEXT PRIMARY KEY, " +
                    "ProfileId TEXT NULL, " +
                    "ProfileName TEXT NOT NULL DEFAULT '', " +
                    "EngineKey TEXT NOT NULL DEFAULT '', " +
                    "Nonce TEXT NOT NULL, " +
                    "SqlCipher TEXT NOT NULL, " +
                    "ExecutedUtc TEXT NOT NULL, " +
                    "ElapsedMs INTEGER NOT NULL, " +
                    "RowCount INTEGER NOT NULL, " +
                    "Succeeded INTEGER NOT NULL, " +
                    "Error TEXT NULL)";
                cmd.ExecuteNonQuery();
            }

            // Newest-first listing is the only query shape this table serves.
            using (var idx = conn.CreateCommand())
            {
                idx.CommandText = "CREATE INDEX IF NOT EXISTS ix_history_executed ON history(ExecutedUtc DESC)";
                idx.ExecuteNonQuery();
            }

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
