using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using DbManager.Abstractions;
using DbManager.Models;
using Npgsql;

namespace DbManager.Providers
{
    /// <summary>
    /// PostgreSQL. Procedures come from pg_proc (NOT information_schema.routines,
    /// which only lists routines the current user may execute) with prokind:
    /// 'f' = function, 'p' = procedure, 'w' = window function. Objects in the
    /// default "public" schema get bare names; anything else "schema.name".
    /// </summary>
    public class PostgreSqlConnector : IDbConnector
    {
        public string EngineKey => "postgres";
        public string DisplayName => "PostgreSQL";
        public int DefaultPort => 5432;
        public bool SupportsGoBatches => false;
        public bool IsSelectIntoWrite => false;

        private static readonly HashSet<string> WriteKeywords = ConnectorHelpers.CoreWriteKeywords();

        ISet<string> IDbConnector.WriteKeywords => WriteKeywords;

        public DialectConfig DialectConfig => new()
        {
            Quote = "\"",
            OwnerHint = null,
            TopSnippet = "SELECT *\nFROM ${1:table}\nLIMIT 20"
        };

        public DbConnection CreateConnection(string connectionString) =>
            new NpgsqlConnection(NormalizeConnectionString(connectionString));

        /// <summary>
        /// Npgsql takes Keyword=Value strings, but cloud providers (Supabase, Neon, …)
        /// hand out postgres://user:pass@host:port/db URIs — fed to Npgsql raw, those
        /// fail with "Format of the initialization string does not conform to
        /// specification". URIs are converted here; anything else passes through.
        /// </summary>
        private static string NormalizeConnectionString(string connectionString)
        {
            if (!ConnectorHelpers.TryParseConnectionUri(connectionString, out var uri) || uri is null)
                return connectionString;

            var parts = new List<string>
            {
                $"Host={ConnectorHelpers.EscapeCsValue(uri.Host)}",
                $"Port={uri.Port ?? 5432}"
            };
            if (uri.User is not null)
                parts.Add($"Username={ConnectorHelpers.EscapeCsValue(uri.User)}");
            if (uri.Password is not null)
                parts.Add($"Password={ConnectorHelpers.EscapeCsValue(uri.Password)}");
            if (uri.Database is not null)
                parts.Add($"Database={ConnectorHelpers.EscapeCsValue(uri.Database)}");

            // URI query options map onto Npgsql keywords; unknown ones are dropped
            // (better a clear Npgsql error about a missing option than a parse crash).
            foreach (var (key, value) in uri.Query)
            {
                var mapped = key.ToLowerInvariant() switch
                {
                    "sslmode" => "SSL Mode",
                    "connect_timeout" => "Timeout",
                    "application_name" => "Application Name",
                    "client_encoding" => "Client Encoding",
                    "load_balance_hosts" => "Load Balance Hosts",
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
                SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
                FROM information_schema.TABLES
                WHERE TABLE_SCHEMA NOT IN ('pg_catalog', 'information_schema')
                  AND TABLE_TYPE IN ('BASE TABLE', 'VIEW')
                ORDER BY TABLE_SCHEMA, TABLE_NAME;";
            const string columnsSql = @"
                SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA NOT IN ('pg_catalog', 'information_schema')
                ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION;";
            const string routinesSql = @"
                SELECT n.nspname AS SchemaName, p.proname AS Name, p.prokind::text AS Kind
                FROM pg_proc p
                JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
                ORDER BY n.nspname, p.proname;";
            // Function/procedure parameters: proargnames (text[]) zipped with
            // proargtypes (oidvector). unnest ... WITH ORDINALITY aligns the two.
            const string routineParamsSql = @"
                SELECT n.nspname AS SchemaName, p.proname AS Routine,
                       pn.name AS Param, t.typname AS DataType
                FROM pg_proc p
                JOIN pg_namespace n ON n.oid = p.pronamespace
                CROSS JOIN LATERAL unnest(p.proargtypes::oid[]) WITH ORDINALITY AS pt(oid, ord)
                LEFT JOIN LATERAL unnest(p.proargnames) WITH ORDINALITY AS pn(name, ord) ON pn.ord = pt.ord
                JOIN pg_type t ON t.oid = pt.oid
                WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
                ORDER BY n.nspname, p.proname, pt.ord;";

            var tables = await conn.QueryAsync<(string Schema, string Name, string Type)>(new CommandDefinition(tablesSql, cancellationToken: ct));
            var columns = await conn.QueryAsync<(string Schema, string Table, string Column, string DataType)>(new CommandDefinition(columnsSql, cancellationToken: ct));
            var routines = await conn.QueryAsync<(string Schema, string Name, string Kind)>(new CommandDefinition(routinesSql, cancellationToken: ct));
            var routineParams = await conn.QueryAsync<(string Schema, string Routine, string? Param, string DataType)>(new CommandDefinition(routineParamsSql, cancellationToken: ct));

            var columnsByTable = columns
                .GroupBy(c => Qualify(c.Schema, c.Table))
                .ToDictionary(g => g.Key, g => g.Select(c => new SchemaColumn
                {
                    Name = c.Column,
                    DataType = c.DataType
                }).ToList());

            var schema = new List<SchemaObject>();
            foreach (var (schemaName, name, type) in tables)
            {
                var qualified = Qualify(schemaName, name);
                schema.Add(new SchemaObject
                {
                    Name = qualified,
                    Schema = schemaName,
                    Kind = type == "BASE TABLE" ? SchemaObjectKind.Table : SchemaObjectKind.View,
                    Columns = columnsByTable.TryGetValue(qualified, out var cols) ? cols : []
                });
            }

            var paramsByRoutine = routineParams
                .GroupBy(r => Qualify(r.Schema, r.Routine))
                .ToDictionary(g => g.Key, g => g.Select(r => new SchemaColumn
                {
                    Name = r.Param ?? $"arg{g.ToList().IndexOf(r) + 1}",
                    DataType = r.DataType
                }).ToList());

            foreach (var (schemaName, name, kind) in routines)
            {
                // prokind: f = function, p = procedure, w = window, a = aggregate.
                // Window and aggregate functions surface like functions; the
                // aggregate case is rare enough not to warrant its own kind.
                var qualified = Qualify(schemaName, name);
                schema.Add(new SchemaObject
                {
                    Name = qualified,
                    Schema = schemaName,
                    Kind = kind == "p" ? SchemaObjectKind.Procedure : SchemaObjectKind.Function,
                    Columns = paramsByRoutine.TryGetValue(qualified, out var ps) ? ps : []
                });
            }

            return schema;
        }

        private static string Qualify(string schema, string name) =>
            schema.Equals("public", StringComparison.OrdinalIgnoreCase) ? name : $"{schema}.{name}";

        public string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

        public string QuoteQualifiedName(string qualifiedName) =>
            string.Join(".", qualifiedName.Split('.').Select(QuoteIdentifier));

        public string BuildSelectTopN(string qualifiedName, int n) =>
            $"SELECT * FROM {QuoteQualifiedName(qualifiedName)} LIMIT {n}";

        public string BuildExecStub(string qualifiedName) => $"CALL {QuoteQualifiedName(qualifiedName)}(";

        public string BuildFunctionStub(string qualifiedName) => $"SELECT {QuoteQualifiedName(qualifiedName)}(";

        public string BuildConnectionString(ConnectionFormData form)
        {
            var port = form.Port ?? DefaultPort;
            // Npgsql requires a database: server-level profiles with nothing
            // picked yet land on the postgres maintenance database.
            var database = string.IsNullOrEmpty(form.Database) ? "postgres" : form.Database;
            return $"Host={ConnectorHelpers.EscapeCsValue(form.Host)};Port={port};"
                + $"Database={ConnectorHelpers.EscapeCsValue(database)};"
                + $"Username={ConnectorHelpers.EscapeCsValue(form.Username)};"
                + $"Password={ConnectorHelpers.EscapeCsValue(form.Password)}";
        }

        public bool SupportsServerLevel => true;

        public bool SupportsCrossDatabaseQueries => false;

        public string BuildServerConnectionString(ConnectionFormData form)
        {
            var port = form.Port ?? DefaultPort;
            return $"Host={ConnectorHelpers.EscapeCsValue(form.Host)};Port={port};"
                + $"Database=postgres;"
                + $"Username={ConnectorHelpers.EscapeCsValue(form.Username)};"
                + $"Password={ConnectorHelpers.EscapeCsValue(form.Password)}";
        }

        public async Task<List<string>> ListDatabasesAsync(DbConnection conn, CancellationToken ct = default)
        {
            // datallowconn excludes template0 and other non-connectable rows.
            const string sql = @"
                SELECT datname FROM pg_database
                WHERE datallowconn
                ORDER BY datname;";
            var dbs = await conn.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: ct));
            return dbs.ToList();
        }
    }
}