using System.Collections.Generic;
using DbManager.Models;

namespace DbManager.Abstractions
{
    /// <summary>
    /// Stateless format facade for saved-query backup/restore, mirroring
    /// IBackupService for connections: sniffing uploaded files, building the
    /// plaintext envelope, and encrypting/decrypting the self-contained encrypted
    /// copy. AES-GCM + PBKDF2 primitives live in VaultCrypto; this class is pure
    /// format work.
    /// </summary>
    public interface IQueryBackupService
    {
        /// <summary>Builds the plaintext envelope (query SQL in cleartext).</summary>
        string BuildPlaintextExport(List<SavedQuery> queries);

        /// <summary>
        /// Builds the encrypted copy: the query-list JSON is AES-GCM-encrypted
        /// under <paramref name="key"/> (the session vault key), with
        /// <paramref name="kdf"/> embedded so the master password alone re-derives
        /// the key on restore.
        /// </summary>
        string BuildEncryptedExport(List<SavedQuery> queries, byte[] key, KdfParams kdf);

        /// <summary>Sniffs the backup format from the JSON root properties. Never throws.</summary>
        QueryBackupFormat DetectFormat(string json);

        /// <summary>Parses and validates a plaintext export envelope.</summary>
        bool TryReadPlaintextExport(string json, out List<SavedQuery> queries, out string error);

        /// <summary>
        /// Decrypts an encrypted copy using the master password against its
        /// embedded KDF params. Never throws; failures land in <paramref name="error"/>.
        /// </summary>
        bool TryReadEncryptedExport(string json, string masterPassword, out List<SavedQuery> queries, out string error);
    }
}
