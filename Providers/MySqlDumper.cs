using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using DbManager.Abstractions;
using DbManager.Models;
using DbManager.Services.Dumping;

namespace DbManager.Providers
{
    /// <summary>
    /// MySQL/MariaDB dump: DDL comes straight from SHOW CREATE … (full fidelity:
    /// keys, constraints, charset/collation per column). Host-specific bits are
    /// stripped so the script imports anywhere: the AUTO_INCREMENT counter and
    /// the DEFINER= clauses (routines/views/triggers keep their SQL SECURITY
    /// characteristic, executing as the importing user).
    /// Import is NOT all-or-nothing — MySQL DDL auto-commits — and reruns are
    /// safe thanks to DROP … IF EXISTS.
    /// </summary>
    public class MySqlDumper : IDbDumper
    {
        public string EngineKey => "mysql";

        // "... AUTO_INCREMENT=123 ..." — counter is host state, not schema.
        private static readonly Regex AutoIncrementRegex = new(@"\s*AUTO_INCREMENT=\d+", RegexOptions.IgnoreCase);

        // DEFINER=`user`@`host` / user@host — both quoted and bare forms.
        private static readonly Regex DefinerRegex =
            new(@"DEFINER\s*=\s*(?:`[^`]*`|[^\s@`]+)@(?:`[^`]*`|[^\s`]+)", RegexOptions.IgnoreCase);

        private const string TablesSql = """
            SELECT TABLE_NAME FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_NAME;
            """;

        private const string ViewsSql = """
            SELECT TABLE_NAME FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'VIEW'
            ORDER BY TABLE_NAME;
            """;

        private const string RoutinesSql = """
            SELECT ROUTINE_NAME, ROUTINE_TYPE FROM information_schema.ROUTINES
            WHERE ROUTINE_SCHEMA = DATABASE() AND ROUTINE_TYPE IN ('PROCEDURE', 'FUNCTION')
            ORDER BY ROUTINE_NAME;
            """;

        private const string TriggersSql = """
            SELECT TRIGGER_NAME FROM information_schema.TRIGGERS
            WHERE TRIGGER_SCHEMA = DATABASE()
            ORDER BY TRIGGER_NAME;
            """;

        private const string ColumnsSql = """
            SELECT COLUMN_NAME FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t
              AND EXTRA NOT LIKE '%GENERATED%'
            ORDER BY ORDINAL_POSITION;
            """;

        public async Task<DumpStats> DumpAsync(IDbConnector connector, DbConnection conn, DumpOptions options,
            TextWriter output, IProgress<DumpProgress>? progress, CancellationToken ct = default)
        {
            var stats = new DumpStats();
            var q = connector.QuoteIdentifier; // engine-correct quoting (doubles embedded backticks)
            var tables = await QueryNamesAsync(conn, TablesSql, ct);
            var views = await QueryNamesAsync(conn, ViewsSql, ct);
            var routines = (await conn.QueryAsync<(string Name, string Type)>(
                new CommandDefinition(RoutinesSql, cancellationToken: ct))).ToList();
            var triggers = await QueryNamesAsync(conn, TriggersSql, ct);
            var total = tables.Count + views.Count + routines.Count + triggers.Count;
            var done = 0;

            await SqliteDumper.WriteHeaderAsync(output, "MySQL / MariaDB", options);

            // FKs are inline in SHOW CREATE TABLE output, so table order would
            // otherwise matter — for the whole script run, checks stay off.
            await SqliteDumper.WriteLineAsync(output, "SET NAMES utf8mb4;");
            await SqliteDumper.WriteLineAsync(output, "SET FOREIGN_KEY_CHECKS = 0;");

            foreach (var table in tables)
            {
                progress?.Report(new DumpProgress("Schema", table, 0, done, total));
                await SqliteDumper.WriteLineAsync(output, Environment.NewLine + $"-- table: {table}");
                var create = await ShowCreateAsync(conn, $"SHOW CREATE TABLE {q(table)}", 1, ct);
                await SqliteDumper.WriteLineAsync(output, $"DROP TABLE IF EXISTS {q(table)};");
                await SqliteDumper.WriteLineAsync(output, AutoIncrementRegex.Replace(create, string.Empty).TrimEnd() + ";");

                if (options.IncludeData)
                {
                    var columns = await conn.QueryAsync<string>(
                        new CommandDefinition(ColumnsSql, new { t = table }, cancellationToken: ct));
                    var rows = await InsertWriter.WriteInsertsAsync(conn, connector, SqlDialect.MySql,
                        q(table), columns.Select(q).ToList(), output, options, progress, ct);
                    stats.Rows += rows;
                }
                done++;
            }

            foreach (var view in views)
            {
                progress?.Report(new DumpProgress("Schema", view, 0, done, total));
                await SqliteDumper.WriteLineAsync(output, Environment.NewLine + $"-- view: {view}");
                var create = await ShowCreateAsync(conn, $"SHOW CREATE VIEW {q(view)}", 1, ct);
                await SqliteDumper.WriteLineAsync(output, $"DROP VIEW IF EXISTS {q(view)};");
                await SqliteDumper.WriteLineAsync(output, DefinerRegex.Replace(create, string.Empty) + ";");
                done++;
            }
            stats.Views = views.Count;

            // Routine/trigger bodies contain semicolons (BEGIN…END): the dbm
            // markers make the importer treat each as ONE statement.
            foreach (var (name, type) in routines)
            {
                progress?.Report(new DumpProgress("Schema", name, 0, done, total));
                await SqliteDumper.WriteLineAsync(output, Environment.NewLine + $"-- {type.ToLowerInvariant()}: {name}");
                var show = type == "PROCEDURE" ? "PROCEDURE" : "FUNCTION";
                var create = await ShowCreateAsync(conn, $"SHOW CREATE {show} {q(name)}", 2, ct);
                var drop = type == "PROCEDURE" ? $"DROP PROCEDURE IF EXISTS {q(name)};" : $"DROP FUNCTION IF EXISTS {q(name)};";
                await WriteRoutineAsync(output, drop, DefinerRegex.Replace(create, string.Empty), ct);
                stats.Routines++;
                done++;
            }

            foreach (var trigger in triggers)
            {
                progress?.Report(new DumpProgress("Schema", trigger, 0, done, total));
                await SqliteDumper.WriteLineAsync(output, Environment.NewLine + $"-- trigger: {trigger}");
                // "SQL Original Statement" is the 3rd column of SHOW CREATE TRIGGER.
                var create = await ShowCreateAsync(conn, $"SHOW CREATE TRIGGER {q(trigger)}", 2, ct);
                await WriteRoutineAsync(output, $"DROP TRIGGER IF EXISTS {q(trigger)};",
                    DefinerRegex.Replace(create, string.Empty), ct);
                done++;
            }

            await SqliteDumper.WriteLineAsync(output, Environment.NewLine + "SET FOREIGN_KEY_CHECKS = 1;");
            stats.Tables = tables.Count;
            return stats;
        }

        /// <summary>DROP first, then the body wrapped in routine markers (embedded semicolons stay intact).</summary>
        private static async Task WriteRoutineAsync(TextWriter output, string drop, string createSql, CancellationToken ct)
        {
            await SqliteDumper.WriteLineAsync(output, drop);
            await output.WriteAsync(ScriptSplitter.RoutineStartMarker + "\n");
            await output.WriteAsync(createSql.TrimEnd() + ";\n");
            await output.WriteAsync(ScriptSplitter.RoutineEndMarker + "\n");
            _ = ct;
        }

        /// <summary>
        /// Runs SHOW CREATE … and picks the Nth column: SHOW CREATE TABLE's DDL
        /// is column 1, routines'/triggers' is column 2, views' is column 1.
        /// </summary>
        private static async Task<string> ShowCreateAsync(DbConnection conn, string sql, int column, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException($"No output from: {sql}");
            return reader.GetString(column);
        }

        private static async Task<List<string>> QueryNamesAsync(DbConnection conn, string sql, CancellationToken ct) =>
            (await conn.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: ct))).ToList();
    }
}