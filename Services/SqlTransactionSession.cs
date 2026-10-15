using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbManager.Abstractions;
using DbManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DbManager.Services
{
    /// <summary>
    /// Interactive transaction session. Unlike <see cref="SqlEditorService"/>, which
    /// opens (and closes) a connection per GO batch, this holds ONE connection and
    /// its DbTransaction open between UI actions: BeginAndExecute runs the script
    /// inside the transaction and returns, leaving the transaction pending until the
    /// user commits or rolls it back. Scoped per circuit; Dispose rolls back.
    /// </summary>
    public class SqlTransactionSession : ISqlTransactionSession
    {
        private readonly IDbConnectorRegistry _registry;
        private readonly int _commandTimeout;
        private readonly ILogger<SqlTransactionSession> _logger;

        private DbConnection? _conn;
        private DbTransaction? _tx;
        private Guid? _profileId;

        public SqlTransactionSession(IDbConnectorRegistry registry, IConfiguration configuration, ILogger<SqlTransactionSession> logger)
        {
            _registry = registry;
            _commandTimeout = configuration.GetValue("SqlEditor:CommandTimeoutSeconds", 60);
            _logger = logger;
        }

        public bool IsActive => _tx is not null;

        public Guid? ActiveProfileId => _profileId;

        public async Task<SqlEditorResult> BeginAndExecuteAsync(ConnectionProfile profile, string sql, CancellationToken ct = default)
        {
            // A transaction can only belong to one connection; a new run supersedes
            // any pending one (rolled back, never silently committed).
            await ResetAsync();

            var result = new SqlEditorResult();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var connector = ResolveConnector(profile);

            try
            {
                _conn = connector.CreateConnection(profile.ConnectionString);
                await _conn.OpenAsync(ct);
                _tx = await _conn.BeginTransactionAsync(ct);
                _profileId = profile.Id;

                var batches = SqlExecutionHelpers.SplitOnGo(sql, connector)
                    .Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
                if (batches.Count == 0)
                {
                    result.Error = "Empty query.";
                    return result;
                }

                foreach (var batch in batches)
                {
                    await using var cmd = _conn.CreateCommand();
                    cmd.CommandText = batch;
                    cmd.CommandTimeout = _commandTimeout;
                    // Enlist every command in the pending transaction.
                    cmd.Transaction = _tx;

                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (true)
                    {
                        if (reader.FieldCount > 0)
                            result.ResultSets.Add(await SqlExecutionHelpers.ReadCurrentTableAsync(reader, ct));
                        if (!await reader.NextResultAsync(ct))
                            break;
                    }
                    if (!reader.IsClosed)
                        await reader.CloseAsync();
                    if (reader.RecordsAffected > 0)
                        result.RowsAffected += reader.RecordsAffected;
                }
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                result.Error = "Query cancelled.";
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                // The transaction stays open on a statement error: with PostgreSQL an
                // error aborts the transaction, so the user must ROLLBACK — surfaced
                // as an inline error, not an automatic rollback that hides the cause.
                _logger.LogError(ex, "Transaction statement failed on {Engine} profile '{Profile}'.", profile.EngineKey, profile.Name);
                result.Error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Transaction statement failed (unexpected) on {Engine} profile '{Profile}'.", profile.EngineKey, profile.Name);
                result.Error = $"{ex.GetType().Name}: {ex.Message}";
            }

            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return result;
        }

        public async Task<SqlEditorResult> CommitAsync(CancellationToken ct = default)
        {
            var result = new SqlEditorResult();
            if (_tx is null)
            {
                result.Error = "No transaction is open.";
                return result;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await _tx.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                // A failed commit (e.g. a deferred constraint) leaves the work
                // unapplied; report it and let the connection go so the next run
                // starts clean.
                _logger.LogError(ex, "Transaction commit failed.");
                result.Error = ex.Message;
            }
            finally
            {
                await DisposeCoreAsync();
            }

            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return result;
        }

        public async Task<SqlEditorResult> RollbackAsync(CancellationToken ct = default)
        {
            var result = new SqlEditorResult();
            if (_tx is null)
            {
                result.Error = "No transaction is open.";
                return result;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await _tx.RollbackAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Transaction rollback failed (connection may already be gone).");
                result.Error = ex.Message;
            }
            finally
            {
                await DisposeCoreAsync();
            }

            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return result;
        }

        public async Task ResetAsync()
        {
            if (_tx is null)
                return;
            try
            {
                await _tx.RollbackAsync();
            }
            catch (Exception)
            {
                // Connection already gone / server dropped it: nothing left to roll
                // back. Disposing below is all that remains.
            }
            await DisposeCoreAsync();
        }

        private async Task DisposeCoreAsync()
        {
            if (_tx is not null)
            {
                await _tx.DisposeAsync();
                _tx = null;
            }
            if (_conn is not null)
            {
                await _conn.DisposeAsync();
                _conn = null;
            }
            _profileId = null;
        }

        private IDbConnector ResolveConnector(ConnectionProfile profile) =>
            _registry.Resolve(profile.EngineKey)
            ?? throw new InvalidOperationException($"Unknown engine '{profile.EngineKey}'.");

        public void Dispose()
        {
            // Sync teardown (circuit ending): a best-effort blocking rollback, then
            // drop the connection. Dispose cannot await, so the async path is not used.
            try { _tx?.Rollback(); } catch (Exception) { /* connection already gone */ }
            try { _tx?.Dispose(); } catch (Exception) { /* ignore */ }
            try { _conn?.Dispose(); } catch (Exception) { /* ignore */ }
            _tx = null;
            _conn = null;
        }
    }
}
