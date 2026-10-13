using System;
using System.Collections.Generic;
using System.Linq;
using DbManager.Abstractions;
using DbManager.Models;

namespace DbManager.Services
{
    /// <summary>
    /// Scoped (per circuit) unlocked state. The derived AES key and the decrypted
    /// profiles live here for the circuit's lifetime only; Dispose zeroes the
    /// key so a torn-down circuit leaves nothing behind. The navbar Lock button
    /// calls Lock() directly.
    /// </summary>
    public class SessionVault : ISessionVault
    {
        private readonly IVaultService _vaultService;

        public SessionVault(IVaultService vaultService) => _vaultService = vaultService;

        private byte[]? _key;
        private List<ConnectionProfile> _profiles = [];

        public bool IsUnlocked => _key is not null;

        public byte[]? DerivedKey => _key;

        public List<ConnectionProfile> Profiles => _profiles;

        public ConnectionProfile? GetProfile(Guid id) =>
            _profiles.FirstOrDefault(p => p.Id == id);

        public void Unlock(byte[] key, List<ConnectionProfile> profiles)
        {
            _key = key;
            _profiles = profiles;
        }

        public void SaveProfiles(List<ConnectionProfile> profiles)
        {
            if (_key is null)
                throw new InvalidOperationException("The vault is locked.");

            _vaultService.SaveProfiles(_key, profiles);
            _profiles = profiles;
        }

        public void Lock()
        {
            if (_key is not null)
                Array.Clear(_key);
            _key = null;
            _profiles = [];
        }

        public void Dispose()
        {
            Lock();
            GC.SuppressFinalize(this);
        }
    }
}