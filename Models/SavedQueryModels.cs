using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DbManager.Models
{
    /// <summary>
    /// One query in the saved-query library. <see cref="Sql"/> is the plaintext
    /// in memory; at rest it is stored AES-GCM-encrypted per row in the SQLite
    /// library (see QueryStore) under the session vault key, so only the name and
    /// the connection tag are readable on disk. <see cref="ProfileId"/> records the
    /// connection the query was written against (nullable: the library is global,
    /// a query need not belong to one connection).
    /// </summary>
    public class SavedQuery
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Name { get; set; } = string.Empty;

        /// <summary>Plaintext SQL in memory; encrypted at rest.</summary>
        public string Sql { get; set; } = string.Empty;

        /// <summary>Connection the query was saved from, or null.</summary>
        public Guid? ProfileId { get; set; }

        /// <summary>Engine key of the originating connection ("mssql", "sqlite", …), for the tag.</summary>
        public string EngineKey { get; set; } = string.Empty;

        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

        public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Envelope for the plaintext query-library export. Query SQL is in cleartext
    /// here, so the UI warns before exporting. "Kind" is a provenance marker; the
    /// format sniffer keys off the "Queries" array instead, so a hand-edited file
    /// without the marker still sniffs correctly.
    /// </summary>
    public class QueryExportFile
    {
        public const string KindMarker = "dbm-queries-plaintext";
        public const int CurrentVersion = 1;

        public string? Kind { get; set; }

        public int Version { get; set; } = CurrentVersion;

        public DateTimeOffset ExportedUtc { get; set; } = DateTimeOffset.UtcNow;

        public List<SavedQuery> Queries { get; set; } = [];
    }

    /// <summary>
    /// Encrypted query-library backup. Same AES-GCM + KDF-params shape as
    /// VaultFile, but with a distinct <see cref="Kind"/> so the format sniffer
    /// never confuses it with the connection vault. The KDF params are embedded
    /// (copied from the vault at export time) so the blob is self-contained: any
    /// machine can restore it from just the master password it was made under.
    /// </summary>
    public class QueryBackupFile
    {
        public const string KindMarker = "dbm-queries-encrypted";
        public const int CurrentVersion = 1;

        public string Kind { get; set; } = KindMarker;

        public int Version { get; set; } = CurrentVersion;

        public KdfParams Kdf { get; set; } = new();

        public VaultBlob Vault { get; set; } = new();
    }

    /// <summary>Query-library backup format sniffed from the JSON root properties.</summary>
    public enum QueryBackupFormat
    {
        /// <summary>A QueryBackupFile (Kind = "dbm-queries-encrypted") — AES-GCM; restoring needs the master password the backup was made with.</summary>
        EncryptedQueries,

        /// <summary>A QueryExportFile envelope — query SQL in cleartext.</summary>
        PlaintextExport,

        /// <summary>Neither shape; refused with an inline error.</summary>
        Unrecognized
    }
}
