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

        public DbConnection CreateConnection(string connectionString) =>
            new SqlConnection(SanitizeConnectionString(connectionString));

        /// <summary>
        /// Keywords SSMS's connection-string box tolerates but
        /// Microsoft.Data.SqlClient rejects. Fed raw, any one of them makes the
        /// WHOLE string throw "Keyword not supported: '…'" at construction — the
        /// classic "works in SSMS, not here" case. Stripped before the string
        /// reaches SqlConnection. "Command Timeout" is the usual offender; the
        /// app controls command timeouts itself (SqlEditor:CommandTimeoutSeconds),
        /// so dropping it loses nothing.
        /// </summary>
        private static readonly HashSet<string> SsmsOnlyKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "Command Timeout"
        };

        /// <summary>
        /// Removes <see cref="SsmsOnlyKeywords"/> from a raw connection string,
        /// parsing with the generic DbConnectionStringBuilder (which accepts any
        /// keyword) and rebuilding without them. Anything else is passed through
        /// untouched — including an unparseable string, so the provider reports
        /// its own error rather than a silent rewrite.
        /// </summary>
        private static string SanitizeConnectionString(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return connectionString;

            DbConnectionStringBuilder builder;
            try
            {
                builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            }
            catch (ArgumentException)
            {
                return connectionString;
            }

            var removed = false;
            foreach (var key in builder.Keys.Cast<string>().ToList())
            {
                if (SsmsOnlyKeywords.Contains(key))
                {
                    builder.Remove(key);
                    removed = true;
                }
            }

            return removed ? builder.ConnectionString : connectionString;
        }

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

        /// <summary>
        /// T-SQL has no EXPLAIN: the estimated plan comes from SET SHOWPLAN_XML,
        /// which makes the following statement compile (not run) and return its
        /// plan as a result set. Each statement is its own command so SHOWPLAN
        /// stays the only statement in its batch (a T-SQL requirement); the
        /// sequence runs on one connection so the setting persists.
        /// </summary>
        public IReadOnlyList<string> BuildExplainCommands(string sql) =>
            ["SET SHOWPLAN_XML ON;", sql, "SET SHOWPLAN_XML OFF;"];

        public bool SupportsTableEditing => true;

        /// <summary>
        /// Column metadata via sys.* rather than INFORMATION_SCHEMA: identity and
        /// primary-key membership are not exposed by INFORMATION_SCHEMA.COLUMNS.
        /// The qualified name is split on its last dot (bare → dbo); QUOTENAME +
        /// OBJECT_ID resolves it safely even for names with spaces or dots.
        /// </summary>
        public async Task<List<TableColumnInfo>> GetTableColumnsAsync(DbConnection conn, string qualifiedName, CancellationToken ct = default)
        {
            var lastDot = qualifiedName.LastIndexOf('.');
            var schema = lastDot < 0 ? "dbo" : qualifiedName[..lastDot];
            var table = lastDot < 0 ? qualifiedName : qualifiedName[(lastDot + 1)..];

            const string sql = @"
                SELECT c.name AS Name,
                       ty.name AS DataType,
                       CAST(c.is_nullable AS bit) AS IsNullable,
                       CAST(c.is_identity AS bit) AS IsIdentity,
                       CAST(CASE WHEN pk.name IS NULL THEN 0 ELSE 1 END AS bit) AS IsPrimaryKey
                FROM sys.columns c
                JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                LEFT JOIN (
                    SELECT ic.name
                    FROM sys.indexes i
                    JOIN sys.index_columns ix ON ix.object_id = i.object_id AND ix.index_id = i.index_id
                    JOIN sys.columns ic ON ic.object_id = ix.object_id AND ic.column_id = ix.column_id
                    WHERE i.is_primary_key = 1
                      AND i.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
                ) pk ON pk.name = c.name
                WHERE c.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
                ORDER BY c.column_id;";

            var rows = await conn.QueryAsync<(string Name, string DataType, bool IsNullable, bool IsIdentity, bool IsPrimaryKey)>(
                new CommandDefinition(sql, new { schema, table }, cancellationToken: ct));

            return rows.Select(r => new TableColumnInfo
            {
                Name = r.Name,
                DataType = r.DataType,
                IsNullable = r.IsNullable,
                IsIdentity = r.IsIdentity,
                IsPrimaryKey = r.IsPrimaryKey
            }).ToList();
        }

        public string FormatSqlLiteral(object? value) =>
            ConnectorHelpers.FormatSqlLiteral(value, hexLiteral: true, nativeBool: false, unicodePrefix: true);

        public string BuildTruncateStatement(string quotedTable) =>
            ConnectorHelpers.TruncateStatement(quotedTable, supportsTruncate: true);

        public string BuildConnectionString(ConnectionFormData form)
        {
            var port = form.Port ?? DefaultPort;
            var cs = $"Server={ConnectorHelpers.EscapeCsValue(form.Host)},{port};";
            // Empty database = server-level profile with nothing picked yet:
            // omitting the segment lands on the login's default (master), like SSMS.
            if (!string.IsNullOrEmpty(form.Database))
                cs += $"Database={ConnectorHelpers.EscapeCsValue(form.Database)};";
            cs += $"User Id={ConnectorHelpers.EscapeCsValue(form.Username)};"
                + $"Password={ConnectorHelpers.EscapeCsValue(form.Password)};"
                + SqlServerEncryption.ToConnectionStringSegments(form.EncryptMode);
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
                + SqlServerEncryption.ToConnectionStringSegments(form.EncryptMode);
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