using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using DbManager.Abstractions;
using DbManager.Models;
using Microsoft.Data.Sqlite;

namespace DbManager.Providers
{
    /// <summary>
    /// SQLite. Schema comes from sqlite_master (internal sqlite_% tables excluded)
    /// plus one PRAGMA table_info round trip per table — Dapper reads pragma result
    /// sets like any other query. No stored procedures or functions exist.
    /// </summary>
    public class SqliteConnector : IDbConnector
    {
        public string EngineKey => "sqlite";
        public string DisplayName => "SQLite";
        public int DefaultPort => 0;
        public bool SupportsGoBatches => false;
        public bool IsSelectIntoWrite => false;

        private static readonly HashSet<string> WriteKeywords = ConnectorHelpers.CoreWriteKeywords();

        static SqliteConnector()
        {
            WriteKeywords.Add("ATTACH");
            WriteKeywords.Add("DETACH");
        }

        ISet<string> IDbConnector.WriteKeywords => WriteKeywords;

        public DialectConfig DialectConfig => new()
        {
            Quote = "\"",
            OwnerHint = null,
            TopSnippet = "SELECT *\nFROM ${1:table}\nLIMIT 20"
        };

        public DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);

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
            const string objectsSql = @"
                SELECT name, type FROM sqlite_master
                WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%'
                ORDER BY name;";

            var objects = await conn.QueryAsync<(string Name, string Type)>(new CommandDefinition(objectsSql, cancellationToken: ct));

            var schema = new List<SchemaObject>();
            foreach (var (name, type) in objects)
            {
                var kind = type == "table" ? SchemaObjectKind.Table : SchemaObjectKind.View;
                var columns = new List<SchemaColumn>();

                // Column types via pragma — the declared type string (SQLite is
                // dynamically typed, so "type" is what the DDL said, or empty).
                var pragmaSql = $"PRAGMA table_info({QuoteIdentifier(name)});";
                var cols = await conn.QueryAsync<(int Seq, string Name, string? Type, bool NotNull, object? Default, int Pk)>(
                    new CommandDefinition(pragmaSql, cancellationToken: ct));
                columns.AddRange(cols.Select(c => new SchemaColumn
                {
                    Name = c.Name,
                    DataType = c.Type ?? ""
                }));

                schema.Add(new SchemaObject
                {
                    Name = name,
                    Kind = kind,
                    Columns = columns
                });
            }

            return schema;
        }

        public string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

        public string QuoteQualifiedName(string qualifiedName) =>
            string.Join(".", qualifiedName.Split('.').Select(QuoteIdentifier));

        public string BuildSelectTopN(string qualifiedName, int n) =>
            $"SELECT * FROM {QuoteQualifiedName(qualifiedName)} LIMIT {n}";

        /// <summary>SQLite has no procedures; stub kept for interface symmetry.</summary>
        public string BuildExecStub(string qualifiedName) => QuoteIdentifier(qualifiedName);

        /// <summary>SQLite has no functions; stub kept for interface symmetry.</summary>
        public string BuildFunctionStub(string qualifiedName) => QuoteIdentifier(qualifiedName);

        public string BuildConnectionString(ConnectionFormData form) =>
            $"Data Source={ConnectorHelpers.EscapeCsValue(form.FilePath)}";

        public bool SupportsServerLevel => false;

        public string BuildServerConnectionString(ConnectionFormData form) =>
            throw new System.NotSupportedException("SQLite is file-based; there is no server.");

        public Task<List<string>> ListDatabasesAsync(DbConnection conn, CancellationToken ct = default) =>
            throw new System.NotSupportedException("SQLite is file-based; there is no server.");
    }
}