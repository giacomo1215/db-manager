using System.Text.Json.Serialization;

namespace DbManager.Models
{
    /// <summary>
    /// On-disk shape of the connection vault. The whole profile list (including
    /// connection strings with credentials) is serialized to JSON and stored as a
    /// single AES-GCM blob — one decrypt per unlock, zero partial-plaintext leakage.
    /// The KDF parameters live beside the blob so they can be raised later without
    /// invalidating old files.
    /// </summary>
    public class VaultFile
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        public KdfParams Kdf { get; set; } = new();

        public VaultBlob Vault { get; set; } = new();
    }

    public class KdfParams
    {
        /// <summary>Key derivation id, e.g. "PBKDF2-SHA512".</summary>
        public string Algo { get; set; } = "PBKDF2-SHA512";

        /// <summary>PBKDF2 iteration count. OWASP 2023+ guidance: ≥210,000 for SHA-512.</summary>
        public int Iterations { get; set; } = 210_000;

        /// <summary>Base64 PBKDF2 salt (16 bytes).</summary>
        public string Salt { get; set; } = string.Empty;
    }

    public class VaultBlob
    {
        /// <summary>Base64 AES-GCM nonce (12 bytes).</summary>
        public string Nonce { get; set; } = string.Empty;

        /// <summary>Base64 AES-GCM blob: 16-byte authentication tag followed by ciphertext.</summary>
        public string Ciphertext { get; set; } = string.Empty;
    }

    /// <summary>
    /// Per-dialect payload pushed to the Monaco JS alongside the schema (sqlIdent
    /// quoting, owner completion, TOP-N snippet all read from it).
    /// Serialized camelCase: the JS expects { quote, ownerHint, topSnippet }.
    /// </summary>
    public class DialectConfig
    {
        /// <summary>Identifier quote char: '[', '`' or '"'.</summary>
        public string Quote { get; set; } = "[";

        /// <summary>
        /// Owner prefix that alone lists all schema objects in completions
        /// ("dbo." in SSMS), or null when the engine has no such convention.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OwnerHint { get; set; }

        /// <summary>Snippet text for the "top-N rows" quick action.</summary>
        public string TopSnippet { get; set; } = "SELECT TOP 20 *\nFROM ";
    }
}