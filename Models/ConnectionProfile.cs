namespace DbManager.Models
{
    /// <summary>
    /// A saved database connection. Everything needed to reconnect — including
    /// the full connection string with credentials — lives in this object, and the
    /// whole profile list is stored AES-GCM-encrypted under the master password
    /// (see VaultService). Nothing about a profile ever reaches disk in plaintext.
    /// </summary>
    public class ConnectionProfile
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Display name chosen by the user ("Prod reporting", "Local sqlite", …).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Connector engine key: "mssql", "mysql", "postgres", "sqlite".</summary>
        public string EngineKey { get; set; } = string.Empty;

        /// <summary>Full connection string, credentials included. Secret at rest.
        /// For server-level profiles this always targets the currently selected database.</summary>
        public string ConnectionString { get; set; } = string.Empty;

        /// <summary>True when the profile connects to the whole server rather
        /// than one pinned database (SSMS-style, switchable in the editor).</summary>
        public bool IsServerLevel { get; set; }

        /// <summary>Database queries currently run against (server-level profiles).</summary>
        public string SelectedDatabase { get; set; } = string.Empty;

        /// <summary>Structured fields captured at creation so the connection
        /// string can be rebuilt for any database without decomposing it.</summary>
        public string Host { get; set; } = string.Empty;

        public int? Port { get; set; }

        public string Username { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        /// <summary>SQL Server transport encryption mode (a
        /// <see cref="SqlServerEncryption"/> key); ignored by the other engines.
        /// Persisted so the connection string can be rebuilt when a server-level
        /// profile switches database — that rebuild goes through ToFormData and
        /// would otherwise lose the user's encryption choice.</summary>
        public string EncryptMode { get; set; } = SqlServerEncryption.Default;

        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Form fields reconstructed from the stored structured fields,
        /// optionally overriding the target database.</summary>
        public ConnectionFormData ToFormData(string? databaseOverride = null) => new()
        {
            EngineKey = EngineKey,
            Host = Host,
            Port = Port,
            Username = Username,
            Password = Password,
            Database = databaseOverride ?? SelectedDatabase,
            IsServerLevel = IsServerLevel,
            EncryptMode = EncryptMode
        };
    }

    /// <summary>
    /// SQL Server transport encryption choices, mapped to Microsoft.Data.SqlClient
    /// connection-string keywords. Shared by the connection form (dropdown), the
    /// persisted profile and the connector so the keys stay in one place.
    ///
    /// Why this exists: the old connector hard-coded Encrypt=True with a trusted
    /// self-signed certificate. That is right for internal boxes with self-signed
    /// certs but wrong on both ends of the spectrum — legacy servers that don't
    /// speak TLS at all, and hardened/cloud servers that require the certificate
    /// chain to be validated (or a strict TDS 8.0 handshake). Those were exactly
    /// the "inaccessible" databases.
    /// </summary>
    public static class SqlServerEncryption
    {
        /// <summary>No TLS: Encrypt=False. For old servers that reject the handshake.</summary>
        public const string Off = "off";

        /// <summary>Encrypt=True, TrustServerCertificate=True: accepts a self-signed
        /// cert. The long-standing default, kept as such so existing profiles and
        /// the structured form behave exactly as before.</summary>
        public const string TrustServerCertificate = "trust";

        /// <summary>Encrypt=True, TrustServerCertificate=False: validate the server
        /// certificate against the machine's trust store (name + chain).</summary>
        public const string VerifyCertificate = "verify";

        /// <summary>Encrypt=Strict: TDS 8.0, encryption negotiated before login.</summary>
        public const string Strict = "strict";

        public const string Default = TrustServerCertificate;

        /// <summary>Connection-string segment for a mode key; unknown/empty falls
        /// back to the default (so profiles saved before this field existed, whose
        /// value deserializes to the default, still connect).</summary>
        public static string ToConnectionStringSegments(string? mode) => mode switch
        {
            Off => "Encrypt=False",
            VerifyCertificate => "Encrypt=True;TrustServerCertificate=False",
            Strict => "Encrypt=Strict",
            _ => "Encrypt=True;TrustServerCertificate=True"
        };
    }

    /// <summary>
    /// Form fields for the "add connection" dialog. The structured fields are
    /// composed into a connection string by the engine's connector; the user can
    /// alternatively type a raw connection string directly (UseRaw).
    /// </summary>
    public class ConnectionFormData
    {
        public string EngineKey { get; set; } = "sqlite";

        public string Host { get; set; } = string.Empty;

        public int? Port { get; set; }

        public string Database { get; set; } = string.Empty;

        public string Username { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        /// <summary>Database file path (SQLite only).</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>SQL Server transport encryption mode (<see cref="SqlServerEncryption"/>);
        /// ignored by the other engines.</summary>
        public string EncryptMode { get; set; } = SqlServerEncryption.Default;

        /// <summary>When true, RawConnectionString is used as-is.</summary>
        public bool UseRaw { get; set; }

        /// <summary>When true, the connection targets the whole server and the
        /// database can be picked/switched later (SSMS-style).</summary>
        public bool IsServerLevel { get; set; }

        public string RawConnectionString { get; set; } = string.Empty;
    }
}