using System;
using System.Collections.Generic;

namespace DbManager.Models
{
    /// <summary>
    /// Envelope for the plaintext backup export. Connection strings — including
    /// credentials — are in cleartext here, so the UI must warn before exporting.
    /// "Kind" doubles as a self-describing marker; import format detection keys
    /// off the structural difference to VaultFile instead (Profiles array
    /// present vs. Kdf+Vault present), so a hand-edited envelope without the
    /// marker still sniffs correctly.
    /// </summary>
    public class PlaintextExportFile
    {
        public const string KindMarker = "dbm-plaintext-export";
        public const int CurrentVersion = 1;

        /// <summary>
        /// Provenance marker, null when absent from a hand-crafted file — import
        /// validation rejects that, while DetectFormat still sniffs structurally.
        /// </summary>
        public string? Kind { get; set; }

        public int Version { get; set; } = CurrentVersion;

        public DateTimeOffset ExportedUtc { get; set; } = DateTimeOffset.UtcNow;

        public List<ConnectionProfile> Profiles { get; set; } = [];
    }

    /// <summary>Backup format sniffed from the JSON root properties.</summary>
    public enum BackupFormat
    {
        /// <summary>A VaultFile ({ Version, Kdf, Vault }) — AES-GCM encrypted; restoring needs the master password the backup was made with.</summary>
        EncryptedVault,

        /// <summary>A PlaintextExportFile envelope — credentials in cleartext.</summary>
        PlaintextExport,

        /// <summary>Neither shape; refused with an inline error.</summary>
        Unrecognized
    }
}