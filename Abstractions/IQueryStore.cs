using System;
using System.Collections.Generic;
using DbManager.Models;

namespace DbManager.Abstractions
{
    /// <summary>
    /// The saved-query library, persisted as a small SQLite file with each query's
    /// SQL text AES-GCM-encrypted under the session vault key (names/tags stay
    /// plaintext for fast listing). Scoped per circuit — the key lives only for
    /// the circuit's lifetime — so every method requires an unlocked vault.
    /// </summary>
    public interface IQueryStore
    {
        /// <summary>True while the vault is unlocked (the key needed to read/write is present).</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// All saved queries, decrypted, ordered by name. A row whose SQL cannot be
        /// decrypted (e.g. written under a key no longer held) is skipped rather
        /// than throwing — callers render what is readable.
        /// </summary>
        List<SavedQuery> GetAll();

        /// <summary>Saves a new query and returns it (with its assigned id).</summary>
        SavedQuery Add(string name, string sql, Guid? profileId, string engineKey);

        /// <summary>Renames an existing query.</summary>
        void Rename(Guid id, string name);

        /// <summary>Removes a query.</summary>
        void Delete(Guid id);

        /// <summary>
        /// Re-encrypts every stored query from the old key to the new one. Used
        /// when the master password (hence the vault key) changes, so the library
        /// survives.
        /// </summary>
        void Reencrypt(byte[] oldKey, byte[] newKey);

        /// <summary>Replaces the whole library with the given queries (used by restore).</summary>
        void ReplaceAll(IEnumerable<SavedQuery> queries);
    }
}
