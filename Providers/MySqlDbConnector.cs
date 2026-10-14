using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using DbManager.Abstractions;
using DbManager.Models;
using MySqlConnector;

namespace DbManager.Providers
{
    /// <summary>
    /// MySQL / MariaDB. Every catalog query is scoped to DATABASE() so a shared
    /// host never triggers a server-wide information_schema scan or lists
    /// objects of other databases. Class name avoids clashing with the
    /// MySqlConnector package namespace.
    /// </summary>
    public class MySqlDbConnector : IDbConnector
    {
        public string EngineKey => "mysql";
        public string DisplayName => "MySQL / MariaDB";
        public int DefaultPort => 3306;
        public bool SupportsGoBatches => false;
        public bool IsSelectIntoWrite => false;

        private static readonly HashSet<string> WriteKeywords = ConnectorHelpers.CoreWriteKeywords();

        static MySqlDbConnector()
        {
            // CALL is MySQL's procedure invocation and procedures may write.
            WriteKeywords.Add("CALL");
        }

        ISet<string> IDbConnector.WriteKeywords => WriteKeywords;

        public DialectConfig DialectConfig => new()
        {
            Quote = "`",
            OwnerHint = null,
            TopSnippet = "SELECT *\nFROM ${1:table}\nLIMIT 20"
        };

        public DbConnection CreateConnection(string connectionString) =>
            new MySqlConnection(NormalizeConnectionString(connectionString));

        /// <summary>
        /// MySqlConnector takes Keyword=Value strings, but cloud providers hand out
        /// mysql://user:pass@host:port/db URIs. URIs are converted here; anything
        /// else passes through.
        /// </summary>
        private static string NormalizeConnectionString(string connectionString)
        {
            if (!ConnectorHelpers.TryParseConnectionUri(connectionString, out var uri) || uri is null)
                return connectionString;

            var parts = new List<string>
            {
                $"Server={ConnectorHelpers.EscapeCsValue(uri.Host)}",
                $"Port={uri.Port ?? 3306}"
            };
            if (uri.User is not null)
                parts.Add($"User ID={ConnectorHelpers.EscapeCsValue(uri.User)}");
            if (uri.Password is not null)
                parts.Add($"Password={ConnectorHelpers.EscapeCsValue(uri.Password)}");
            if (uri.Database is not null)
                parts.Add($"Database={ConnectorHelpers.EscapeCsValue(uri.Database)}");

            foreach (var (key, value) in uri.Query)
            {
                var mapped = key.ToLowerInvariant() switch
                {
                    "sslmode" => "SSL Mode",
                    "connect_timeout" => "Connection Timeout",
                    "application_name" => "Application Name",
                    _ => null
                };
                if (mapped is not null)
                    parts.Add($"{mapped}={ConnectorHelpers.EscapeCsValue(value)}");
            }

            return string.Join(";", parts);
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
            const string tablesSql = @"
                SELECT TABLE_NAME, TABLE_TYPE
                FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE IN ('BASE TABLE', 'VIEW')
                ORDER BY TABLE_NAME;";
            const string columnsSql = @"
                SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                ORDER BY TABLE_NAME, ORDINAL_POSITION;";
            const string routinesSql = @"
                SELECT ROUTINE_NAME, ROUTINE_TYPE
                FROM information_schema.ROUTINES
                WHERE ROUTINE_SCHEMA = DATABASE() AND ROUTINE_TYPE IN ('PROCEDURE', 'FUNCTION')
                ORDER BY ROUTINE_NAME;";
            // FUNCTION parameters only (procedure CALL stubs take no placeholder list).
            const string funcParamsSql = @"
                SELECT SPECIFIC_NAME, PARAMETER_NAME, DTD_IDENTIFIER
                FROM information_schema.PARAMETERS
                WHERE SPECIFIC_SCHEMA = DATABASE() AND ROUTINE_TYPE = 'FUNCTION' AND ORDINAL_POSITION >= 1
                ORDER BY SPECIFIC_NAME, ORDINAL_POSITION;";

            var tables = await conn.QueryAsync<(string Name, string Type)>(new CommandDefinition(tablesSql, cancellationToken: ct));
            var columns = await conn.QueryAsync<(string Table, string Column, string DataType)>(new CommandDefinition(columnsSql, cancellationToken: ct));
            var routines = await conn.QueryAsync<(string Name, string Type)>(new CommandDefinition(routinesSql, cancellationToken: ct));
            var funcParams = await conn.QueryAsync<(string Func, string Param, string DataType)>(new CommandDefinition(funcParamsSql, cancellationToken: ct));

            var columnsByTable = columns
                .GroupBy(c => c.Table)
                .ToDictionary(g => g.Key, g => g.Select(c => new SchemaColumn
                {
                    Name = c.Column,
                    DataType = c.DataType
                }).ToList());

            var schema = new List<SchemaObject>();
            foreach (var (name, type) in tables)
            {
                schema.Add(new SchemaObject
                {
                    Name = name,
                    Kind = type == "BASE TABLE" ? SchemaObjectKind.Table : SchemaObjectKind.View,
                    Columns = columnsByTable.TryGetValue(name, out var cols) ? cols : []
                });
            }

            var paramsByFunc = funcParams
                .GroupBy(p => p.Func)
                .ToDictionary(g => g.Key, g => g.Select(p => new SchemaColumn
                {
                    Name = p.Param,
                    DataType = p.DataType
                }).ToList());

            foreach (var (name, type) in routines)
            {
                var isFunction = type == "FUNCTION";
                schema.Add(new SchemaObject
                {
                    Name = name,
                    Kind = isFunction ? SchemaObjectKind.Function : SchemaObjectKind.Procedure,
                    Columns = isFunction && paramsByFunc.TryGetValue(name, out var ps) ? ps : []
                });
            }

            return schema;
        }

        public string QuoteIdentifier(string name) => $"`{name.Replace("`", "``")}`";

        public string QuoteQualifiedName(string qualifiedName) =>
            string.Join(".", qualifiedName.Split('.').Select(QuoteIdentifier));

        public string BuildSelectTopN(string qualifiedName, int n) =>
            $"SELECT * FROM {QuoteQualifiedName(qualifiedName)} LIMIT {n}";

        public string BuildExecStub(string qualifiedName) => $"CALL {QuoteQualifiedName(qualifiedName)}(";

        public string BuildFunctionStub(string qualifiedName) => $"SELECT {QuoteQualifiedName(qualifiedName)}(";

        public string FormatSqlLiteral(object? value) =>
            ConnectorHelpers.FormatSqlLiteral(value, hexLiteral: false, nativeBool: false, unicodePrefix: false);

        public string BuildTruncateStatement(string quotedTable) =>
            ConnectorHelpers.TruncateStatement(quotedTable, supportsTruncate: true);

        public string BuildConnectionString(ConnectionFormData form)
        {
            var port = form.Port ?? DefaultPort;
            var cs = $"Server={ConnectorHelpers.EscapeCsValue(form.Host)};Port={port};";
            // Empty database = server-level profile: the server default DB applies.
            if (!string.IsNullOrEmpty(form.Database))
                cs += $"Database={ConnectorHelpers.EscapeCsValue(form.Database)};";
            cs += $"User ID={ConnectorHelpers.EscapeCsValue(form.Username)};"
                + $"Password={ConnectorHelpers.EscapeCsValue(form.Password)}";
            return cs;
        }

        public bool SupportsServerLevel => true;

        public bool SupportsCrossDatabaseQueries => true;

        public string BuildServerConnectionString(ConnectionFormData form)
        {
            var port = form.Port ?? DefaultPort;
            return $"Server={ConnectorHelpers.EscapeCsValue(form.Host)};Port={port};"
                + $"User ID={ConnectorHelpers.EscapeCsValue(form.Username)};"
                + $"Password={ConnectorHelpers.EscapeCsValue(form.Password)}";
        }

        public async Task<List<string>> ListDatabasesAsync(DbConnection conn, CancellationToken ct = default)
        {
            // information_schema.SCHEMATA is grant-filtered: a restricted user
            // only sees databases they may access.
            const string sql = @"
                SELECT SCHEMA_NAME FROM information_schema.SCHEMATA
                ORDER BY SCHEMA_NAME;";
            var dbs = await conn.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: ct));
            return dbs.ToList();
        }
    }
}