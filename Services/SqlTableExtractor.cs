using System.Text;
using System.Text.RegularExpressions;

namespace DbManager.Services
{
    /// <summary>
    /// Best-effort extraction of the target table from a SELECT, so the results
    /// grid can prefill its "truncate + insert" export without asking the user.
    /// Finds the first FROM and returns the qualified table reference VERBATIM
    /// (the user's own quoting is preserved), or null when none is found — a
    /// derived-table-only query, VALUES, or a non-SELECT statement.
    ///
    /// Deliberately not a full parser: joins are ignored and the first FROM table
    /// is taken, which is the intended target for the reload use case. A "FROM"
    /// inside a string literal or a quoted identifier can mislead it.
    /// </summary>
    public static class SqlTableExtractor
    {
        private static readonly Regex FromRegex = new(@"\bFROM\b", RegexOptions.IgnoreCase);

        /// <summary>The first table reference after FROM, or null.</summary>
        public static string? Extract(string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return null;

            var match = FromRegex.Match(sql);
            return match.Success ? ReadQualifiedName(sql, match.Index + match.Length) : null;
        }

        /// <summary>
        /// Reads a dotted, possibly-quoted name from position <paramref name="i"/>.
        /// Consecutive dots are kept as empty parts so three-part T-SQL references
        /// with a defaulted schema ("Db..Table") survive verbatim.
        /// </summary>
        private static string? ReadQualifiedName(string sql, int i)
        {
            var joined = new StringBuilder();
            var havePart = false;

            while (true)
            {
                while (i < sql.Length && char.IsWhiteSpace(sql[i]))
                    i++;
                if (i >= sql.Length)
                    break;

                // "FROM (" — a derived table has no name to reuse.
                if (!havePart && sql[i] == '(')
                    return null;

                // Empty part: "Db..Table" (or leading dot).
                if (sql[i] == '.')
                {
                    joined.Append('.');
                    havePart = true;
                    i++;
                    continue;
                }

                var start = i;
                i = SkipIdentifier(sql, i);
                if (i == start)
                    break;

                joined.Append(sql, start, i - start);
                havePart = true;

                // A following '.' (whitespace tolerated around "dbo . T") continues
                // the qualified name; the loop's dot branch consumes it next pass.
                var j = i;
                while (j < sql.Length && char.IsWhiteSpace(sql[j]))
                    j++;
                if (j < sql.Length && sql[j] == '.')
                {
                    i = j;
                    continue;
                }
                break;
            }

            return havePart ? joined.ToString() : null;
        }

        /// <summary>
        /// Advances past one identifier part, honoring [] / "" / `` quoting.
        /// Returns the new index, unchanged when no part starts at <paramref name="i"/>.
        /// </summary>
        private static int SkipIdentifier(string sql, int i)
        {
            var close = sql[i] switch { '[' => ']', '"' => '"', '`' => '`', _ => '\0' };

            if (close != '\0')
            {
                var end = sql.IndexOf(close, i + 1);
                return end < 0 ? sql.Length : end + 1;
            }

            // Bare identifier: stop at whitespace or a delimiter.
            var start = i;
            while (i < sql.Length)
            {
                var c = sql[i];
                if (char.IsWhiteSpace(c) || c is '.' or ',' or ';' or '(' or ')')
                    break;
                i++;
            }
            return i == start ? start : i;
        }
    }
}
