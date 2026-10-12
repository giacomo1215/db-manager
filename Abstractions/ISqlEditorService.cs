using System.Threading;
using System.Threading.Tasks;
using DbManager.Models;

namespace DbManager.Abstractions
{
    /// <summary>
    /// Executes arbitrary SQL against a saved connection and reads its schema.
    /// Errors are ALWAYS carried in <see cref="SqlEditorResult.Error"/> and never
    /// thrown — an unhandled exception in a Blazor Server event handler would tear
    /// down the whole circuit.
    /// </summary>
    public interface ISqlEditorService
    {
        /// <summary>Lists the objects of the profile's database, via its engine connector.</summary>
        Task<List<SchemaObject>> GetSchemaAsync(ConnectionProfile profile, CancellationToken ct = default);

        /// <summary>Lists databases visible on the server of a server-level profile.</summary>
        Task<List<string>> ListDatabasesAsync(ConnectionProfile profile, CancellationToken ct = default);

        /// <summary>
        /// Eagerly loads the schema of EVERY database on the profile's server
        /// (server-level mssql/mysql profiles) for cross-database IntelliSense.
        /// Databases the login cannot read are skipped, never thrown for.
        /// Throws only when the server itself is unreachable.
        /// </summary>
        Task<List<DatabaseSchema>> GetServerSchemaAsync(ConnectionProfile profile, CancellationToken ct = default);

        /// <summary>Executes the script (possibly multiple batches) against the profile.</summary>
        Task<SqlEditorResult> ExecuteAsync(ConnectionProfile profile, string sql, CancellationToken ct = default);

        /// <summary>
        /// Heuristic: does the script mutate data or structure? Only decides whether
        /// the confirm dialog appears; execution is identical either way.
        /// </summary>
        bool IsWriteStatement(ConnectionProfile profile, string sql);
    }
}