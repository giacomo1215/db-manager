using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using DbManager.Abstractions;
using DbManager.Models;

namespace DbManager.Services
{
    /// <summary>
    /// Format facade for saved-query backup/restore. Encrypted blobs reuse the
    /// same AES-GCM + KDF-params shape as the connection vault (VaultCrypto), but
    /// the envelope carries a distinct Kind marker so an encrypted query backup is
    /// never mistaken for a connection vault by either format sniffer.
    /// </summary>
    public class QueryBackupService : IQueryBackupService
    {
        // Default PascalCase naming, matching VaultFile casing on disk.
        private static readonly JsonSerializerOptions ExportJsonOptions = new()
        {
            WriteIndented = true
        };

        // The blob's plaintext is the query list; same options as the envelope.
        private static readonly JsonSerializerOptions PayloadJsonOptions = new()
        {
            WriteIndented = false
        };

        public string BuildPlaintextExport(List<SavedQuery> queries) =>
            JsonSerializer.Serialize(
                new QueryExportFile { Queries = queries, ExportedUtc = DateTimeOffset.UtcNow },
                ExportJsonOptions);

        public string BuildEncryptedExport(List<SavedQuery> queries, byte[] key, KdfParams kdf)
        {
            var plaintext = JsonSerializer.Serialize(queries, PayloadJsonOptions);
            var file = new QueryBackupFile
            {
                Kdf = kdf,
                Vault = VaultCrypto.Encrypt(key, plaintext)
            };
            return JsonSerializer.Serialize(file, ExportJsonOptions);
        }

        public QueryBackupFormat DetectFormat(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return QueryBackupFormat.Unrecognized;

                var kind = root.TryGetProperty("Kind", out var k) && k.ValueKind == JsonValueKind.String
                    ? k.GetString()
                    : null;

                if (root.TryGetProperty("Queries", out var queries) && queries.ValueKind == JsonValueKind.Array)
                    return QueryBackupFormat.PlaintextExport;

                if (string.Equals(kind, QueryBackupFile.KindMarker, StringComparison.Ordinal)
                    && root.TryGetProperty("Kdf", out _) && root.TryGetProperty("Vault", out _))
                    return QueryBackupFormat.EncryptedQueries;

                return QueryBackupFormat.Unrecognized;
            }
            catch (JsonException)
            {
                return QueryBackupFormat.Unrecognized;
            }
        }

        public bool TryReadPlaintextExport(string json, out List<SavedQuery> queries, out string error)
        {
            queries = [];
            error = string.Empty;

            try
            {
                var file = JsonSerializer.Deserialize<QueryExportFile>(json, ExportJsonOptions);
                if (file is null || file.Queries is null)
                {
                    error = "The file is not a valid saved-query export.";
                    return false;
                }
                if (!string.Equals(file.Kind, QueryExportFile.KindMarker, StringComparison.OrdinalIgnoreCase))
                {
                    error = "This file is not a db-manager saved-query export.";
                    return false;
                }
                if (file.Version != QueryExportFile.CurrentVersion)
                {
                    error = $"Unsupported export version: {file.Version}.";
                    return false;
                }
                foreach (var query in file.Queries)
                {
                    if (string.IsNullOrWhiteSpace(query.Name))
                    {
                        error = "The backup contains a query without a name.";
                        return false;
                    }
                }
                queries = file.Queries;
                return true;
            }
            catch (JsonException)
            {
                error = "The backup file is corrupted.";
                return false;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        public bool TryReadEncryptedExport(string json, string masterPassword, out List<SavedQuery> queries, out string error)
        {
            queries = [];
            error = string.Empty;

            try
            {
                var file = JsonSerializer.Deserialize<QueryBackupFile>(json, ExportJsonOptions);
                if (file is null || string.IsNullOrEmpty(file.Vault.Ciphertext) || string.IsNullOrEmpty(file.Kdf.Salt))
                {
                    error = "The backup file is missing required sections.";
                    return false;
                }
                if (!string.Equals(file.Kind, QueryBackupFile.KindMarker, StringComparison.Ordinal))
                {
                    error = "This file is not a db-manager saved-query backup.";
                    return false;
                }

                var key = VaultCrypto.DeriveKey(masterPassword, file.Kdf);
                var plaintext = VaultCrypto.Decrypt(key, file.Vault);
                var parsed = JsonSerializer.Deserialize<List<SavedQuery>>(plaintext, PayloadJsonOptions);
                queries = parsed ?? [];
                return true;
            }
            catch (CryptographicException)
            {
                // AES-GCM tag mismatch: wrong master password or a tampered file.
                error = "Wrong master password.";
                return false;
            }
            catch (JsonException)
            {
                error = "The backup file is corrupted.";
                return false;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }
    }
}
