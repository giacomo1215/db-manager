using System.Collections.Generic;
using DbManager.Models;

namespace DbManager.Abstractions
{
    /// <summary>
    /// Stateless facade for the backup/restore feature: format sniffing plus
    /// building/parsing the plaintext export envelope. Encrypted-blob crypto
    /// stays in IVaultService; this service never touches keys.
    /// </summary>
    public interface IBackupService
    {
        /// <summary>The raw on-disk vault file JSON, verbatim — the encrypted export download.</summary>
        string GetEncryptedExport();

        /// <summary>Builds the plaintext export envelope (credentials in cleartext).</summary>
        string BuildPlaintextExport(List<ConnectionProfile> profiles);

        /// <summary>Sniffs the backup format from the JSON root properties. Never throws.</summary>
        BackupFormat DetectFormat(string json);

        /// <summary>Parses and validates a plaintext export envelope.</summary>
        bool TryReadPlaintextExport(string json, out List<ConnectionProfile> profiles, out string error);
    }
}