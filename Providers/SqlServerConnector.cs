using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using DbManager.Abstractions;
using DbManager.Models;
using Microsoft.Data.SqlClient;

namespace DbManager.Providers
{
    /// <summary>
    /// SQL Server / T-SQL. The schema queries are ported verbatim from the
    /// ZeatDashboard SqlEditorService (INFORMATION_SCHEMA + sys.*), extended with
    /// TABLE_SCHEMA so non-dbo objects get qualified names.
    /// </summary>
    public class SqlServerConnector : IDbConnector
    {
        public string EngineKey => "mssql";
        public string DisplayName => "SQL Server";
        public int DefaultPort => 1433;
        public bool SupportsGoBatches => true;
        public bool IsSelectIntoWrite => true;

        private static readonly HashSet<string> WriteKeywords = new(System.StringComparer.OrdinalIgnoreCase)
        {
            "UPDATE", "DELETE", "INSERT", "MERGE", "ALTER", "DROP", "TRUNCATE",
            "CREATE", "EXEC", "EXECUTE", "GRANT", "REVOKE", "DENY", "BACKUP", "RESTORE"
        };

        ISet<string> IDbConnector.WriteKeywords => WriteKeywords;

        public DialectConfig DialectConfig => new()
        {
            Quote = "[",
            OwnerHint = "dbo",
            TopSnippet = "SELECT TOP 20 *\nFROM "
        };

        public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

        public async Task TestConnectionAsync(string connectionString, CancellationToken ct = default)
        {
            await using var conn = CreateConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
        }

        public async Task<List<SchemaObject>> GetSchemaAsync(DbConnection conn, CancellationToken ct = default)
        {
            // One round trip per catalog object type, all against catalog views
            // (no sysadmin rights needed — INFORMATION_SCHEMA is readable by all).
            // TABLE_SCHEMA is kept so objects outside dbo can be name-qualified.
            const string tablesSql = @"
                SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_TYPE IN ('BASE TABLE', 'VIEW')
                ORDER BY TABLE_SCHEMA, TABLE_NAME;";
            const string columnsSql = @"
                SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE
                FROM INFORMATION_SCHEMA.COLUMNS
                ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION;";
            const string procsSql = @"
                SELECT s.name AS SchemaName, p.name AS Name
                FROM sys.procedures p
                JOIN sys.schemas s ON p.schema_id = s.schema_id
                ORDER BY p.name;";
            // Functions: FN = scalar, IF = inline table-valued, TF = table-valued,
            // AF = aggregate (rare, e.g. CLR). Names and parameters are separate
            // queries so a parameterless function still shows up (an INNER JOIN
            // on sys.parameters would drop it).
            const string funcsSql = @"
                SELECT s.name AS SchemaName, o.name AS Name
                FROM sys.objects o
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.type IN ('FN', 'IF', 'TF', 'AF')
                ORDER BY o.name;";
            const string funcParamsSql = @"
                SELECT s.name AS SchemaName, o.name AS FuncName, p.name AS ParamName, t.name AS DataType
                FROM sys.objects o
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                JOIN sys.parameters p ON p.object_id = o.object_id
                JOIN sys.types t ON t.user_type_id = p.user_type_id
                WHERE o.type IN ('FN', 'IF', 'TF', 'AF')
                  AND p.parameter_id >= 1
                ORDER BY o.name, p.parameter_id;";

            // Dapper's dynamic QueryAsync (no <T>) keeps the one-off row shapes
            // inline without dedicated model classes.
            var tables = await conn.QueryAsync<(string Schema, string Name, string Type)>(new CommandDefinition(tablesSql, cancellationToken: ct));
            var columns = await conn.QueryAsync<(string Schema, string Table, string Column, string DataType)>(new CommandDefinition(columnsSql, cancellationToken: ct));
            var procs = await conn.QueryAsync<(string Schema, string Name)>(new CommandDefinition(procsSql, cancellationToken: ct));
            var funcs = await conn.QueryAsync<(string Schema, string Name)>(new CommandDefinition(funcsSql, cancellationToken: ct));
            var funcParams = await conn.QueryAsync<(string Schema, string Func, string Param, string DataType)>(new CommandDefinition(funcParamsSql, cancellationToken: ct));

            // Columns grouped per table in one pass; the dictionary key is the
            // same qualified name assigned to the objects below.
            var columnsByTable = columns
                .GroupBy(c => Qualify(c.Schema, c.Table))
                .ToDictionary(g => g.Key, g => g.Select(c => new SchemaColumn
                {
                    Name = c.Column,
                    DataType = c.DataType
                }).ToList());

            var schema = new List<SchemaObject>();

            // dbo is the default schema: its objects get bare names (what a query
            // would usually write); anything else gets "schema.name".
            foreach (var (schemaName, name, type) in tables)
            {
                var kind = type == "BASE TABLE" ? SchemaObjectKind.Table : SchemaObjectKind.View;
                schema.Add(new SchemaObject
                {
                    Name = Qualify(schemaName, name),
                    Schema = schemaName,
                    Kind = kind,
                    // TryGetValue: a table with no discoverable columns shows an
                    // empty list instead of crashing.
                    Columns = columnsByTable.TryGetValue(Qualify(schemaName, name), out var cols) ? cols : []
                });
            }

            // Stored procedures have no entries in INFORMATION_SCHEMA.COLUMNS.
            // Name/Schema come from sys.schemas so non-dbo routines get the
            // same qualified-name treatment as non-dbo tables.
            schema.AddRange(procs.Select(p => new SchemaObject
            {
                Name = Qualify(p.Schema, p.Name),
                Schema = p.Schema,
                Kind = SchemaObjectKind.Procedure,
                Columns = []
            }));

            // Functions: parameters grouped per function, same dictionary pattern
            // as table columns — SchemaColumn is reused (name + type is all the
            // sidebar and IntelliSense need).
            var paramsByFunc = funcParams
                .GroupBy(p => Qualify(p.Schema, p.Func))
                .ToDictionary(g => g.Key, g => g.Select(p => new SchemaColumn
                {
                    Name = p.Param,
                    DataType = p.DataType
                }).ToList());

            schema.AddRange(funcs.Select(f => new SchemaObject
            {
                Name = Qualify(f.Schema, f.Name),
                Schema = f.Schema,
                Kind = SchemaObjectKind.Function,
                Columns = paramsByFunc.TryGetValue(Qualify(f.Schema, f.Name), out var ps) ? ps : []
            }));

            return schema;
        }

        /// <summary>Bare name inside the default schema, "schema.name" outside it.</summary>
        private static string Qualify(string schema, string name) =>
            schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? name : $"{schema}.{name}";

        public string QuoteIdentifier(string name) => $"[{name.Replace("]", "]]")}]";

        public string QuoteQualifiedName(string qualifiedName) =>
            string.Join(".", qualifiedName.Split('.').Select(QuoteIdentifier));

        public string BuildSelectTopN(string qualifiedName, int n) =>
            $"SELECT TOP {n} * FROM {QuoteQualifiedName(qualifiedName)}";

        public string BuildExecStub(string qualifiedName) =>
            $"EXEC {QuoteQualifiedName(qualifiedName)} ";

        public string BuildFunctionStub(string qualifiedName) =>
            $"SELECT dbo.{QuoteIdentifier(qualifiedName)}(";

        public string FormatSqlLiteral(object? value) =>
            ConnectorHelpers.FormatSqlLiteral(value, hexLiteral: true, nativeBool: false, unicodePrefix: true);

        public string BuildTruncateStatement(string quotedTable) =>
            ConnectorHelpers.TruncateStatement(quotedTable, supportsTruncate: true);

        public string BuildConnectionString(ConnectionFormData form)
        {
            var port = form.Port ?? DefaultPort;
            // TrustServerCertificate keeps quick-connect working against servers
            // with self-signed certificates — the common case for internal boxes.
            var cs = $"Server={ConnectorHelpers.EscapeCsValue(form.Host)},{port};";
            // Empty database = server-level profile with nothing picked yet:
            // omitting the segment lands on the login's default (master), like SSMS.
            if (!string.IsNullOrEmpty(form.Database))
                cs += $"Database={ConnectorHelpers.EscapeCsValue(form.Database)};";
            cs += $"User Id={ConnectorHelpers.EscapeCsValue(form.Username)};"
                + $"Password={ConnectorHelpers.EscapeCsValue(form.Password)};"
                + "Encrypt=True;TrustServerCertificate=True";
            return cs;
        }

        public bool SupportsServerLevel => true;

        public bool SupportsCrossDatabaseQueries => true;

        public string BuildServerConnectionString(ConnectionFormData form)
        {
            var port = form.Port ?? DefaultPort;
            return $"Server={ConnectorHelpers.EscapeCsValue(form.Host)},{port};"
                + $"User Id={ConnectorHelpers.EscapeCsValue(form.Username)};"
                + $"Password={ConnectorHelpers.EscapeCsValue(form.Password)};"
                + "Encrypt=True;TrustServerCertificate=True";
        }

        public async Task<List<string>> ListDatabasesAsync(DbConnection conn, CancellationToken ct = default)
        {
            // state = 0: ONLINE only; HAS_DBACCESS hides databases the login
            // has no entry in (SSMS shows the same filtered list).
            const string sql = @"
                SELECT name FROM sys.databases
                WHERE state = 0 AND HAS_DBACCESS(name) = 1
                ORDER BY name;";
            var dbs = await conn.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: ct));
            return dbs.ToList();
        }
    }
}