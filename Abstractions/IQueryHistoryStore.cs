using System.Collections.Generic;
using DbManager.Models;

namespace DbManager.Abstractions
{
    /// <summary>
    /// Rolling history of executed statements, persisted in the same SQLite file
    /// as the saved-query library (each statement's SQL AES-GCM-encrypted under
    /// the session vault key). Scoped per circuit, like <see cref="IQueryStore"/>:
    /// every method needs the unlocked key.
    /// </summary>
    public interface IQueryHistoryStore
    {
        /// <summary>True while the vault is unlocked (the key needed to read/write is present).</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Records one execution and trims the store to its retention limit.
        /// Never throws — a history write must never break a query run.
        /// </summary>
        void Add(QueryHistoryEntry entry);

        /// <summary>
        /// The most recent entries, newest first. Rows whose SQL cannot be decrypted
        /// (written under a key no longer held) are skipped rather than throwing.
        /// </summary>
        List<QueryHistoryEntry> GetRecent(int limit);

        /// <summary>Empties the history.</summary>
        void Clear();
    }
}
