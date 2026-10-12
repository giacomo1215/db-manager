using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DbManager.Models;

namespace DbManager.Abstractions
{
    /// <summary>
    /// Per-circuit state of the SQL Editor: which profile is active and its
    /// cached schema. Scoped — each browser tab is its own circuit with its own
    /// active connection, which is the natural model for a database client.
    /// </summary>
    public interface ISqlEditorViewModel
    {
        /// <summary>The connection queries run against, or null before one is picked.</summary>
        ConnectionProfile? ActiveProfile { get; }

        /// <summary>Schema of the active profile, empty before LoadSchemaAsync completes.</summary>
        List<SchemaObject> Schema { get; }

        bool SchemaLoaded { get; }

        /// <summary>
        /// Connection error from the last LoadSchemaAsync (unreachable host, wrong
        /// credentials, timeout). The editor still works without a schema — the
        /// page shows this inline instead of dying, same contract as ExecuteAsync.
        /// </summary>
        string? SchemaError { get; }

        /// <summary>
        /// Loads (and caches) the schema of the given profile. Passing the profile
        /// that is already active is a no-op; passing a different one switches and
        /// invalidates the cache first. NEVER throws — failures land in SchemaError.
        /// </summary>
        Task LoadSchemaAsync(ConnectionProfile profile, CancellationToken ct = default);

        /// <summary>
        /// Reloads the active profile's schema even when already cached — used after
        /// a database switch inside a server-level profile (same profile id, new
        /// target). NEVER throws — failures land in SchemaError.
        /// </summary>
        Task ReloadSchemaAsync(CancellationToken ct = default);

        /// <summary>Executes against the active profile.</summary>
        Task<SqlEditorResult> ExecuteAsync(string sql, CancellationToken ct = default);

        /// <summary>Write-gate heuristic, against the active profile's dialect.</summary>
        bool IsWriteStatement(string sql);
    }
}