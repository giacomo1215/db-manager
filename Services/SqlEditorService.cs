using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DbManager.Abstractions;
using DbManager.Models;
using Microsoft.Extensions.Configuration;

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

        // GO batch separator: a line containing only "GO", case-insensitive. SSMS
        // treats GO the same way (its batches never reach the server). Only used
        // for engines with SupportsGoBatches (T-SQL); the others always get the
        // whole script as a single batch. Limitation (accepted heuristic for an
        /// admin tool): a "GO" inside a string literal or comment would be split.
        private static readonly Regex GoBatchRegex = new(@"^\s*GO\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        // Comments are stripped before write-detection so a batch that only LOOKS
        // like a write behind "--" or /* */ is not misdetected (and vice versa).
        private static readonly Regex LineCommentRegex = new(@"--[^\n]*");
        private static readonly Regex BlockCommentRegex = new(@"/\*[\s\S]*?\*/");

        public SqlEditorService(IDbConnectorRegistry registry, IConfiguration configuration)
        {
            _registry = registry;
            _commandTimeout = configuration.GetValue("SqlEditor:CommandTimeoutSeconds", 60);
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
        public async Task<SqlEditorResult> ExecuteAsync(ConnectionProfile profile, string sql, CancellationToken ct = default)
        {
            var result = new SqlEditorResult();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var connector = ResolveConnector(profile);

                // GO batches are executed sequentially, each on its own connection:
                // simplest mental model and matches SSMS (GO also breaks variable scope).
                var batches = SplitOnGo(sql, connector).Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
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
                    do
                    {
                        if (reader.FieldCount > 0)
                        {
                            // Deliberately NOT DataTable.Load(reader): Load CLOSES
                            // the reader when it thinks it is done, so the loop's
                            // NextResultAsync throws InvalidOperationException and
                            // kills the circuit, and it silently swallowed every
                            // result set after the first. Filling the DataTable
                            // manually keeps the reader open and walks ALL result
                            // sets ourselves.
                            var table = new DataTable();

                            // Columns first, from the reader's metadata. Duplicate
                            // aliases ("SELECT 1 AS a, 2 AS a") would throw
                            // DuplicateNameException in DataTable.Columns.Add, so
                            // they get a " (2)" suffix like a spreadsheet would.
                            for (var i = 0; i < reader.FieldCount; i++)
                            {
                                var name = reader.GetName(i);
                                var baseName = name;
                                for (var dup = 2; table.Columns.Contains(name); dup++)
                                    name = $"{baseName} ({dup})";
                                table.Columns.Add(name, reader.GetFieldType(i));
                            }

                            while (await reader.ReadAsync(ct))
                            {
                                var row = table.NewRow();
                                for (var i = 0; i < reader.FieldCount; i++)
                                    row[i] = reader.GetValue(i);
                                table.Rows.Add(row);
                            }

                            result.ResultSets.Add(table);
                        }
                    } while (await reader.NextResultAsync(ct));

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
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
            {
                // SqlException, MySqlException, NpgsqlException and SqliteException
                // all derive from DbException: engine-level errors (bad SQL, wrong
                // credentials, timeout). Shown inline in the results pane like SSMS.
                result.Error = ex.Message;
            }
            catch (Exception ex)
            {
                // Safety net so NOTHING here can terminate the circuit. Includes
                // the exception type because non-DB failures (e.g. invalid state or
                // provider bugs) are harder to diagnose from Message alone.
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

            foreach (var batch in SplitOnGo(sql, connector))
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
        /// Splits a script on GO batch separators for dialects that support them;
        /// everything else is one single batch (the common case).
        /// </summary>
        private static string[] SplitOnGo(string sql, IDbConnector connector) =>
            connector.SupportsGoBatches ? GoBatchRegex.Split(sql) : [sql];

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