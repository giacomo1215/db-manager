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
    /// PostgreSQL dump: DDL composed from the pg_* catalog using the engine's
    /// own decompilers (format_type, pg_get_constraintdef, pg_get_indexdef,
    /// pg_get_viewdef, pg_get_functiondef, pg_get_triggerdef). pg_dump's
    /// ordering: sequences → tables (+defaults/identity, PK/unique/check
    /// inline) → data → setval → indexes + FKs (post-data, so dependency order
    /// cannot fail) → views (Kahn-sorted on view-on-view edges) → routines →
    /// triggers.
    /// Documented limitations: partition children are not dumped (only the
    /// parent); routines restore via CREATE OR REPLACE, so signatures that no
    /// longer exist in a newer script are not dropped. Managed-hosting
    /// internal schemas (Supabase auth/storage/…, extension schemas) are
    /// excluded — the dump is the user's own schemas only.
    /// </summary>
    public class PostgreSqlDumper : IDbDumper
    {
        public string EngineKey => "postgres";

        // Platform/managed-hosting internal schemas are never the user's data:
        // Supabase (auth, storage, realtime, vault, …), Neon/Cloud (extensions),
        // plus the standard system schemas. On a self-hosted server these simply
        // don't exist, so the filter is a no-op there.
        private const string ExcludedSchemas =
            "('pg_catalog', 'information_schema', 'auth', 'storage', 'realtime', 'supabase_functions', " +
            "'supabase_admin', 'vault', 'pgsodium', 'pgsodium_keyring', 'graphql', 'graphql_public', " +
            "'extensions', 'net', 'pgjwt', 'cron', 'pgtle')";

        private const string SequencesSql = $"""
            SELECT schemaname, sequencename, start_value, min_value, max_value, increment_by, cycle, last_value
            FROM pg_sequences
            WHERE schemaname NOT IN {ExcludedSchemas}
            ORDER BY schemaname, sequencename;
            """;

        private const string SequenceOwnershipSql = $"""
            SELECT n.nspname AS SchemaName, c.relname AS SeqName,
                   tn.nspname AS TableSchema, t.relname AS TableName, a.attname AS ColName
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_depend d ON d.objid = c.oid AND d.classid = 'pg_class'::regclass
                            AND d.refclassid = 'pg_class'::regclass AND d.deptype = 'a'
            JOIN pg_class t ON t.oid = d.refobjid
            JOIN pg_namespace tn ON tn.oid = t.relnamespace
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = d.refobjsubid
            WHERE c.relkind = 'S' AND n.nspname NOT IN {ExcludedSchemas}
              AND tn.nspname NOT IN {ExcludedSchemas};
            """;

        private const string TablesSql = $"""
            SELECT n.nspname AS SchemaName, c.relname AS TableName, c.oid AS Oid
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p') AND NOT c.relispartition
              AND n.nspname NOT IN {ExcludedSchemas}
            ORDER BY n.nspname, c.relname;
            """;

        private const string ColumnsSql = """
            SELECT a.attname AS Name, format_type(a.atttypid, a.atttypmod) AS ColType, a.attnotnull AS NotNull,
                   a.attidentity::text AS IdentityKind, a.attgenerated::text AS GeneratedKind,
                   pg_get_expr(ad.adbin, ad.adrelid) AS DefaultExpr
            FROM pg_attribute a
            LEFT JOIN pg_attrdef ad ON ad.adrelid = a.attrelid AND ad.adnum = a.attnum
            WHERE a.attrelid = @oid::oid AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum;
            """;

        // The insertable column list: stored-generated columns can't take values,
        // and an ALWAYS identity needs OVERRIDING SYSTEM VALUE on the INSERT.
        private const string DataColumnsSql = """
            SELECT a.attname AS Name, a.attidentity::text AS IdentityKind
            FROM pg_attribute a
            WHERE a.attrelid = @oid::oid AND a.attnum > 0 AND NOT a.attisdropped
              AND a.attgenerated = ''
            ORDER BY a.attnum;
            """;

        private const string ConstraintsSql = """
            SELECT conname AS Name, contype::text AS Kind, pg_get_constraintdef(oid, true) AS Def
            FROM pg_constraint WHERE conrelid = @oid::oid
            ORDER BY contype, conname;
            """;

        private const string IndexesSql = """
            SELECT n.nspname AS SchemaName, ci.relname AS Name, pg_get_indexdef(i.indexrelid) AS Def
            FROM pg_index i
            JOIN pg_class ci ON ci.oid = i.indexrelid
            JOIN pg_namespace n ON n.oid = ci.relnamespace
            WHERE i.indrelid = @oid::oid
              AND NOT EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conindid = i.indexrelid);
            """;

        private const string ForeignKeysSql = $"""
            SELECT conname AS Name, conrelid AS TableOid, pg_get_constraintdef(oid, true) AS Def
            FROM pg_constraint WHERE contype = 'f'
              AND conrelid IN (SELECT c.oid FROM pg_class c
                               JOIN pg_namespace n ON n.oid = c.relnamespace
                               WHERE c.relkind IN ('r', 'p') AND NOT c.relispartition
                                 AND n.nspname NOT IN {ExcludedSchemas})
            ORDER BY conname;
            """;

        private const string ViewsSql = $"""
            SELECT n.nspname AS SchemaName, c.relname AS ViewName, c.oid AS Oid
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'v' AND n.nspname NOT IN {ExcludedSchemas};
            """;

        private const string ViewDependenciesSql = $"""
            SELECT vn.nspname AS ViewSchema, vc.relname AS ViewName,
                   tn.nspname AS RefSchema, t.relname AS RefName
            FROM pg_rewrite rw
            JOIN pg_class vc ON vc.oid = rw.ev_class
            JOIN pg_namespace vn ON vn.oid = vc.relnamespace
            JOIN pg_depend d ON d.objid = rw.oid AND d.classid = 'pg_rewrite'::regclass AND d.deptype IN ('n', 'a')
            JOIN pg_class t ON t.oid = d.refobjid
            JOIN pg_namespace tn ON tn.oid = t.relnamespace
            WHERE vc.relkind = 'v' AND t.relkind = 'v'
              AND vn.nspname NOT IN {ExcludedSchemas}
              AND tn.nspname NOT IN {ExcludedSchemas};
            """;

        private const string RoutinesSql = $"""
            SELECT n.nspname AS SchemaName, p.proname AS Name, pg_get_functiondef(p.oid) AS Def
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname NOT IN {ExcludedSchemas}
              AND p.prokind IN ('f', 'p')
            ORDER BY n.nspname, p.proname;
            """;

        private const string TriggersSql = $"""
            SELECT n.nspname AS SchemaName, pg_get_triggerdef(t.oid) AS Def
            FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE NOT t.tgisinternal AND n.nspname NOT IN {ExcludedSchemas}
            ORDER BY n.nspname, t.tgrelid, t.tgname;
            """;

        public async Task<DumpStats> DumpAsync(IDbConnector connector, DbConnection conn, DumpOptions options,
            TextWriter output, IProgress<DumpProgress>? progress, CancellationToken ct = default)
        {
            var stats = new DumpStats();
            await SqliteDumper.WriteHeaderAsync(output, "PostgreSQL", options);

            // ---- sequences (before tables: serial defaults nextval() them) ----------
            var sequences = (await conn.QueryAsync<SequenceRow>(
                new CommandDefinition(SequencesSql, cancellationToken: ct))).ToList();
            foreach (var seq in sequences)
            {
                await SqliteDumper.WriteLineAsync(output, Environment.NewLine
                    + $"-- sequence: {seq.SchemaName}.{seq.Sequencename}");
                var quoted = connector.QuoteQualifiedName($"{seq.SchemaName}.{seq.Sequencename}");
                await SqliteDumper.WriteLineAsync(output, $"DROP SEQUENCE IF EXISTS {quoted};");
                await SqliteDumper.WriteLineAsync(output,
                    $"CREATE SEQUENCE {quoted} START WITH {seq.StartValue} INCREMENT BY {seq.IncrementBy}"
                    + $" MINVALUE {seq.MinValue} MAXVALUE {seq.MaxValue}"
                    + (seq.Cycle ? " CYCLE" : " NO CYCLE") + ";");
            }

            // ---- tables (DDL + data) --------------------------------------------------
            var tables = (await conn.QueryAsync<TableRow>(
                new CommandDefinition(TablesSql, cancellationToken: ct))).ToList();
            var total = tables.Count;
            var done = 0;
            foreach (var table in tables)
            {
                progress?.Report(new DumpProgress("Schema", $"{table.SchemaName}.{table.TableName}", 0, done, total));
                var quoted = connector.QuoteQualifiedName($"{table.SchemaName}.{table.TableName}");
                await WriteTableAsync(conn, output, connector, table, ct);

                if (options.IncludeData)
                {
                    var columns = (await conn.QueryAsync<(string Name, string IdentityKind)>(
                        new CommandDefinition(DataColumnsSql, new { oid = table.Oid }, cancellationToken: ct))).ToList();
                    var hasAlwaysIdentity = columns.Any(c => c.IdentityKind == "a");
                    var rows = await InsertWriter.WriteInsertsAsync(conn, connector, SqlDialect.Postgres,
                        quoted, columns.Select(c => connector.QuoteIdentifier(c.Name)).ToList(), output, options, progress, ct,
                        overridingSystemValue: hasAlwaysIdentity);
                    stats.Rows += rows;
                }
                done++;
            }
            stats.Tables = tables.Count;

            // ---- sequence ownership + counters (after data) ---------------------------
            var ownership = (await conn.QueryAsync<OwnerRow>(
                new CommandDefinition(SequenceOwnershipSql, cancellationToken: ct))).ToList();
            foreach (var own in ownership)
            {
                var seq = connector.QuoteQualifiedName($"{own.SchemaName}.{own.SeqName}");
                var col = connector.QuoteQualifiedName($"{own.TableSchema}.{own.TableName}") + "." + connector.QuoteIdentifier(own.ColName);
                await SqliteDumper.WriteLineAsync(output, $"ALTER SEQUENCE {seq} OWNED BY {col};");
            }
            foreach (var seq in sequences.Where(s => s.LastValue is not null))
            {
                var name = $"{seq.SchemaName}.{seq.Sequencename}".Replace("'", "''");
                await SqliteDumper.WriteLineAsync(output, $"SELECT setval('{name}', {seq.LastValue}, true);");
            }

            // ---- post-data: indexes, then FKs ------------------------------------------
            foreach (var table in tables)
            {
                var indexes = (await conn.QueryAsync<IndexDefRow>(
                    new CommandDefinition(IndexesSql, new { oid = table.Oid }, cancellationToken: ct))).ToList();
                if (indexes.Count == 0)
                    continue;
                await SqliteDumper.WriteLineAsync(output, Environment.NewLine
                    + $"-- indexes: {table.SchemaName}.{table.TableName}");
                foreach (var index in indexes)
                {
                    await SqliteDumper.WriteLineAsync(output,
                        $"DROP INDEX IF EXISTS {connector.QuoteQualifiedName($"{index.SchemaName}.{index.Name}")};");
                    await SqliteDumper.WriteLineAsync(output, index.Def.TrimEnd() + ";");
                    stats.Indexes++;
                }
            }

            await SqliteDumper.WriteLineAsync(output, Environment.NewLine + "-- foreign keys (after data: dependency order cannot fail)");
            var fks = (await conn.QueryAsync<FkDefRow>(
                new CommandDefinition(ForeignKeysSql, cancellationToken: ct))).ToList();
            foreach (var fk in fks)
            {
                // TableOid → back to a qualified name.
                var table = tables.FirstOrDefault(t => t.Oid == fk.TableOid);
                if (table is null)
                    continue;
                await SqliteDumper.WriteLineAsync(output,
                    $"ALTER TABLE {connector.QuoteQualifiedName($"{table.SchemaName}.{table.TableName}")}"
                    + $" ADD CONSTRAINT {connector.QuoteIdentifier(fk.Name)} {fk.Def};");
            }

            // ---- views (Kahn topological sort) ------------------------------------------
            var views = (await conn.QueryAsync<ViewRow>(
                new CommandDefinition(ViewsSql, cancellationToken: ct))).ToList();
            var dependencies = (await conn.QueryAsync<(string ViewSchema, string ViewName, string RefSchema, string RefName)>(
                new CommandDefinition(ViewDependenciesSql, cancellationToken: ct))).ToList();
            var orderedViews = SortViews(views, dependencies);
            foreach (var view in orderedViews)
            {
                progress?.Report(new DumpProgress("Schema", $"{view.SchemaName}.{view.ViewName}", 0, done, total));
                var quoted = connector.QuoteQualifiedName($"{view.SchemaName}.{view.ViewName}");
                var def = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                    "SELECT pg_get_viewdef(@oid::oid, true);", new { oid = view.Oid }, cancellationToken: ct));
                await SqliteDumper.WriteLineAsync(output, Environment.NewLine + $"-- view: {view.SchemaName}.{view.ViewName}");
                await SqliteDumper.WriteLineAsync(output, $"DROP VIEW IF EXISTS {quoted};");
                await SqliteDumper.WriteLineAsync(output, $"CREATE VIEW {quoted} AS{Environment.NewLine}{(def ?? string.Empty).TrimEnd()};");
                stats.Views++;
            }

            // ---- routines + triggers -----------------------------------------------------
            var routines = (await conn.QueryAsync<RoutineRow>(
                new CommandDefinition(RoutinesSql, cancellationToken: ct))).ToList();
            foreach (var routine in routines)
            {
                await SqliteDumper.WriteLineAsync(output, Environment.NewLine
                    + $"-- routine: {routine.SchemaName}.{routine.Name}");
                await output.WriteAsync(ScriptSplitter.RoutineStartMarker + "\n");
                await output.WriteAsync(routine.Def.TrimEnd() + ";\n");
                await output.WriteAsync(ScriptSplitter.RoutineEndMarker + "\n");
                stats.Routines++;
            }

            var triggers = (await conn.QueryAsync<(string SchemaName, string Def)>(
                new CommandDefinition(TriggersSql, cancellationToken: ct))).ToList();
            foreach (var (_, def) in triggers)
            {
                await output.WriteAsync(ScriptSplitter.RoutineStartMarker + "\n");
                await output.WriteAsync(def.TrimEnd() + ";\n");
                await output.WriteAsync(ScriptSplitter.RoutineEndMarker + "\n");
            }

            return stats;
        }

        /// <summary>
        /// Emits DROP + CREATE TABLE (columns, identity/generated, defaults,
        /// PK/unique/check/exclusion constraints inline). FKs are deliberately
        /// excluded here — they land after the data.
        /// </summary>
        private static async Task WriteTableAsync(DbConnection conn, TextWriter output, IDbConnector connector,
            TableRow table, CancellationToken ct)
        {
            var columns = (await conn.QueryAsync<ColumnRow>(
                new CommandDefinition(ColumnsSql, new { oid = table.Oid }, cancellationToken: ct))).ToList();
            var constraints = (await conn.QueryAsync<ConstraintRow>(
                new CommandDefinition(ConstraintsSql, new { oid = table.Oid }, cancellationToken: ct))).ToList();

            var quoted = connector.QuoteQualifiedName($"{table.SchemaName}.{table.TableName}");
            await SqliteDumper.WriteLineAsync(output, Environment.NewLine + $"-- table: {table.SchemaName}.{table.TableName}");
            await SqliteDumper.WriteLineAsync(output, $"DROP TABLE IF EXISTS {quoted};");
            await SqliteDumper.WriteLineAsync(output, $"CREATE TABLE {quoted} (");

            var parts = new List<string>();
            foreach (var col in columns)
            {
                var line = $"    {connector.QuoteIdentifier(col.Name)} {col.ColType}";
                if (!string.IsNullOrEmpty(col.IdentityKind))
                {
                    // 'a' = ALWAYS, 'd' = BY DEFAULT; the DEFAULT expr on such a
                    // column is the sequence nextval — the identity clause replaces it.
                    line += col.IdentityKind == "a"
                        ? " GENERATED ALWAYS AS IDENTITY"
                        : " GENERATED BY DEFAULT AS IDENTITY";
                }
                else if (!string.IsNullOrEmpty(col.GeneratedKind))
                {
                    line += $" GENERATED ALWAYS AS ({col.DefaultExpr}) STORED";
                }
                else if (!string.IsNullOrEmpty(col.DefaultExpr))
                {
                    line += $" DEFAULT {col.DefaultExpr}";
                }
                if (col.NotNull)
                    line += " NOT NULL";
                parts.Add(line);
            }

            foreach (var constraint in constraints.Where(c => c.Kind != "f"))
                parts.Add($"    CONSTRAINT {connector.QuoteIdentifier(constraint.Name)} {constraint.Def}");

            await SqliteDumper.WriteLineAsync(output, string.Join("," + Environment.NewLine, parts));
            await SqliteDumper.WriteLineAsync(output, ");");
        }

        /// <summary>
        /// Kahn topological sort so a view depending on a view is created after
        /// it. Cycles (possible via function indirection) fall back to name
        /// order for the remaining nodes.
        /// </summary>
        private static List<ViewRow> SortViews(List<ViewRow> views,
            List<(string ViewSchema, string ViewName, string RefSchema, string RefName)> dependencies)
        {
            var key = (string s, string n) => $"{s}.{n}";
            var remaining = views.ToDictionary(v => key(v.SchemaName, v.ViewName), v => v);
            var dependents = new Dictionary<string, HashSet<string>>();
            foreach (var (vs, vn, rs, rn) in dependencies)
            {
                var viewKey = key(vs, vn);
                var refKey = key(rs, rn);
                if (!remaining.ContainsKey(viewKey) || !remaining.ContainsKey(refKey) || viewKey == refKey)
                    continue;
                if (!dependents.TryGetValue(refKey, out var set))
                    dependents[refKey] = set = new HashSet<string>();
                set.Add(viewKey);
            }

            var result = new List<ViewRow>();
            while (remaining.Count > 0)
            {
                // Ready = no dependency on a view that is still pending.
                var ready = remaining.Values
                    .Where(v => !(dependents.TryGetValue(key(v.SchemaName, v.ViewName), out var deps)
                                  && deps.Any(d => remaining.ContainsKey(d))))
                    .OrderBy(v => key(v.SchemaName, v.ViewName))
                    .ToList();
                if (ready.Count == 0)
                    break; // cycle: emit the rest in name order
                foreach (var v in ready)
                {
                    result.Add(v);
                    remaining.Remove(key(v.SchemaName, v.ViewName));
                }
            }
            result.AddRange(remaining.Values.OrderBy(v => key(v.SchemaName, v.ViewName)));
            return result;
        }

        // ---------------------------------------------------------------- row shapes (Dapper maps by name)

        private sealed class SequenceRow
        {
            public string SchemaName { get; set; } = string.Empty;
            public string Sequencename { get; set; } = string.Empty;
            public long StartValue { get; set; }
            public long MinValue { get; set; }
            public long MaxValue { get; set; }
            public long IncrementBy { get; set; }
            public bool Cycle { get; set; }
            public long? LastValue { get; set; }
        }

        private sealed class OwnerRow
        {
            public string SchemaName { get; set; } = string.Empty;
            public string SeqName { get; set; } = string.Empty;
            public string TableSchema { get; set; } = string.Empty;
            public string TableName { get; set; } = string.Empty;
            public string ColName { get; set; } = string.Empty;
        }

        private sealed class TableRow
        {
            public string SchemaName { get; set; } = string.Empty;
            public string TableName { get; set; } = string.Empty;
            public long Oid { get; set; }
        }

        private sealed class ColumnRow
        {
            public string Name { get; set; } = string.Empty;
            public string ColType { get; set; } = string.Empty;
            public bool NotNull { get; set; }
            public string? IdentityKind { get; set; }
            public string? GeneratedKind { get; set; }
            public string? DefaultExpr { get; set; }
        }

        private sealed class ConstraintRow
        {
            public string Name { get; set; } = string.Empty;
            public string Kind { get; set; } = string.Empty;
            public string Def { get; set; } = string.Empty;
        }

        private sealed class IndexDefRow
        {
            public string SchemaName { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Def { get; set; } = string.Empty;
        }

        private sealed class FkDefRow
        {
            public string Name { get; set; } = string.Empty;
            public long TableOid { get; set; }
            public string Def { get; set; } = string.Empty;
        }

        private sealed class ViewRow
        {
            public string SchemaName { get; set; } = string.Empty;
            public string ViewName { get; set; } = string.Empty;
            public long Oid { get; set; }
        }

        private sealed class RoutineRow
        {
            public string SchemaName { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Def { get; set; } = string.Empty;
        }
    }
}