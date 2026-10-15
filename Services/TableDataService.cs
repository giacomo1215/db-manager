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
    /// Loads a table's rows + column metadata for the data editor. Reads go
    /// through the active connection's connector (BuildSelectTopN for the rows,
    /// GetTableColumnsAsync for the key/identity/nullability flags) so the read
    /// matches exactly what the sidebar quick-peek runs. Never throws.
    /// </summary>
    public class TableDataService : ITableDataService
    {
        private readonly IDbConnectorRegistry _registry;
        private readonly int _commandTimeout;
        private readonly ILogger<TableDataService> _logger;

        public TableDataService(IDbConnectorRegistry registry, IConfiguration configuration, ILogger<TableDataService> logger)
        {
            _registry = registry;
            _commandTimeout = configuration.GetValue("SqlEditor:CommandTimeoutSeconds", 60);
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<TableDataResult> LoadAsync(ConnectionProfile profile, string qualifiedName, int limit, CancellationToken ct = default)
        {
            var result = new TableDataResult { LoadLimit = limit };

            try
            {
                var connector = _registry.Resolve(profile.EngineKey)
                    ?? throw new InvalidOperationException($"Unknown engine '{profile.EngineKey}'.");
                if (!connector.SupportsTableEditing)
                {
                    result.Error = $"Table editing is not supported for {connector.DisplayName}.";
                    return result;
                }

                await using var conn = connector.CreateConnection(profile.ConnectionString);
                await conn.OpenAsync(ct);

                result.Columns.AddRange(await connector.GetTableColumnsAsync(conn, qualifiedName, ct));
                result.HasPrimaryKey = result.Columns.Exists(c => c.IsPrimaryKey);

                // Same read the sidebar quick-peek uses. "SELECT *" keeps the
                // table's column order, which is the order the metadata above is in.
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = connector.BuildSelectTopN(qualifiedName, limit);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                result.Rows = await SqlExecutionHelpers.ReadCurrentTableAsync(reader, ct);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                _logger.LogError(ex, "Loading table '{Table}' failed on {Engine} profile '{Profile}'.", qualifiedName, profile.EngineKey, profile.Name);
                result.Error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Loading table '{Table}' failed (unexpected) on {Engine} profile '{Profile}'.", qualifiedName, profile.EngineKey, profile.Name);
                result.Error = $"{ex.GetType().Name}: {ex.Message}";
            }

            return result;
        }

        /// <inheritdoc />
        public async Task<SqlEditorResult> ApplyAsync(ConnectionProfile profile, string script, int expectedChanges, CancellationToken ct = default)
        {
            var result = new SqlEditorResult();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var connector = _registry.Resolve(profile.EngineKey)
                    ?? throw new InvalidOperationException($"Unknown engine '{profile.EngineKey}'.");

                await using var conn = connector.CreateConnection(profile.ConnectionString);
                await conn.OpenAsync(ct);
                // ONE transaction: the whole change set commits or nothing does.
                await using var tx = await conn.BeginTransactionAsync(ct);

                var batches = SqlExecutionHelpers.SplitOnGo(script, connector)
                    .Where(b => !string.IsNullOrWhiteSpace(b)).ToList();

                foreach (var batch in batches)
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = batch;
                    cmd.CommandTimeout = _commandTimeout;
                    cmd.Transaction = tx;

                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    // DML only: no result grid to read, just consume every set.
                    do { } while (await reader.NextResultAsync(ct));
                    if (!reader.IsClosed)
                        await reader.CloseAsync();
                    // RecordsAffected is only reliable once the reader is closed.
                    if (reader.RecordsAffected > 0)
                        result.RowsAffected += reader.RecordsAffected;
                }

                // Every change is one single-row statement, so fewer affected rows
                // than expected means a target row vanished (deleted, or its key
                // changed) after the grid loaded. Roll the whole run back rather than
                // apply a partial change set.
                if (result.RowsAffected < expectedChanges)
                {
                    await tx.RollbackAsync(ct);
                    result.RowsAffected = 0;
                    result.Error = "One or more rows were changed or deleted by another user since the grid was loaded. Nothing was applied — reload and retry.";
                }
                else
                {
                    await tx.CommitAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                result.Error = "Apply cancelled.";
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                _logger.LogError(ex, "Applying table changes failed on {Engine} profile '{Profile}'.", profile.EngineKey, profile.Name);
                result.Error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Applying table changes failed (unexpected) on {Engine} profile '{Profile}'.", profile.EngineKey, profile.Name);
                result.Error = $"{ex.GetType().Name}: {ex.Message}";
            }

            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return result;
        }
    }
}
