using System.Collections.Generic;
using DbManager.Models;

namespace DbManager.Abstractions
{
    /// <summary>
    /// Stateless file + crypto facade for the connection vault. The derived key
    /// lives in the caller (SessionVault) for the circuit's lifetime only; this
    /// service touches the key only long enough to encrypt/decrypt.
    /// </summary>
    public interface IVaultService
    {
        /// <summary>Whether the vault file exists on disk (first run: it does not).</summary>
        bool VaultExists();

        /// <summary>Creates the vault file, encrypting an (initially empty) profile list.</summary>
        void CreateVault(string masterPassword, List<ConnectionProfile> profiles);

        /// <summary>
        /// Attempts to decrypt the vault. Returns false (wrong password, tampered
        /// or unreadable file) with a message suitable for the unlock form.
        /// </summary>
        bool TryUnlock(string masterPassword, out byte[] key, out List<ConnectionProfile> profiles, out string error);

        /// <summary>Persists the profile list, re-encrypting with the given (already derived) key.</summary>
        void SaveProfiles(byte[] key, List<ConnectionProfile> profiles);

        /// <summary>
        /// Re-encrypts the profiles under a new master password (fresh salt) and
        /// returns the new derived key.
        /// </summary>
        byte[] ChangeMasterPassword(string newPassword, List<ConnectionProfile> profiles);

        /// <summary>The raw on-disk vault file JSON, verbatim (the encrypted export download).</summary>
        string ReadVaultFileText();

        /// <summary>
        /// Attempts to decrypt an arbitrary vault-file JSON — an uploaded backup —
        /// with the given master password, WITHOUT touching the configured vault
        /// path. Same failure semantics as TryUnlock.
        /// </summary>
        bool TryUnlockJson(string json, string masterPassword, out List<ConnectionProfile> profiles, out string error);

        /// <summary>
        /// Atomically replaces the on-disk vault file with the given
        /// (caller-validated via TryUnlockJson) vault-file JSON. The master
        /// password becomes whatever the backup was made with.
        /// </summary>
        void ReplaceVault(string json);
    }
}