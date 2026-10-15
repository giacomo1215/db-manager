using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DbManager.Abstractions;
using DbManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DbManager.Services
{
    /// <summary>
    /// Executes arbitrary SQL against a saved connection and reads its schema —
    /// the provider-agnostic rewrite of the ZeatDashboard SqlEditorService. All
    /// dialect behavior lives in the engine's IDbConnector; this class only
    /// orchestrates: batch splitting, execution, result materialization and the
    /// never-throw contract.
    ///
    /// Errors are ALWAYS carried in SqlEditorResult.Error: an unhandled exception
    /// in a Blazor Server event handler would tear down the whole circuit.
    /// </summary>
    public class SqlEditorService : ISqlEditorService
    {
        private readonly IDbConnectorRegistry _registry;
        private readonly int _commandTimeout;
        private readonly ILogger<SqlEditorService> _logger;

        // Comments are stripped before write-detection so a batch that only LOOKS
        // like a write behind "--" or /* */ is not misdetected (and vice versa).
        private static readonly Regex LineCommentRegex = new(@"--[^\n]*");
        private static readonly Regex BlockCommentRegex = new(@"/\*[\s\S]*?\*/");

        public SqlEditorService(IDbConnectorRegistry registry, IConfiguration configuration, ILogger<SqlEditorService> logger)
        {
            _registry = registry;
            _commandTimeout = configuration.GetValue("SqlEditor:CommandTimeoutSeconds", 60);
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<List<SchemaObject>> GetSchemaAsync(ConnectionProfile profile, CancellationToken ct = default)
        {
            var connector = ResolveConnector(profile);
            await using var conn = connector.CreateConnection(profile.ConnectionString);
            await conn.OpenAsync(ct);
            return await connector.GetSchemaAsync(conn, ct);
        }

        /// <inheritdoc />
        public async Task<List<string>> ListDatabasesAsync(ConnectionProfile profile, CancellationToken ct = default)
        {
            var connector = ResolveConnector(profile);
            await using var conn = connector.CreateConnection(connector.BuildServerConnectionString(profile.ToFormData()));
            await conn.OpenAsync(ct);
            return await connector.ListDatabasesAsync(conn, ct);
        }

        /// <inheritdoc />
        public async Task<List<DatabaseSchema>> GetServerSchemaAsync(ConnectionProfile profile, CancellationToken ct = default)
        {
            var connector = ResolveConnector(profile);
            var databases = await ListDatabasesAsync(profile, ct);

            // Bounded parallelism: a 50-database server must not open 50
            // connections at once, but sequential round trips would be needlessly
            // slow (each database is ~5 independent catalog queries). Profiles
            // created from a raw connection string have empty structured fields,
            // so their per-database connections all fail — the server schema
            // simply comes back empty for those.
            using var throttle = new SemaphoreSlim(8, 8);
            var tasks = databases.Select(async db =>
            {
                await throttle.WaitAsync(ct);
                try
                {
                    await using var conn = connector.CreateConnection(connector.BuildConnectionString(profile.ToFormData(db)));
                    await conn.OpenAsync(ct);
                    return new DatabaseSchema { Name = db, Objects = await connector.GetSchemaAsync(conn, ct) };
                }
                catch (Exception)
                {
                    // Per-DB never-throw: no access / offline / timeout skips that
                    // database — one restricted database must not kill cross-DB
                    // IntelliSense for the rest.
                    return null!;
                }
                finally
                {
                    throttle.Release();
                }
            });
            var loaded = await Task.WhenAll(tasks);
            // Preserves the ListDatabasesAsync order (sys.databases is sorted by name).
            return loaded.Where(d => d is not null).Cast<DatabaseSchema>().ToList();
        }

        /// <inheritdoc />
        public async Task<SqlEditorResult> ExecuteAsync(ConnectionProfile profile, string sql, CancellationToken ct = default)
        {
            var result = new SqlEditorResult();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var connector = ResolveConnector(profile);

                // GO batches are executed sequentially, each on its own connection:
                // simplest mental model and matches SSMS (GO also breaks variable scope).
                var batches = SqlExecutionHelpers.SplitOnGo(sql, connector).Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
                if (batches.Count == 0)
                {
                    result.Error = "Empty query.";
                    return result;
                }

                foreach (var batch in batches)
                {
                    await using var conn = connector.CreateConnection(profile.ConnectionString);
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = batch;
                    cmd.CommandTimeout = _commandTimeout;
                    await conn.OpenAsync(ct);

                    await using var reader = await cmd.ExecuteReaderAsync(ct);

                    // One iteration per result set. FieldCount == 0 means the
                    // statement produced rows-AFFECTED info only (INSERT/UPDATE/
                    // DELETE), not a grid — nothing to load there.
                    while (true)
                    {
                        if (reader.FieldCount > 0)
                            result.ResultSets.Add(await SqlExecutionHelpers.ReadCurrentTableAsync(reader, ct));
                        if (!await reader.NextResultAsync(ct))
                            break;
                    }

                    // RecordsAffected only becomes reliable once every row has been
                    // consumed AND the reader is closed (SqlClient contract; other
                    // providers are similar), hence CloseAsync before reading. It is
                    // -1 for pure SELECT batches — the UI treats -1 as "no DML info".
                    if (!reader.IsClosed)
                        await reader.CloseAsync();
                    if (reader.RecordsAffected > 0)
                        result.RowsAffected += reader.RecordsAffected;
                }
            }
            catch (OperationCanceledException)
            {
                // The user pressed Cancel (or the circuit tore down): the
                // CancellationToken fired and the provider aborted the command.
                // A deliberate stop, not a failure — flagged so the page skips the
                // error toast and shows a plain "cancelled" note instead.
                result.Cancelled = true;
                result.Error = "Query cancelled.";
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
            {
                // SqlException, MySqlException, NpgsqlException and SqliteException
                // all derive from DbException: engine-level errors (bad SQL, wrong
                // credentials, timeout). Shown inline in the results pane like SSMS.
                _logger.LogError(ex, "Query failed on {Engine} profile '{Profile}'.", profile.EngineKey, profile.Name);
                result.Error = ex.Message;
            }
            catch (Exception ex)
            {
                // Safety net so NOTHING here can terminate the circuit. Includes
                // the exception type because non-DB failures (e.g. invalid state or
                // provider bugs) are harder to diagnose from Message alone.
                _logger.LogError(ex, "Query failed (unexpected) on {Engine} profile '{Profile}'.", profile.EngineKey, profile.Name);
                result.Error = $"{ex.GetType().Name}: {ex.Message}";
            }

            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return result;
        }

        /// <inheritdoc />
        public async Task<SqlEditorResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct = default)
        {
            var result = new SqlEditorResult();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var connector = ResolveConnector(profile);
                var commands = connector.BuildExplainCommands(sql);

                // ONE connection for the whole sequence: T-SQL's SET SHOWPLAN_XML is
                // a session setting that must survive from the ON command to the
                // statement (ExecuteAsync's per-batch connections would lose it).
                await using var conn = connector.CreateConnection(profile.ConnectionString);
                await conn.OpenAsync(ct);

                foreach (var commandText in commands)
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = commandText;
                    cmd.CommandTimeout = _commandTimeout;

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
                }
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                result.Error = "Query cancelled.";
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
            {
                _logger.LogError(ex, "Explain failed on {Engine} profile '{Profile}'.", profile.EngineKey, profile.Name);
                result.Error = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Explain failed (unexpected) on {Engine} profile '{Profile}'.", profile.EngineKey, profile.Name);
                result.Error = $"{ex.GetType().Name}: {ex.Message}";
            }

            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return result;
        }

        /// <inheritdoc />
        public bool IsWriteStatement(ConnectionProfile profile, string sql)
        {
            var connector = ResolveConnector(profile);
            var writeKeywords = connector.WriteKeywords;

            foreach (var batch in SqlExecutionHelpers.SplitOnGo(sql, connector))
            {
                var bare = StripComments(batch).Trim();
                if (bare.Length == 0)
                    continue;

                // Every semicolon-separated statement is inspected, not just the
                // first: a pasted "SELECT …; UPDATE …" script is a write. Splitting
                // on ';' is a heuristic — a semicolon inside a string literal can
                // cause a FALSE POSITIVE (a needless confirm dialog), never a
                // false negative, which is the safe direction for this gate.
                foreach (var segment in bare.Split(';'))
                {
                    var tokens = segment.TrimStart().Split(new[] { ' ', '\t', '\r', '\n', '(' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length == 0)
                        continue;

                    // First keyword decides: "UPDATE ..." → write, "SELECT ..." → read.
                    if (writeKeywords.Contains(tokens[0]))
                        return true;

                    // Edge case (T-SQL): SELECT INTO creates a table, so it is a
                    // write even though it starts with SELECT. INTO must appear
                    // BEFORE any FROM ("SELECT * INTO newt FROM oldt"), otherwise
                    // it would fire on every "SELECT … FROM" read.
                    if (connector.IsSelectIntoWrite
                        && tokens[0].Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                    {
                        for (var i = 1; i < tokens.Length; i++)
                        {
                            if (tokens[i].Equals("INTO", StringComparison.OrdinalIgnoreCase))
                                return true;
                            if (tokens[i].Equals("FROM", StringComparison.OrdinalIgnoreCase))
                                break;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Removes -- line comments and /* */ block comments so keyword detection
        /// looks at real tokens. Heuristic limitation (accepted): a "--" inside a
        /// string literal would be stripped too. Worst case that causes is a wrong
        /// confirm-dialog decision, and writes still always hit the dialog via
        /// their real first keyword.
        /// </summary>
        private static string StripComments(string sql) =>
            BlockCommentRegex.Replace(LineCommentRegex.Replace(sql, string.Empty), string.Empty);

        private IDbConnector ResolveConnector(ConnectionProfile profile) =>
            _registry.Resolve(profile.EngineKey)
            ?? throw new InvalidOperationException($"Unknown engine '{profile.EngineKey}'.");
    }
}