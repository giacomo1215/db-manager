using System;
using System.Collections.Generic;
using DbManager.Models;

namespace DbManager.Abstractions
{
    /// <summary>
    /// Scoped per circuit: the unlocked state of the connection vault. Holds the
    /// decrypted profiles plus the derived AES key in memory for the circuit's
    /// lifetime only. The navbar Lock button clears it; circuit teardown
    /// (closing the tab) disposes it and zeroes the key.
    /// Note: each browser tab is its own circuit, so each tab unlocks separately.
    /// </summary>
    public interface ISessionVault : IDisposable
    {
        bool IsUnlocked { get; }

        /// <summary>Decrypted profiles. Empty while locked.</summary>
        List<ConnectionProfile> Profiles { get; }

        /// <summary>Profile lookup, or null when missing or locked.</summary>
        ConnectionProfile? GetProfile(Guid id);

        /// <summary>Unlocks the circuit after a successful IVaultService.TryUnlock.</summary>
        void Unlock(byte[] key, List<ConnectionProfile> profiles);

        /// <summary>Replaces the profile list and persists it to disk immediately.</summary>
        void SaveProfiles(List<ConnectionProfile> profiles);

        /// <summary>Clears the key and profiles from memory; pages redirect to the unlock form.</summary>
        void Lock();
    }
}