using System.Threading;
using System.Threading.Tasks;
using DbManager.Models;

namespace DbManager.Abstractions
{
    /// <summary>
    /// Loads one table's rows together with the column metadata the data editor
    /// needs (primary key, identity, nullability). Only engines with
    /// <see cref="IDbConnector.SupportsTableEditing"/> can be loaded; every other
    /// engine — and every failure — comes back as a <see cref="TableDataResult"/>
    /// carrying an Error, never thrown (the never-throw UI contract).
    /// </summary>
    public interface ITableDataService
    {
        /// <summary>
        /// Reads up to <paramref name="limit"/> rows of <paramref name="qualifiedName"/>
        /// plus its column metadata. Callers must check the result's Error before
        /// using Columns/Rows.
        /// </summary>
        Task<TableDataResult> LoadAsync(ConnectionProfile profile, string qualifiedName, int limit, CancellationToken ct = default);

        /// <summary>
        /// Runs the editor's generated INSERT/UPDATE/DELETE script inside ONE
        /// transaction (all-or-nothing: a mid-script failure leaves the table
        /// untouched) and commits only when at least <paramref name="expectedChanges"/>
        /// rows were affected. Each grid change is one single-row statement, so a
        /// shortfall means a row was deleted or its key changed on the server since
        /// the grid loaded — the whole run is rolled back and an Error returned,
        /// instead of silently applying the rest. Never throws; failures ride in
        /// <see cref="SqlEditorResult.Error"/>.
        /// </summary>
        Task<SqlEditorResult> ApplyAsync(ConnectionProfile profile, string script, int expectedChanges, CancellationToken ct = default);
    }
}
