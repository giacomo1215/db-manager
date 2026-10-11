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
    /// SQL Server dump: no SHOW CREATE exists, so CREATE TABLE is composed from
    /// the sys.* catalog (identity, defaults, computed columns, PK/unique
    /// inline; CHECKs inline when single-column). Foreign keys are deferred to
    /// ALTER TABLE after all tables AND data — dependency order then cannot
    /// fail, matching how SSMS scripts dependencies. Views/routines/triggers
    /// come from sys.sql_modules, each in its own GO batch (T-SQL requires
    /// CREATE VIEW/PROC/FUNCTION/TRIGGER to lead a batch), ordered by
    /// create_date as a dependency heuristic.
    /// </summary>
    public class SqlServerDumper : IDbDumper
    {
        public string EngineKey => "mssql";

        private const string TablesSql = """
            SELECT s.name AS SchemaName, t.name AS TableName, t.object_id AS Oid
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name;
            """;

        private const string ColumnsSql = """
            SELECT c.name AS Name, tp.name AS TypeName, c.max_length AS MaxLength, c.[precision] AS Prec, c.scale AS ScaleVal,
                   c.is_nullable AS IsNullable, c.is_computed AS IsComputed,
                   CAST(ic.seed_value AS bigint) AS SeedValue, CAST(ic.increment_value AS int) AS IncrementValue,
                   cc.definition AS ComputedDef, OBJECT_DEFINITION(c.default_object_id) AS DefaultDef
            FROM sys.columns c
            JOIN sys.types tp ON tp.user_type_id = c.user_type_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            WHERE c.object_id = @oid
            ORDER BY c.column_id;
            """;

        private const string KeyConstraintsSql = """
            SELECT kc.name AS Name, kc.type AS Kind, i.type AS IndexType, i.index_id AS IndexId
            FROM sys.key_constraints kc
            JOIN sys.indexes i ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
            WHERE kc.parent_object_id = @oid;
            """;

        private const string IndexColumnsSql = """
            SELECT c.name AS Name, ic.is_descending_key AS Descending, ic.is_included_column AS Included
            FROM sys.index_columns ic
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE ic.object_id = @oid AND ic.index_id = @iid
            ORDER BY CASE WHEN ic.key_ordinal = 0 THEN 1 ELSE 0 END, ic.key_ordinal, ic.index_column_id;
            """;

        private const string CheckConstraintsSql = """
            SELECT name AS Name, definition AS Definition, parent_column_id AS ParentColumnId
            FROM sys.check_constraints WHERE parent_object_id = @oid;
            """;

        private const string IndexesSql = """
            SELECT i.name AS Name, i.is_unique AS IsUnique, i.type AS IndexType,
                   i.has_filter AS HasFilter, i.filter_definition AS FilterDefinition, i.index_id AS Iid
            FROM sys.indexes i
            WHERE i.object_id = @oid AND i.is_primary_key = 0 AND i.is_unique_constraint = 0
              AND i.index_id > 0 AND i.name IS NOT NULL;
            """;

        private const string ForeignKeysSql = """
            SELECT fk.name AS Name, fk.object_id AS FkId,
                   OBJECT_SCHEMA_NAME(fk.parent_object_id) AS ParentSchema, OBJECT_NAME(fk.parent_object_id) AS ParentName,
                   OBJECT_SCHEMA_NAME(fk.referenced_object_id) AS RefSchema, OBJECT_NAME(fk.referenced_object_id) AS RefName,
                   fk.delete_referential_action AS OnDelete, fk.update_referential_action AS OnUpdate
            FROM sys.foreign_keys fk
            WHERE fk.parent_object_id IN (SELECT object_id FROM sys.tables WHERE is_ms_shipped = 0)
            ORDER BY fk.name;
            """;

        private const string FkColumnsSql = """
            SELECT pc.name AS ParentCol, rc.name AS RefCol
            FROM sys.foreign_key_columns fkc
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE fkc.constraint_object_id = @fkid
            ORDER BY fkc.constraint_column_id;
            """;

        private const string ModulesSql = """
            SELECT s.name AS SchemaName, o.name AS Name, o.type AS ObjType, sm.definition AS Definition
            FROM sys.sql_modules sm
            JOIN sys.objects o ON o.object_id = sm.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type = {0} AND o.is_ms_shipped = 0
            ORDER BY o.create_date, o.name;
            """;

        private const string BatchPreamble = "SET NOCOUNT ON;\nSET QUOTED_IDENTIFIER ON;\nSET ANSI_NULLS ON;\n";

        public async Task<DumpStats> DumpAsync(IDbConnector connector, DbConnection conn, DumpOptions options,
            TextWriter output, IProgress<DumpProgress>? progress, CancellationToken ct = default)
        {
            var stats = new DumpStats();
            var q = connector.QuoteIdentifier;
            var tables = (await conn.QueryAsync<(string SchemaName, string TableName, int Oid)>(
                new CommandDefinition(TablesSql, cancellationToken: ct))).ToList();
            var total = tables.Count;

            await SqliteDumper.WriteHeaderAsync(output, "SQL Server", options);
            await SqliteDumper.WriteLineAsync(output, BatchPreamble.TrimEnd());
            await GoAsync(output);

            var done = 0;
            foreach (var (schemaName, tableName, oid) in tables)
            {
                progress?.Report(new DumpProgress("Schema", $"{schemaName}.{tableName}", 0, done, total));
                var quoted = connector.QuoteQualifiedName($"{schemaName}.{tableName}");

                await SqliteDumper.WriteLineAsync(output, Environment.NewLine + $"-- table: {schemaName}.{tableName}");
                await SqliteDumper.WriteLineAsync(output, $"DROP TABLE IF EXISTS {quoted};");

                await WriteCreateTableAsync(conn, output, connector, schemaName, tableName, oid, ct);

                var hasIdentity = await HasIdentityAsync(conn, oid, ct);
                if (options.IncludeData)
                {
                    var colNames = await GetColumnNamesAsync(conn, connector, oid, ct);
                    if (hasIdentity)
                        await SqliteDumper.WriteLineAsync(output, $"SET IDENTITY_INSERT {quoted} ON;");
                    var rows = await InsertWriter.WriteInsertsAsync(conn, connector, SqlDialect.MsSql,
                        quoted, colNames, output, options, progress, ct);
                    stats.Rows += rows;
                    if (hasIdentity)
                        await SqliteDumper.WriteLineAsync(output, $"SET IDENTITY_INSERT {quoted} OFF;");
                }
                await GoAsync(output);
                done++;
            }

            // Non-constraint indexes AFTER data (faster restore, no order hazard).
            foreach (var (schemaName, tableName, oid) in tables)
            {
                stats.Indexes += await WriteIndexesAsync(conn, output, connector, schemaName, tableName, oid, ct);
            }
            stats.Tables = tables.Count;

            if (options.IncludeData)
                await WriteForeignKeysAsync(conn, output, connector, ct);

            // Module-backed objects, each its own GO batch (T-SQL batch rule).
            stats.Views = await WriteModulesAsync(conn, output, connector, "V", "VIEW", ct);
            stats.Routines = await WriteModulesAsync(conn, output, connector, "P", "PROCEDURE", ct)
                + await WriteModulesAsync(conn, output, connector, "FN", "FUNCTION", ct)
                + await WriteModulesAsync(conn, output, connector, "IF", "FUNCTION", ct)
                + await WriteModulesAsync(conn, output, connector, "TF", "FUNCTION", ct)
                + await WriteModulesAsync(conn, output, connector, "TR", "TRIGGER", ct);
            return stats;
        }

        // ---------------------------------------------------------------- CREATE TABLE

        private async Task WriteCreateTableAsync(DbConnection conn, TextWriter output, IDbConnector connector,
            string schemaName, string tableName, int oid, CancellationToken ct)
        {
            var columns = (await conn.QueryAsync<ColumnRow>(
                new CommandDefinition(ColumnsSql, new { oid }, cancellationToken: ct))).ToList();
            var keys = (await conn.QueryAsync<KeyRow>(
                new CommandDefinition(KeyConstraintsSql, new { oid }, cancellationToken: ct))).ToList();
            var checks = (await conn.QueryAsync<CheckRow>(
                new CommandDefinition(CheckConstraintsSql, new { oid }, cancellationToken: ct))).ToList();

            await SqliteDumper.WriteLineAsync(output, $"CREATE TABLE {connector.QuoteQualifiedName($"{schemaName}.{tableName}")} (");

            var parts = new List<string>();
            foreach (var col in columns)
            {
                var line = $"    {connector.QuoteIdentifier(col.Name)} ";
                if (col.IsComputed)
                {
                    parts.Add(line + $"AS {col.ComputedDef}");
                    continue;
                }
                line += RenderType(col);
                if (col.SeedValue is { } seed)
                    line += $" IDENTITY({seed},{col.IncrementValue})";
                line += col.IsNullable ? " NULL" : " NOT NULL";
                if (!string.IsNullOrEmpty(col.DefaultDef))
                    line += $" DEFAULT {col.DefaultDef}";
                parts.Add(line);
            }

            foreach (var key in keys)
            {
                var cols = await IndexColumnsAsync(conn, oid, key.IndexId, ct);
                var colList = string.Join(", ", cols.Select(c => connector.QuoteIdentifier(c.Name) + (c.Descending ? " DESC" : "")));
                var clustered = key.IndexType == 1 ? "CLUSTERED" : "NONCLUSTERED";
                var kind = key.Kind == "PK" ? "PRIMARY KEY" : "UNIQUE";
                parts.Add($"    CONSTRAINT {connector.QuoteIdentifier(key.Name)} {kind} {clustered} ({colList})");
            }

            // Single-column CHECKs inline; table-level ones are deferred to ALTER.
            foreach (var check in checks.Where(c => c.ParentColumnId > 0))
                parts.Add($"    CONSTRAINT {connector.QuoteIdentifier(check.Name)} CHECK {check.Definition}");

            await SqliteDumper.WriteLineAsync(output, string.Join("," + Environment.NewLine, parts));
            await SqliteDumper.WriteLineAsync(output, ");");

            foreach (var check in checks.Where(c => c.ParentColumnId == 0))
                await SqliteDumper.WriteLineAsync(output,
                    $"ALTER TABLE {connector.QuoteQualifiedName($"{schemaName}.{tableName}")} WITH CHECK ADD CONSTRAINT {connector.QuoteIdentifier(check.Name)} CHECK {check.Definition};");
        }

        private static string RenderType(ColumnRow col)
        {
            switch (col.TypeName)
            {
                case "char" or "varchar" or "binary" or "varbinary":
                    return $"{col.TypeName}({(col.MaxLength == -1 ? "MAX" : col.MaxLength.ToString())})";
                case "nchar" or "nvarchar":
                    return $"{col.TypeName}({(col.MaxLength == -1 ? "MAX" : (col.MaxLength / 2).ToString())})";
                case "decimal" or "numeric":
                    return $"{col.TypeName}({col.Prec},{col.ScaleVal})";
                case "datetime2" or "datetimeoffset" or "time":
                    return $"{col.TypeName}({col.ScaleVal})";
                case "float":
                    return col.Prec < 53 ? $"float({col.Prec})" : "float";
                default:
                    // int, bigint, bit, date, datetime, money, real, text, xml,
                    // uniqueidentifier, rowversion, … take no length arguments.
                    return col.TypeName;
            }
        }

        private async Task<int> WriteIndexesAsync(DbConnection conn, TextWriter output, IDbConnector connector,
            string schemaName, string tableName, int oid, CancellationToken ct)
        {
            var indexes = (await conn.QueryAsync<IndexRow>(
                new CommandDefinition(IndexesSql, new { oid }, cancellationToken: ct))).ToList();
            if (indexes.Count == 0)
                return 0;
            var written = 0;

            await SqliteDumper.WriteLineAsync(output, Environment.NewLine + $"-- indexes: {schemaName}.{tableName}");
            var quotedTable = connector.QuoteQualifiedName($"{schemaName}.{tableName}");
            foreach (var index in indexes)
            {
                var cols = await IndexColumnsAsync(conn, oid, index.Iid, ct);
                var keys = cols.Where(c => !c.Included).ToList();
                var included = cols.Where(c => c.Included).ToList();
                if (keys.Count == 0)
                    continue; // XML/spatial indexes have no key columns: skipped (documented).
                written++;

                var unique = index.IsUnique ? "UNIQUE " : string.Empty;
                var clustered = index.IndexType == 1 ? "CLUSTERED " : string.Empty;
                var line = $"CREATE {unique}{clustered}INDEX {connector.QuoteIdentifier(index.Name)} ON {quotedTable} ("
                    + string.Join(", ", keys.Select(c => connector.QuoteIdentifier(c.Name) + (c.Descending ? " DESC" : ""))) + ")";
                if (included.Count > 0)
                    line += " INCLUDE (" + string.Join(", ", included.Select(c => connector.QuoteIdentifier(c.Name))) + ")";
                if (index.HasFilter && !string.IsNullOrEmpty(index.FilterDefinition))
                    line += $" WHERE {index.FilterDefinition}";

                await SqliteDumper.WriteLineAsync(output, $"DROP INDEX IF EXISTS {connector.QuoteIdentifier(index.Name)} ON {quotedTable};");
                await SqliteDumper.WriteLineAsync(output, line + ";");
                await SqliteDumper.WriteLineAsync(output, "GO");
            }
            return written;
        }

        private async Task WriteForeignKeysAsync(DbConnection conn, TextWriter output, IDbConnector connector, CancellationToken ct)
        {
            var fks = (await conn.QueryAsync<FkRow>(
                new CommandDefinition(ForeignKeysSql, cancellationToken: ct))).ToList();
            if (fks.Count == 0)
                return;

            await SqliteDumper.WriteLineAsync(output, Environment.NewLine + "-- foreign keys (after data: dependency order cannot fail)");
            foreach (var fk in fks)
            {
                var cols = (await conn.QueryAsync<(string ParentCol, string RefCol)>(
                    new CommandDefinition(FkColumnsSql, new { fkid = fk.FkId }, cancellationToken: ct))).ToList();
                await SqliteDumper.WriteLineAsync(output,
                    $"ALTER TABLE {connector.QuoteQualifiedName($"{fk.ParentSchema}.{fk.ParentName}")} WITH CHECK ADD CONSTRAINT {connector.QuoteIdentifier(fk.Name)}"
                    + $" FOREIGN KEY ({string.Join(", ", cols.Select(c => connector.QuoteIdentifier(c.ParentCol)))})"
                    + $" REFERENCES {connector.QuoteQualifiedName($"{fk.RefSchema}.{fk.RefName}")} ({string.Join(", ", cols.Select(c => connector.QuoteIdentifier(c.RefCol)))})"
                    + ReferentialAction(" ON DELETE", fk.OnDelete) + ReferentialAction(" ON UPDATE", fk.OnUpdate) + ";");
            }
            await SqliteDumper.WriteLineAsync(output, "GO");
        }

        private static string ReferentialAction(string prefix, int action) => action switch
        {
            1 => $"{prefix} CASCADE",
            2 => $"{prefix} SET NULL",
            3 => $"{prefix} SET DEFAULT",
            _ => string.Empty // 0 = NO ACTION, the default
        };

        private async Task<int> WriteModulesAsync(DbConnection conn, TextWriter output, IDbConnector connector,
            string type, string dropKeyword, CancellationToken ct)
        {
            var modules = (await conn.QueryAsync<(string SchemaName, string Name, string Definition)>(
                new CommandDefinition(string.Format(ModulesSql, $"'{type}'"), cancellationToken: ct))).ToList();
            foreach (var (schemaName, name, definition) in modules)
            {
                var qualified = connector.QuoteQualifiedName($"{schemaName}.{name}");
                await SqliteDumper.WriteLineAsync(output, Environment.NewLine + $"-- {dropKeyword.ToLowerInvariant()}: {schemaName}.{name}");
                await SqliteDumper.WriteLineAsync(output, "SET NOCOUNT ON;");
                await SqliteDumper.WriteLineAsync(output, "SET QUOTED_IDENTIFIER ON;");
                await SqliteDumper.WriteLineAsync(output, "SET ANSI_NULLS ON;");
                await SqliteDumper.WriteLineAsync(output, $"DROP {dropKeyword} IF EXISTS {qualified};");
                await SqliteDumper.WriteLineAsync(output, definition.TrimEnd());
                await GoAsync(output);
            }
            return modules.Count;
        }

        // ---------------------------------------------------------------- small helpers

        private Task GoAsync(TextWriter output) => SqliteDumper.WriteLineAsync(output, "GO");

        private async Task<bool> HasIdentityAsync(DbConnection conn, int oid, CancellationToken ct) =>
            await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT 1 FROM sys.identity_columns WHERE object_id = @oid;", new { oid }, cancellationToken: ct)) == 1;

        private async Task<List<string>> GetColumnNamesAsync(DbConnection conn, IDbConnector connector, int oid, CancellationToken ct)
        {
            var names = await conn.QueryAsync<string>(new CommandDefinition(
                "SELECT c.name FROM sys.columns c WHERE c.object_id = @oid AND c.is_computed = 0 ORDER BY c.column_id;",
                new { oid }, cancellationToken: ct));
            return names.Select(connector.QuoteIdentifier).ToList();
        }

        private async Task<List<IndexColumnRow>> IndexColumnsAsync(DbConnection conn, int oid, int iid, CancellationToken ct)
        {
            var cols = await conn.QueryAsync<IndexColumnRow>(
                new CommandDefinition(IndexColumnsSql, new { oid, iid }, cancellationToken: ct));
            return cols.ToList();
        }

        // ---------------------------------------------------------------- row shapes (Dapper maps by name)

        private sealed class ColumnRow
        {
            public string Name { get; set; } = string.Empty;
            public string TypeName { get; set; } = string.Empty;
            public short MaxLength { get; set; }
            public byte Prec { get; set; }
            public byte ScaleVal { get; set; }
            public bool IsNullable { get; set; }
            public bool IsComputed { get; set; }
            public long? SeedValue { get; set; }
            public int? IncrementValue { get; set; }
            public string? ComputedDef { get; set; }
            public string? DefaultDef { get; set; }
        }

        private sealed class KeyRow
        {
            public string Name { get; set; } = string.Empty;
            public string Kind { get; set; } = string.Empty; // PK / UQ
            public int IndexType { get; set; }               // 1 clustered, 2 nonclustered
            public int IndexId { get; set; }
        }

        private sealed class CheckRow
        {
            public string Name { get; set; } = string.Empty;
            public string Definition { get; set; } = string.Empty;
            public int ParentColumnId { get; set; }
        }

        private sealed class IndexRow
        {
            public string Name { get; set; } = string.Empty;
            public bool IsUnique { get; set; }
            public int IndexType { get; set; }
            public bool HasFilter { get; set; }
            public string? FilterDefinition { get; set; }
            public int Iid { get; set; }
        }

        private sealed class IndexColumnRow
        {
            public string Name { get; set; } = string.Empty;
            public bool Descending { get; set; }
            public bool Included { get; set; }
        }

        private sealed class FkRow
        {
            public string Name { get; set; } = string.Empty;
            public int FkId { get; set; }
            public string ParentSchema { get; set; } = string.Empty;
            public string ParentName { get; set; } = string.Empty;
            public string RefSchema { get; set; } = string.Empty;
            public string RefName { get; set; } = string.Empty;
            public int OnDelete { get; set; }
            public int OnUpdate { get; set; }
        }
    }
}