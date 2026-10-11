using System;
using System.Collections.Generic;
using System.Text.Json;
using DbManager.Abstractions;
using DbManager.Models;

namespace DbManager.Services
{
    /// <summary>
    /// Format facade for backup/restore: sniffing uploaded files and building/
    /// parsing the plaintext export envelope. All key material work (PBKDF2,
    /// AES-GCM) is delegated to IVaultService; this class is pure JSON.
    /// </summary>
    public class BackupService : IBackupService
    {
        // Both app-produced formats use default PascalCase naming, so the
        // export and the sniff below agree with the on-disk VaultFile casing.
        private static readonly JsonSerializerOptions ExportJsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly IVaultService _vaultService;

        public BackupService(IVaultService vaultService) => _vaultService = vaultService;

        public string GetEncryptedExport() => _vaultService.ReadVaultFileText();

        public string BuildPlaintextExport(List<ConnectionProfile> profiles) =>
            JsonSerializer.Serialize(
                new PlaintextExportFile { Kind = PlaintextExportFile.KindMarker, Profiles = profiles, ExportedUtc = DateTimeOffset.UtcNow },
                ExportJsonOptions);

        public BackupFormat DetectFormat(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return BackupFormat.Unrecognized;
                // Structural, not marker-based: a VaultFile never has Profiles,
                // the export envelope never has Kdf/Vault.
                if (root.TryGetProperty("Profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
                    return BackupFormat.PlaintextExport;
                if (root.TryGetProperty("Kdf", out _) && root.TryGetProperty("Vault", out _))
                    return BackupFormat.EncryptedVault;
                return BackupFormat.Unrecognized;
            }
            catch (JsonException)
            {
                return BackupFormat.Unrecognized;
            }
        }

        public bool TryReadPlaintextExport(string json, out List<ConnectionProfile> profiles, out string error)
        {
            profiles = [];
            error = string.Empty;

            try
            {
                var file = JsonSerializer.Deserialize<PlaintextExportFile>(json, ExportJsonOptions);
                if (file is null || file.Profiles is null)
                {
                    error = "The backup file is not a valid plaintext export.";
                    return false;
                }
                if (!string.Equals(file.Kind, PlaintextExportFile.KindMarker, StringComparison.OrdinalIgnoreCase))
                {
                    error = "This file is not a db-manager plaintext export.";
                    return false;
                }
                if (file.Version != PlaintextExportFile.CurrentVersion)
                {
                    error = $"Unsupported export version: {file.Version}.";
                    return false;
                }
                foreach (var profile in file.Profiles)
                {
                    if (string.IsNullOrWhiteSpace(profile.Name))
                    {
                        error = "The backup contains a profile without a name.";
                        return false;
                    }
                    if (string.IsNullOrWhiteSpace(profile.ConnectionString))
                    {
                        error = $"The profile \"{profile.Name}\" has no connection string.";
                        return false;
                    }
                }
                profiles = file.Profiles;
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
    }
}