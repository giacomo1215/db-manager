using System;
using System.Security.Cryptography;
using System.Text;
using DbManager.Models;

namespace DbManager.Services
{
    /// <summary>
    /// AES-GCM + PBKDF2-SHA512 primitives shared by everything that protects
    /// data under the master password: the connection vault (VaultService) and
    /// the saved-query library backup (QueryBackupService). The blob layout is
    /// "16-byte tag followed by ciphertext" — one opaque base64 field — so a
    /// wrong password simply fails the GCM tag check.
    /// </summary>
    internal static class VaultCrypto
    {
        private const int SaltSize = 16;
        private const int KeySize = 32;
        private const int NonceSize = 12;
        private const int TagSize = 16;

        /// <summary>Fresh KDF params: a random salt at the OWASP iteration floor.</summary>
        public static KdfParams NewKdfParams() => new()
        {
            Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltSize))
        };

        /// <summary>
        /// Derives the AES-256 key from the master password against the given KDF
        /// params (PBKDF2-SHA512). The params are stored beside the blob so the
        /// iteration count can be raised later without invalidating old files.
        /// </summary>
        public static byte[] DeriveKey(string masterPassword, KdfParams kdf)
        {
            var salt = Convert.FromBase64String(kdf.Salt);
            return Rfc2898DeriveBytes.Pbkdf2(masterPassword, salt, kdf.Iterations, HashAlgorithmName.SHA512, KeySize);
        }

        /// <summary>Encrypts the plaintext under the key into an AES-GCM blob.</summary>
        public static VaultBlob Encrypt(byte[] key, string plaintext)
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

        /// <summary>
        /// Decrypts a blob under the key. Throws CryptographicException on a tag
        /// mismatch (wrong key / tampered data) — callers map that to a message.
        /// </summary>
        public static string Decrypt(byte[] key, VaultBlob blob)
        {
            var nonce = Convert.FromBase64String(blob.Nonce);
            var all = Convert.FromBase64String(blob.Ciphertext);
            var tag = all.AsSpan(0, TagSize);
            var cipher = all.AsSpan(TagSize);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, ReadOnlySpan<byte>.Empty);
            return Encoding.UTF8.GetString(plain);
        }
    }
}
