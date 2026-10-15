using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DbManager.Abstractions;

namespace DbManager.Services
{
    /// <summary>
    /// Pieces shared by the SQL executor and the interactive-transaction session:
    /// GO batch splitting and result-set materialization. Kept in one place so the
    /// two execution paths (autocommit runs and transaction-wrapped runs) behave
    /// identically.
    /// </summary>
    internal static class SqlExecutionHelpers
    {
        // GO batch separator: a line containing only "GO", case-insensitive. SSMS
        // treats GO the same way (its batches never reach the server). Only used
        // for engines with SupportsGoBatches (T-SQL); the others always get the
        // whole script as a single batch. Limitation (accepted heuristic for an
        // admin tool): a "GO" inside a string literal or comment would be split.
        private static readonly Regex GoBatchRegex =
            new(@"^\s*GO\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        /// <summary>
        /// Splits a script on GO batch separators for dialects that support them;
        /// everything else is one single batch (the common case).
        /// </summary>
        public static string[] SplitOnGo(string sql, IDbConnector connector) =>
            connector.SupportsGoBatches ? GoBatchRegex.Split(sql) : [sql];

        /// <summary>
        /// Materializes ONE result set (the reader's current one) into a DataTable.
        /// Deliberately NOT DataTable.Load(reader): Load CLOSES the reader when it
        /// thinks it is done, which breaks the caller's NextResultAsync loop and
        /// silently swallows every result set after the first. Filling manually
        /// keeps the reader open so the caller walks ALL result sets itself.
        /// </summary>
        public static async Task<DataTable> ReadCurrentTableAsync(DbDataReader reader, CancellationToken ct)
        {
            var table = new DataTable();

            // Columns first, from the reader's metadata. Duplicate aliases
            // ("SELECT 1 AS a, 2 AS a") would throw DuplicateNameException in
            // DataTable.Columns.Add, so they get a " (2)" suffix like a spreadsheet.
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                var baseName = name;
                for (var dup = 2; table.Columns.Contains(name); dup++)
                    name = $"{baseName} ({dup})";
                table.Columns.Add(name, reader.GetFieldType(i));
            }

            while (await reader.ReadAsync(ct))
            {
                var row = table.NewRow();
                for (var i = 0; i < reader.FieldCount; i++)
                    row[i] = reader.GetValue(i);
                table.Rows.Add(row);
            }

            return table;
        }
    }
}
