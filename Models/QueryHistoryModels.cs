using System;

namespace DbManager.Models
{
    /// <summary>
    /// One executed statement, kept in the rolling query history. The SQL text is
    /// the plaintext in memory; at rest it is AES-GCM-encrypted under the session
    /// vault key (same treatment as the saved-query library) so the history is
    /// only readable while the vault is unlocked. Everything else here is a
    /// non-secret run summary the list can render without decrypting.
    /// </summary>
    public class QueryHistoryEntry
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Plaintext SQL in memory; encrypted at rest.</summary>
        public string Sql { get; set; } = string.Empty;

        /// <summary>Connection the statement was run against, or null.</summary>
        public Guid? ProfileId { get; set; }

        /// <summary>Connection name at run time (plaintext, for the list).</summary>
        public string ProfileName { get; set; } = string.Empty;

        /// <summary>Engine key of that connection ("mssql", "sqlite", …).</summary>
        public string EngineKey { get; set; } = string.Empty;

        public DateTimeOffset ExecutedUtc { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Server execution time in milliseconds (the result footer's value).</summary>
        public long ElapsedMs { get; set; }

        /// <summary>Rows returned by the first result set, or rows affected by a DML statement.</summary>
        public int RowCount { get; set; }

        /// <summary>False when the statement failed (the run still happened and is worth keeping).</summary>
        public bool Succeeded { get; set; }

        /// <summary>Error message when the run failed, or null.</summary>
        public string? Error { get; set; }
    }
}
