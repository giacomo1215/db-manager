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

        /// <summary>Full connection string, credentials included. Secret at rest.</summary>
        public string ConnectionString { get; set; } = string.Empty;

        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
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

        /// <summary>When true, RawConnectionString is used as-is.</summary>
        public bool UseRaw { get; set; }

        public string RawConnectionString { get; set; } = string.Empty;
    }
}