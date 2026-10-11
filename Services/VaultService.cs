using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbManager.Abstractions;
using DbManager.Models;
using Microsoft.Extensions.Configuration;

namespace DbManager.Services
{
    /// <summary>
    /// File + crypto implementation of the connection vault.
    ///
    /// Layout on disk (see VaultFile): a JSON header carrying the KDF parameters
    /// (salt + iteration count, so they can be raised later without breaking old
    /// files) beside a single AES-GCM blob whose plaintext is the serialized
    /// profile list INCLUDING connection strings. One aggregate blob — rather
    /// than per-field encryption — means zero partial-plaintext leakage and one
    /// decrypt per unlock. A wrong master password simply fails GCM tag
    /// verification; the key never leaves memory except into the caller's
    /// (SessionVault, per-circuit) custody.
    /// </summary>
    public class VaultService : IVaultService
    {
        private const int SaltSize = 16;
        private const int KeySize = 32;
        private const int NonceSize = 12;
        private const int TagSize = 16;

        private static readonly JsonSerializerOptions ProfileJsonOptions = new()
        {
            // Deterministic enough for round-tripping; profiles are our own model.
            WriteIndented = false
        };

        private readonly string _vaultPath;

        public VaultService(IConfiguration configuration)
        {
            var configured = configuration["ConnectionStore:Path"] ?? "~/.db-manager/connections.json";
            _vaultPath = ExpandPath(configured);
        }

        public bool VaultExists() => File.Exists(_vaultPath);

        public void CreateVault(string masterPassword, List<ConnectionProfile> profiles)
        {
            var file = new VaultFile
            {
                Kdf = NewKdfParams()
            };
            var key = DeriveKey(masterPassword, file.Kdf);
            file.Vault = Encrypt(key, Serialize(profiles));

            Directory.CreateDirectory(Path.GetDirectoryName(_vaultPath)!);
            WriteFile(file);
        }

        public bool TryUnlock(string masterPassword, out byte[] key, out List<ConnectionProfile> profiles, out string error)
        {
            key = [];
            profiles = [];
            error = string.Empty;

            try
            {
                (key, profiles) = UnlockFile(ReadFile(), masterPassword);
                return true;
            }
            catch (CryptographicException)
            {
                // AES-GCM tag mismatch: wrong master password or a tampered file.
                error = "Wrong master password.";
                return false;
            }
            catch (JsonException)
            {
                error = "The vault file is corrupted.";
                return false;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        public string ReadVaultFileText() => File.ReadAllText(_vaultPath);

        public bool TryUnlockJson(string json, string masterPassword, out List<ConnectionProfile> profiles, out string error)
        {
            profiles = [];
            error = string.Empty;

            try
            {
                // The key against the uploaded KDF params is useless once the
                // file is replaced and re-unlocked from disk, so it is dropped.
                profiles = UnlockFile(ParseVaultFile(json), masterPassword).Profiles;
                return true;
            }
            catch (CryptographicException)
            {
                error = "Wrong master password.";
                return false;
            }
            catch (JsonException)
            {
                error = "The backup file is corrupted.";
                return false;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        public void ReplaceVault(string json)
        {
            // Refuse to write anything we could not read back.
            ParseVaultFile(json);
            Directory.CreateDirectory(Path.GetDirectoryName(_vaultPath)!);
            // Atomic: restore is destructive, so a half-written file must never exist.
            var temp = _vaultPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _vaultPath, overwrite: true);
        }

        public void SaveProfiles(byte[] key, List<ConnectionProfile> profiles)
        {
            var file = ReadFile();
            file.Vault = Encrypt(key, Serialize(profiles));
            WriteFile(file);
        }

        public byte[] ChangeMasterPassword(string newPassword, List<ConnectionProfile> profiles)
        {
            // Fresh salt + params, then re-encrypt the plaintext under the new key.
            var file = new VaultFile { Kdf = NewKdfParams() };
            var key = DeriveKey(newPassword, file.Kdf);
            file.Vault = Encrypt(key, Serialize(profiles));
            WriteFile(file);
            return key;
        }

        // --- crypto core (shared by on-disk and uploaded vault JSON) -----------

        /// <summary>
        /// Derives the key against the file's KDF params and decrypts the blob.
        /// Throws (CryptographicException on wrong password, JsonException on a
        /// corrupted payload); the public Try* methods map that to messages.
        /// </summary>
        private static (byte[] Key, List<ConnectionProfile> Profiles) UnlockFile(VaultFile file, string masterPassword)
        {
            var candidateKey = DeriveKey(masterPassword, file.Kdf);
            var plaintext = Decrypt(candidateKey, file.Vault);
            return (candidateKey, Deserialize(plaintext));
        }

        // --- crypto primitives -------------------------------------------------

        private static KdfParams NewKdfParams() => new()
        {
            Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltSize))
        };

        private static byte[] DeriveKey(string masterPassword, KdfParams kdf)
        {
            // PBKDF2-SHA512 at the OWASP-recommended iteration floor. The
            // parameters are stored in the file so they can be raised later.
            var salt = Convert.FromBase64String(kdf.Salt);
            return Rfc2898DeriveBytes.Pbkdf2(masterPassword, salt, kdf.Iterations, HashAlgorithmName.SHA512, KeySize);
        }

        private static VaultBlob Encrypt(byte[] key, string plaintext)
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var cipher = new byte[plainBytes.Length];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plainBytes, cipher, tag, ReadOnlySpan<byte>.Empty);
            return new VaultBlob
            {
                Nonce = Convert.ToBase64String(nonce),
                // One blob: tag first, then ciphertext — matches the layout Decrypt splits.
                Ciphertext = Convert.ToBase64String([.. tag, .. cipher])
            };
        }

        private static string Decrypt(byte[] key, VaultBlob blob)
        {
            var nonce = Convert.FromBase64String(blob.Nonce);
            var all = Convert.FromBase64String(blob.Ciphertext);
            var tag = all.AsSpan(0, TagSize);
            var cipher = all.AsSpan(TagSize);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, TagSize);
            // Throws CryptographicException on tag mismatch (wrong master password).
            aes.Decrypt(nonce, cipher, tag, plain, ReadOnlySpan<byte>.Empty);
            return Encoding.UTF8.GetString(plain);
        }

        // --- serialization -------------------------------------------------------

        private static string Serialize(List<ConnectionProfile> profiles) =>
            JsonSerializer.Serialize(profiles, ProfileJsonOptions);

        private static List<ConnectionProfile> Deserialize(string json) =>
            JsonSerializer.Deserialize<List<ConnectionProfile>>(json, ProfileJsonOptions) ?? [];

        private VaultFile ReadFile() => ParseVaultFile(File.ReadAllText(_vaultPath));

        /// <summary>Deserializes and shape-checks vault-file JSON (on-disk or uploaded).</summary>
        private static VaultFile ParseVaultFile(string json)
        {
            var file = JsonSerializer.Deserialize<VaultFile>(json);
            if (file is null || string.IsNullOrEmpty(file.Vault.Ciphertext) || string.IsNullOrEmpty(file.Kdf.Salt))
                throw new JsonException("Vault file is missing required sections.");
            return file;
        }

        private void WriteFile(VaultFile file) =>
            File.WriteAllText(_vaultPath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));

        /// <summary>Expands a leading ~/ to the user's home directory.</summary>
        private static string ExpandPath(string path)
        {
            if (!path.StartsWith("~/", StringComparison.Ordinal))
                return path;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path[2..]);
        }
    }
}