using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DbManager.Abstractions;
using DbManager.Models;
using DbManager.Services.Dumping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DbManager.Services
{
    /// <summary>
    /// Database-level backup coordinator: resolves the engine's connector and
    /// dumper, owns the connection/temp-file/timeout plumbing, and runs
    /// imports. The connection-vault backup (BackupService) is a different
    /// concern — this one dumps the connected DATABASE itself.
    ///
    /// Imports deliberately do NOT reuse SqlEditorService.ExecuteAsync: the
    /// editor opens a fresh connection per batch, which would lose session
    /// state (SET FOREIGN_KEY_CHECKS, PRAGMA defer_foreign_keys) and could
    /// never be transactional. Here the whole script runs over ONE connection.
    ///
    /// Never-throw contract like SqlEditorService: errors land in the result
    /// objects; an unhandled exception in a Blazor handler kills the circuit.
    /// </summary>
    public class DbDumpService : IDbDumpService
    {
        private readonly IDbConnectorRegistry _registry;
        private readonly IReadOnlyDictionary<string, IDbDumper> _dumpers;
        private readonly int _commandTimeout;
        private readonly ILogger<DbDumpService> _logger;

        public DbDumpService(IDbConnectorRegistry registry, IEnumerable<IDbDumper> dumpers, IConfiguration configuration, ILogger<DbDumpService> logger)
        {
            _registry = registry;
            _dumpers = dumpers.ToDictionary(d => d.EngineKey, StringComparer.OrdinalIgnoreCase);
            // 0 = infinite: dumps/imports routinely exceed the editor's 60 s.
            _commandTimeout = configuration.GetValue("SqlEditor:DumpCommandTimeoutSeconds", 0);
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<DumpResult> DumpAsync(ConnectionProfile profile, DumpOptions options,
            IProgress<DumpProgress>? progress, CancellationToken ct = default)
        {
            var result = new DumpResult();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var tempPath = Path.Combine(Path.GetTempPath(), $"dbm-dump-{Guid.NewGuid():N}.sql");

            try
            {
                var connector = ResolveConnector(profile);
                var dumper = ResolveDumper(profile);
                options.CommandTimeout = _commandTimeout;

                await using var conn = connector.CreateConnection(profile.ConnectionString);
                await conn.OpenAsync(ct);

                await using (var writer = new StreamWriter(tempPath, false, new UTF8Encoding(false)))
                {
                    var stats = await dumper.DumpAsync(connector, conn, options, writer, progress, ct);
                    await writer.FlushAsync(ct);

                    result.Tables = stats.Tables;
                    result.Views = stats.Views;
                    result.Routines = stats.Routines;
                    result.Indexes = stats.Indexes;
                    result.Rows = stats.Rows;
                }

                result.TempScriptPath = tempPath; // success: the UI streams + deletes it
                result.ScriptBytes = new FileInfo(tempPath).Length;
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                _logger.LogError(ex, "Dump failed on {Engine} profile '{Profile}'.", profile.EngineKey, profile.Name);
                TryDelete(tempPath);
                result.Error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dump failed (unexpected) on {Engine} profile '{Profile}'.", profile.EngineKey, profile.Name);
                TryDelete(tempPath);
                result.Error = $"{ex.GetType().Name}: {ex.Message}";
            }

            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return result;
        }

        /// <inheritdoc />
        public async Task<ImportPreview> AnalyzeScriptAsync(string scriptPath, ConnectionProfile profile, CancellationToken ct = default)
        {
            var preview = new ImportPreview();
            try
            {
                preview.Bytes = new FileInfo(scriptPath).Length;
                var connector = ResolveConnector(profile);
                using var reader = File.OpenText(scriptPath);
                await foreach (var _ in ScriptSplitter.ReadStatementsAsync(reader, SplitMode(connector), ct))
                    preview.Statements++;
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException or IOException)
            {
                _logger.LogError(ex, "Import preview failed on {Engine} profile '{Profile}'.", profile.EngineKey, profile.Name);
                preview.Error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Import preview failed (unexpected) on {Engine} profile '{Profile}'.", profile.EngineKey, profile.Name);
                preview.Error = $"{ex.GetType().Name}: {ex.Message}";
            }
            return preview;
        }

        /// <inheritdoc />
        public async Task<ImportResult> ImportAsync(ConnectionProfile profile, string scriptPath,
            IProgress<DumpProgress>? progress, CancellationToken ct = default)
        {
            var result = new ImportResult();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            result.FailedStatementIndex = -1;

            try
            {
                var connector = ResolveConnector(profile);

                await using var conn = connector.CreateConnection(profile.ConnectionString);
                await conn.OpenAsync(ct);

                if (connector.EngineKey == "sqlite")
                {
                    // FK violations are checked at COMMIT, so any table order
                    // loads. Must run BEFORE the transaction — a pragma cannot
                    // change once one is open.
                    await ExecSimple(conn, null, "PRAGMA defer_foreign_keys = ON", ct);
                }

                // MySQL DDL auto-commits, so a transaction there buys nothing and
                // its implicit COMMITs would misreport "all-or-nothing". The
                // other three engines are transactional.
                var useTransaction = connector.EngineKey != "mysql";
                DbTransaction? tx = null;
                if (useTransaction)
                    tx = await conn.BeginTransactionAsync(ct);

                using var reader = File.OpenText(scriptPath);
                var index = 0;
                await foreach (var statement in ScriptSplitter.ReadStatementsAsync(reader, SplitMode(connector), ct))
                {
                    index++;
                    try
                    {
                        await using var cmd = conn.CreateCommand();
                        cmd.CommandText = statement;
                        cmd.CommandTimeout = _commandTimeout;
                        if (tx is not null)
                            cmd.Transaction = tx;
                        await cmd.ExecuteNonQueryAsync(ct);
                        result.StatementsExecuted++;

                        if (index % 25 == 0)
                            progress?.Report(new DumpProgress("Import", null, result.StatementsExecuted, index, 0));
                    }
                    catch (Exception ex) when (ex is DbException or InvalidOperationException)
                    {
                        _logger.LogError(ex, "Import statement #{Index} failed on {Engine} profile '{Profile}'.", index, profile.EngineKey, profile.Name);
                        result.Error = ex.Message;
                        result.FailedStatementPreview = statement.Length <= 300 ? statement : statement[..300] + "…";
                        result.FailedStatementIndex = index;
                        if (tx is not null)
                        {
                            await tx.RollbackAsync(ct);
                            result.RolledBack = true;
                        }
                        stopwatch.Stop();
                        result.ElapsedMs = stopwatch.ElapsedMilliseconds;
                        return result;
                    }
                }

                if (tx is not null)
                    await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException or IOException)
            {
                // result.Error may already be set by the per-statement catch above;
                // still log the aborting exception (rollback/commit/IO).
                _logger.LogError(ex, "Import aborted on {Engine} profile '{Profile}'.", profile.EngineKey, profile.Name);
                result.Error ??= ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Import aborted (unexpected) on {Engine} profile '{Profile}'.", profile.EngineKey, profile.Name);
                result.Error ??= $"{ex.GetType().Name}: {ex.Message}";
            }

            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return result;
        }

        // ------------------------------------------------------------------ helpers

        private static SqlSplitMode SplitMode(IDbConnector connector) =>
            connector.SupportsGoBatches ? SqlSplitMode.GoBatches : SqlSplitMode.Semicolons;

        private static async Task ExecSimple(DbConnection conn, DbTransaction? tx, string sql, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            if (tx is not null)
                cmd.Transaction = tx;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private IDbConnector ResolveConnector(ConnectionProfile profile) =>
            _registry.Resolve(profile.EngineKey)
            ?? throw new InvalidOperationException($"Unknown engine '{profile.EngineKey}'.");

        private IDbDumper ResolveDumper(ConnectionProfile profile) =>
            _dumpers.GetValueOrDefault(profile.EngineKey)
            ?? throw new InvalidOperationException($"No dump support for engine '{profile.EngineKey}'.");

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch { /* best effort: temp files in GetTempPath are cleaned by the OS */ }
        }
    }
}