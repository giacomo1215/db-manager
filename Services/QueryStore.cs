using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DbManager.Abstractions;
using DbManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace DbManager.Services
{
    /// <summary>
    /// SQLite-backed saved-query library. The file is tiny and lives beside the
    /// connection vault (default <c>~/.db-manager/queries.db</c>, override with
    /// <c>QueryStore:Path</c>). Only <c>Name</c> and the connection tag are stored
    /// in the clear; each query's SQL is AES-GCM-encrypted under the session vault
    /// key (the same key that protects connections.json), so the library is only
    /// readable while the vault is unlocked.
    /// </summary>
    public class QueryStore : IQueryStore
    {
        private readonly ISessionVault _session;
        private readonly string _dbPath;
        private bool _schemaReady;

        public QueryStore(IConfiguration configuration, ISessionVault session)
        {
            _session = session;
            var configured = configuration["QueryStore:Path"] ?? "~/.db-manager/queries.db";
            _dbPath = ExpandPath(configured);
        }

        public bool IsAvailable => _session.DerivedKey is not null;

        public List<SavedQuery> GetAll()
        {
            var key = RequireKey();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Name, ProfileId, EngineKey, Nonce, SqlCipher, CreatedUtc, UpdatedUtc FROM queries ORDER BY Name COLLATE NOCASE";

            var result = new List<SavedQuery>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var blob = new VaultBlob
                {
                    Nonce = reader.GetString(4),
                    Ciphertext = reader.GetString(5)
                };
                string sql;
                try
                {
                    sql = VaultCrypto.Decrypt(key, blob);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // Written under a key we no longer hold (e.g. an unrelated
                    // vault was restored): skip rather than kill the whole list.
                    continue;
                }

                result.Add(new SavedQuery
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    Name = reader.GetString(1),
                    ProfileId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                    EngineKey = reader.GetString(3),
                    Sql = sql,
                    CreatedUtc = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                    UpdatedUtc = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture)
                });
            }
            return result;
        }

        public SavedQuery Add(string name, string sql, Guid? profileId, string engineKey)
        {
            var key = RequireKey();
            var query = new SavedQuery
            {
                Name = name,
                Sql = sql,
                ProfileId = profileId,
                EngineKey = engineKey
            };
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            BindInsert(cmd, key, query);
            cmd.ExecuteNonQuery();
            return query;
        }

        public void Rename(Guid id, string name)
        {
            var key = RequireKey();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE queries SET Name = $name, UpdatedUtc = $updated WHERE Id = $id";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.ExecuteNonQuery();
        }

        public void Delete(Guid id)
        {
            _ = RequireKey();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM queries WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.ExecuteNonQuery();
        }

        public void Reencrypt(byte[] oldKey, byte[] newKey)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            var rows = new List<(string Id, string Nonce, string Cipher)>();
            using (var read = conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = "SELECT Id, Nonce, SqlCipher FROM queries";
                using var reader = read.ExecuteReader();
                while (reader.Read())
                    rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }

            foreach (var (id, nonce, cipher) in rows)
            {
                var plain = VaultCrypto.Decrypt(oldKey, new VaultBlob { Nonce = nonce, Ciphertext = cipher });
                var blob = VaultCrypto.Encrypt(newKey, plain);
                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = "UPDATE queries SET Nonce = $nonce, SqlCipher = $cipher WHERE Id = $id";
                upd.Parameters.AddWithValue("$nonce", blob.Nonce);
                upd.Parameters.AddWithValue("$cipher", blob.Ciphertext);
                upd.Parameters.AddWithValue("$id", id);
                upd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        public void ReplaceAll(IEnumerable<SavedQuery> queries)
        {
            var key = RequireKey();
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM queries";
                del.ExecuteNonQuery();
            }

            foreach (var query in queries)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                BindInsert(cmd, key, query);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        // --- internals -----------------------------------------------------------

        private static void BindInsert(SqliteCommand cmd, byte[] key, SavedQuery query)
        {
            var blob = VaultCrypto.Encrypt(key, query.Sql);
            cmd.CommandText =
                "INSERT INTO queries (Id, Name, ProfileId, EngineKey, Nonce, SqlCipher, CreatedUtc, UpdatedUtc) " +
                "VALUES ($id, $name, $profile, $engine, $nonce, $cipher, $created, $updated)";
            cmd.Parameters.AddWithValue("$id", query.Id.ToString());
            cmd.Parameters.AddWithValue("$name", query.Name);
            cmd.Parameters.AddWithValue("$profile", (object?)query.ProfileId?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$engine", query.EngineKey);
            cmd.Parameters.AddWithValue("$nonce", blob.Nonce);
            cmd.Parameters.AddWithValue("$cipher", blob.Ciphertext);
            cmd.Parameters.AddWithValue("$created", query.CreatedUtc.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$updated", query.UpdatedUtc.ToString("o", CultureInfo.InvariantCulture));
        }

        private byte[] RequireKey() =>
            _session.DerivedKey ?? throw new InvalidOperationException("The vault is locked.");

        private SqliteConnection Open()
        {
            EnsureSchema();
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
            conn.Open();
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
                "CREATE TABLE IF NOT EXISTS queries (" +
                "Id TEXT PRIMARY KEY, " +
                "Name TEXT NOT NULL, " +
                "ProfileId TEXT NULL, " +
                "EngineKey TEXT NOT NULL DEFAULT '', " +
                "Nonce TEXT NOT NULL, " +
                "SqlCipher TEXT NOT NULL, " +
                "CreatedUtc TEXT NOT NULL, " +
                "UpdatedUtc TEXT NOT NULL)";
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
