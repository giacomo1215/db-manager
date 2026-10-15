using System;
using System.Threading;
using System.Threading.Tasks;
using DbManager.Models;

namespace DbManager.Abstractions
{
    /// <summary>
    /// Scoped, interactive transaction: a single connection kept open across UI
    /// actions so a script can be run inside an explicit transaction and then
    /// committed or rolled back by the user (SSMS-style BEGIN TRAN editing). One
    /// session per circuit — the open connection lives as long as the browser tab.
    /// Every method follows the executor's never-throw contract: failures are
    /// carried in <see cref="SqlEditorResult.Error"/>.
    /// </summary>
    public interface ISqlTransactionSession : IDisposable
    {
        /// <summary>True while a transaction is open (Commit/Rollback are meaningful).</summary>
        bool IsActive { get; }

        /// <summary>The profile the open transaction belongs to, or null.</summary>
        Guid? ActiveProfileId { get; }

        /// <summary>
        /// Rolls back any open transaction, then opens a new one on the profile and
        /// runs the script inside it WITHOUT committing — the caller's results pane
        /// shows the query output while Commit/Rollback await the user's decision.
        /// </summary>
        Task<SqlEditorResult> BeginAndExecuteAsync(ConnectionProfile profile, string sql, CancellationToken ct = default);

        /// <summary>Commits the open transaction. No-op result when none is open.</summary>
        Task<SqlEditorResult> CommitAsync(CancellationToken ct = default);

        /// <summary>Rolls back the open transaction. No-op result when none is open.</summary>
        Task<SqlEditorResult> RollbackAsync(CancellationToken ct = default);

        /// <summary>
        /// Rolls back and disposes the open transaction quietly (profile/database
        /// switch, page teardown). Never throws; used to keep no transaction
        /// dangling across a connection change.
        /// </summary>
        Task ResetAsync();
    }
}
