using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbManager.Models;

namespace DbManager.Abstractions
{
    /// <summary>
    /// Everything one database engine contributes to the app: how to open it,
    /// how to read its schema, and how its SQL dialect differs (identifier
    /// quoting, TOP-N vs LIMIT, GO batches, write-statement keywords).
    /// Adding a new engine (Oracle, Firebird, …) = one class implementing this
    /// interface + one DI registration.
    /// </summary>
    public interface IDbConnector
    {
        /// <summary>Stable key stored in connection profiles ("mssql", "mysql", "postgres", "sqlite").</summary>
        string EngineKey { get; }

        /// <summary>Human-readable name for dropdowns ("SQL Server", "PostgreSQL", …).</summary>
        string DisplayName { get; }

        /// <summary>Default TCP port, or 0 when the engine is file-based (SQLite).</summary>
        int DefaultPort { get; }

        /// <summary>Opens a new connection. The caller owns and disposes it.</summary>
        DbConnection CreateConnection(string connectionString);

        /// <summary>
        /// Opens the connection and runs a trivial query; throws when the
        /// credentials/host are wrong. Used by the "Test connection" button.
        /// </summary>
        Task TestConnectionAsync(string connectionString, CancellationToken ct = default);

        /// <summary>Lists tables, views, procedures and functions of the connected database.</summary>
        Task<List<SchemaObject>> GetSchemaAsync(DbConnection conn, CancellationToken ct = default);

        /// <summary>Whether the dialect has a GO batch separator (T-SQL only).</summary>
        bool SupportsGoBatches { get; }

        /// <summary>Quotes a bare identifier: [n], `n` or "n" (with escaping).</summary>
        string QuoteIdentifier(string name);

        /// <summary>
        /// Quotes a possibly schema-qualified plain name ("myschema.mytable")
        /// by quoting each dot-separated part on its own.
        /// </summary>
        string QuoteQualifiedName(string qualifiedName);

        /// <summary>Builds a "select first N rows" query for the sidebar quick-run.</summary>
        string BuildSelectTopN(string qualifiedName, int n);

        /// <summary>Builds the stub inserted for a procedure (EXEC … / CALL …).</summary>
        string BuildExecStub(string qualifiedName);

        /// <summary>Builds the stub inserted for a function (SELECT …().</summary>
        string BuildFunctionStub(string qualifiedName);

        /// <summary>
        /// Commands that render <paramref name="sql"/>'s execution plan, run IN
        /// ORDER on a single connection. Most engines return one "EXPLAIN <sql>"
        /// command; T-SQL has no EXPLAIN keyword and instead returns a
        /// SET SHOWPLAN_XML ON / statement / OFF sequence, because SHOWPLAN is a
        /// per-session compile setting that must wrap the statement (and makes it
        /// plan without executing). The whole sequence shares ONE connection
        /// precisely so the session setting survives between the commands.
        /// </summary>
        IReadOnlyList<string> BuildExplainCommands(string sql);

        /// <summary>
        /// True when the engine can describe a table well enough for the data
        /// editor to write to it: primary-key columns (to address a row) and
        /// identity/auto-increment columns (so INSERTs skip generated values).
        /// Gates the sidebar's "Data" button; the other engines return false
        /// until their catalog queries have been verified against a live server.
        /// </summary>
        bool SupportsTableEditing { get; }

        /// <summary>
        /// Reads the column metadata the data editor needs for
        /// <paramref name="qualifiedName"/> (as shown in the sidebar: bare, or
        /// "schema.name"). Returns an empty list for engines with
        /// <see cref="SupportsTableEditing"/> == false.
        /// </summary>
        Task<List<TableColumnInfo>> GetTableColumnsAsync(DbConnection conn, string qualifiedName, CancellationToken ct = default);

        /// <summary>Engine-correct SQL literal for one value (used by the results grid's SQL export).</summary>
        string FormatSqlLiteral(object? value);

        /// <summary>Statement that empties a table for the SQL export: TRUNCATE, or DELETE when unsupported.</summary>
        string BuildTruncateStatement(string quotedTable);

        /// <summary>First keywords that mutate data or structure, for the write-gate heuristic.</summary>
        ISet<string> WriteKeywords { get; }

        /// <summary>Whether "SELECT INTO …" creates a table and is therefore a write (T-SQL).</summary>
        bool IsSelectIntoWrite { get; }

        /// <summary>Dialect bits pushed to the Monaco JS (quoting, owner hint, TOP-N snippet).</summary>
        DialectConfig DialectConfig { get; }

        /// <summary>Composes the structured form fields into a full connection string.</summary>
        string BuildConnectionString(ConnectionFormData form);

        /// <summary>True when the engine supports connecting without a pinned
        /// database (server-level profiles and the database picker). False for SQLite.</summary>
        bool SupportsServerLevel { get; }

        /// <summary>
        /// True when queries can span databases (MSSQL three-part names, MySQL
        /// db-qualified tables) and cross-database IntelliSense applies. False for
        /// PostgreSQL (no native cross-database queries) and SQLite.
        /// </summary>
        bool SupportsCrossDatabaseQueries { get; }

        /// <summary>Connection string for server-level operations (listing
        /// databases): no pinned database, or the engine's maintenance database.</summary>
        string BuildServerConnectionString(ConnectionFormData form);

        /// <summary>Lists database names visible to the current user on the server.</summary>
        Task<List<string>> ListDatabasesAsync(DbConnection conn, CancellationToken ct = default);
    }
}