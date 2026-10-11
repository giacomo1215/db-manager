using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DbManager.Abstractions;
using DbManager.Models;

namespace DbManager.Services.Dumping
{
    /// <summary>
    /// Streams a table's rows into multi-row INSERT statements. The dumper hands
    /// in the explicit column list (from its own catalog query, as QUOTED
    /// identifiers) so column order cannot drift and columns added later with
    /// defaults cannot break a re-import of an old script.
    /// </summary>
    public static class InsertWriter
    {
        // A pending statement is flushed at this size even before the row batch
        // fills up: keeps worst-case statement size bounded for the importer.
        private const long FlushBytes = 1024 * 1024;
        private const int ProgressEveryRows = 5000;

        /// <summary>Rows written. Never throws — the coordinator's catch ladder owns errors.</summary>
        public static async Task<long> WriteInsertsAsync(DbConnection conn, IDbConnector connector, SqlDialect dialect,
            string quotedTableName, IReadOnlyList<string> columnNames, TextWriter output,
            DumpOptions options, IProgress<DumpProgress>? progress, CancellationToken ct = default,
            bool overridingSystemValue = false)
        {
            var columnList = string.Join(", ", columnNames);
            var selectSql = new StringBuilder($"SELECT {columnList} FROM {quotedTableName}");

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = selectSql.ToString();
            cmd.CommandTimeout = options.CommandTimeout;
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            var rows = 0L;
            var pending = new StringBuilder();
            var pendingRows = 0;
            var fieldCount = reader.FieldCount;

            while (await reader.ReadAsync(ct))
            {
                if (pendingRows == 0)
                {
                    pending.Append("INSERT INTO ").Append(quotedTableName);
                    if (overridingSystemValue)
                        pending.Append(" OVERRIDING SYSTEM VALUE");
                    pending.Append(" (").Append(columnList).Append(") VALUES\n");
                }
                else
                    pending.Append(",\n");

                pending.Append('(');
                for (var i = 0; i < fieldCount; i++)
                {
                    if (i > 0)
                        pending.Append(", ");
                    SqlLiteral.Write(pending, reader.GetValue(i), dialect);
                }
                pending.Append(')');

                rows++;
                pendingRows++;
                if (rows % ProgressEveryRows == 0)
                    progress?.Report(new DumpProgress("Data", quotedTableName, rows, 0, 0));

                if (pendingRows >= options.InsertBatchSize || pending.Length >= FlushBytes)
                {
                    pending.Append(";\n");
                    await output.WriteAsync(pending, ct);
                    pending.Clear();
                    pendingRows = 0;
                }
            }

            if (pendingRows > 0)
            {
                pending.Append(";\n");
                await output.WriteAsync(pending, ct);
                pending.Clear();
            }

            return rows;
        }
    }
}