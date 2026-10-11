using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using DbManager.Abstractions;
using DbManager.Models;
using DbManager.Services.Dumping;

namespace DbManager.Providers
{
    /// <summary>
    /// SQLite dump: the authoritative DDL for every object is already in
    /// sqlite_master.sql (tables incl. PK/FK/CHECK, indexes, views, triggers) —
    /// exported verbatim, so fidelity is whatever the engine itself stores.
    /// Autoincrement counters are preserved via sqlite_sequence.
    /// Known limitation: FTS virtual tables' shadow tables are dumped as real
    /// tables and collide with what CREATE VIRTUAL TABLE recreates on import.
    /// </summary>
    public class SqliteDumper : IDbDumper
    {
        public string EngineKey => "sqlite";

        // type ordering: tables (so data has targets), then views, indexes,
        // triggers. sql IS NULL rows are UNIQUE/PK autoindexes — their DDL is
        // already inside the CREATE TABLE text.
        private const string ObjectsSql = """
            SELECT type, name, sql
            FROM sqlite_master
            WHERE name NOT LIKE 'sqlite\_%' ESCAPE '\'
              AND sql IS NOT NULL
            ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'view' THEN 1 WHEN 'index' THEN 2 ELSE 3 END, name;
            """;

        public async Task<DumpStats> DumpAsync(IDbConnector connector, DbConnection conn, DumpOptions options,
            TextWriter output, IProgress<DumpProgress>? progress, CancellationToken ct = default)
        {
            var stats = new DumpStats();
            var objects = (await conn.QueryAsync<(string Type, string Name, string Sql)>(
                new CommandDefinition(ObjectsSql, cancellationToken: ct))).ToList();

            var tables = objects.Where(o => o.Type == "table").ToList();
            var views = objects.Where(o => o.Type == "view").ToList();
            var indexes = objects.Where(o => o.Type == "index").ToList();
            var triggers = objects.Where(o => o.Type == "trigger").ToList();
            stats.Tables = tables.Count;
            stats.Views = views.Count;
            stats.Indexes = indexes.Count;
            var total = objects.Count;

            var done = 0;
            await WriteHeaderAsync(output, "SQLite", options);

            // Foreign keys off: the script is standalone (any client can run it)
            // and table order then doesn't matter.
            await WriteLineAsync(output, "PRAGMA foreign_keys = OFF;");

            // ---- tables: ALL DDL first, then ALL data -------------------------------
            // Data after every CREATE TABLE, not interleaved: an FK cycle (a→b, b→a)
            // otherwise INSERTs into the first table while the second doesn't exist
            // yet. At import defer_foreign_keys=ON moves the checks to COMMIT, by
            // which time every parent exists. (The PRAGMA above is belt-and-braces
            // for running the script standalone — inside our own import it is a
            // no-op since a transaction is already open.)
            foreach (var table in tables)
            {
                progress?.Report(new DumpProgress("Schema", table.Name, 0, done, total));
                await WriteObjectAsync(output, connector, "table", table.Name, table.Sql);
                done++;
            }

            if (options.IncludeData)
            {
                foreach (var table in tables)
                {
                    var columns = await GetQuotedColumnsAsync(conn, connector, table.Name, ct);
                    var rows = await InsertWriter.WriteInsertsAsync(conn, connector, SqlDialect.Sqlite,
                        connector.QuoteIdentifier(table.Name), columns, output, options,
                        new TableProgress(progress, table.Name), ct);
                    stats.Rows += rows;
                }

                await WriteSequencesAsync(conn, output, ct);
            }

            // ---- views, indexes, triggers ---------------------------------------------
            foreach (var view in views)
            {
                progress?.Report(new DumpProgress("Schema", view.Name, 0, done, total));
                await WriteObjectAsync(output, connector, "view", view.Name, view.Sql);
                done++;
            }
            foreach (var index in indexes)
            {
                progress?.Report(new DumpProgress("Schema", index.Name, 0, done, total));
                await WriteObjectAsync(output, connector, "index", index.Name, index.Sql);
                done++;
            }
            foreach (var trigger in triggers)
            {
                progress?.Report(new DumpProgress("Schema", trigger.Name, 0, done, total));
                // BEGIN…END bodies carry embedded semicolons — same marker
                // treatment as MySQL/Postgres routine bodies (raw splitter mode).
                await WriteLineAsync(output, Environment.NewLine + $"-- trigger: {trigger.Name}");
                await WriteLineAsync(output, $"DROP TRIGGER IF EXISTS {connector.QuoteIdentifier(trigger.Name)};");
                await WriteLineAsync(output, ScriptSplitter.RoutineStartMarker);
                await WriteLineAsync(output, trigger.Sql);
                await WriteLineAsync(output, ";");
                await WriteLineAsync(output, ScriptSplitter.RoutineEndMarker);
                done++;
            }

            await WriteLineAsync(output, "PRAGMA foreign_keys = ON;");
            return stats;
        }

        /// <summary>Column names (quoted) in table order — the explicit INSERT column list.</summary>
        private static async Task<List<string>> GetQuotedColumnsAsync(DbConnection conn, IDbConnector connector,
            string tableName, CancellationToken ct)
        {
            var columns = await conn.QueryAsync<(int Cid, string Name, string Type, int NotNull, string DfltValue, int Pk)>(
                new CommandDefinition($"PRAGMA table_info({connector.QuoteIdentifier(tableName)});", cancellationToken: ct));
            return columns.Select(c => connector.QuoteIdentifier(c.Name)).ToList();
        }

        /// <summary>
        /// AUTOINCREMENT counters live in the hidden sqlite_sequence table; replaying
        /// them keeps new-row ids from colliding with restored rows.
        /// </summary>
        private static async Task WriteSequencesAsync(DbConnection conn, TextWriter output, CancellationToken ct)
        {
            var exists = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT 1 FROM sqlite_master WHERE name = 'sqlite_sequence';", cancellationToken: ct));
            if (exists != 1)
                return; // no AUTOINCREMENT tables at all

            var rows = (await conn.QueryAsync<(string Name, long Seq)>(
                new CommandDefinition("SELECT name, seq FROM sqlite_sequence;", cancellationToken: ct))).ToList();
            if (rows.Count == 0)
                return;

            await WriteLineAsync(output, Environment.NewLine + "-- autoincrement counters");
            foreach (var (name, seq) in rows)
            {
                var literal = "'" + name.Replace("'", "''") + "'";
                await WriteLineAsync(output, $"UPDATE sqlite_sequence SET seq = {seq} WHERE name = {literal};");
            }
        }

        /// <summary>DROP IF EXISTS + the engine's own DDL, semicolon-terminated.</summary>
        private static async Task WriteObjectAsync(TextWriter output, IDbConnector connector,
            string type, string name, string sql)
        {
            var drop = type switch
            {
                "table" => "DROP TABLE IF EXISTS",
                "view" => "DROP VIEW IF EXISTS",
                "index" => "DROP INDEX IF EXISTS",
                _ => "DROP TRIGGER IF EXISTS"
            };
            await WriteLineAsync(output, Environment.NewLine + $"-- {type}: {name}");
            await WriteLineAsync(output, $"{drop} {connector.QuoteIdentifier(name)};");
            // sqlite_master.sql carries no trailing semicolon — add it.
            await WriteLineAsync(output, sql);
            await WriteLineAsync(output, ";");
        }

        internal static Task WriteHeaderAsync(TextWriter output, string engine, DumpOptions options)
        {
            return output.WriteAsync(
                "-- db-manager database backup" + Environment.NewLine +
                $"-- Engine: {engine}" + Environment.NewLine +
                $"-- Generated (UTC): {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}" + Environment.NewLine +
                $"-- Mode: {(options.IncludeData ? "schema + data" : "schema only")}" + Environment.NewLine +
                "-- Objects with the same names will be DROPPED and replaced on import." + Environment.NewLine
            );
        }

        internal static Task WriteLineAsync(TextWriter output, string? line) =>
            output.WriteLineAsync(line ?? string.Empty);

        /// <summary>Forwards InsertWriter's row reports with the table name attached.</summary>
        private sealed class TableProgress : IProgress<DumpProgress>
        {
            private readonly IProgress<DumpProgress>? _inner;
            private readonly string _tableName;

            public TableProgress(IProgress<DumpProgress>? inner, string tableName)
            {
                _inner = inner;
                _tableName = tableName;
            }

            public void Report(DumpProgress value) =>
                _inner?.Report(new DumpProgress("Data", _tableName, value.RowsDone, value.ObjectsDone, value.ObjectsTotal));
        }
    }
}