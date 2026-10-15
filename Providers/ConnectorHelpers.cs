using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;

namespace DbManager.Providers
{
    /// <summary>
    /// Pieces of a URI-style connection string, the format cloud providers
    /// (Supabase, Neon, PlanetScale, …) hand out: postgresql://user:pass@host:port/db?opt=v.
    /// ADO.NET drivers want Keyword=Value strings instead — connectors normalize.
    /// </summary>
    public sealed record ConnectionUri(
        string Host,
        int? Port,
        string? User,
        string? Password,
        string? Database,
        IReadOnlyList<KeyValuePair<string, string>> Query);

    /// <summary>Shared bits every dialect connector uses.</summary>
    internal static class ConnectorHelpers
    {
        /// <summary>
        /// Escapes a value inside an ADO.NET connection string. Values containing
        /// ';', '=', quotes or outer whitespace must be single-quoted with inner
        /// single quotes doubled — every driver here accepts that form.
        /// </summary>
        public static string EscapeCsValue(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var needsQuotes = value.Contains(';') || value.Contains('=') || value.Contains('\'')
                || value.Contains('"') || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]);
            if (!needsQuotes)
                return value;

            return "'" + value.Replace("'", "''") + "'";
        }

        /// <summary>
        /// Parses a postgres://, postgresql:// or mysql:// connection URI. Returns
        /// false for anything else (including normal Keyword=Value strings), so
        /// callers pass unrecognized input through untouched. Percent-escapes in
        /// the user/password/database are decoded.
        /// </summary>
        public static bool TryParseConnectionUri(string input, out ConnectionUri? uri)
        {
            uri = null;

            if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var parsed))
                return false;
            if (parsed.Scheme is not ("postgres" or "postgresql" or "mysql"))
                return false;
            if (string.IsNullOrEmpty(parsed.Host))
                return false;

            string? user = null, password = null;
            if (!string.IsNullOrEmpty(parsed.UserInfo))
            {
                var separator = parsed.UserInfo.IndexOf(':');
                user = separator < 0
                    ? parsed.UserInfo
                    : parsed.UserInfo[..separator];
                if (separator >= 0)
                    password = parsed.UserInfo[(separator + 1)..];
                user = Uri.UnescapeDataString(user);
                if (!string.IsNullOrEmpty(password))
                    password = Uri.UnescapeDataString(password);
            }

            var database = Uri.UnescapeDataString(parsed.AbsolutePath.TrimStart('/'));
            var query = new List<KeyValuePair<string, string>>();
            if (!string.IsNullOrEmpty(parsed.Query))
            {
                foreach (var pair in parsed.Query.TrimStart('?').Split('&'))
                {
                    var kv = pair.Split('=', 2);
                    if (kv.Length == 2)
                        query.Add(new(Uri.UnescapeDataString(kv[0]), Uri.UnescapeDataString(kv[1])));
                }
            }

            uri = new ConnectionUri(
                parsed.Host,
                parsed.Port > 0 ? parsed.Port : null,
                string.IsNullOrEmpty(user) ? null : user,
                string.IsNullOrEmpty(password) ? null : password,
                database.Length == 0 ? null : database,
                query);
            return true;
        }

        /// <summary>
        /// The write keywords shared by every dialect; engines append their own
        /// (MSSQL: BACKUP/RESTORE/DENY; MySQL: CALL; SQLite: ATTACH/DETACH).
        /// SELECT and WITH are absent because the common case is a read;
        /// SELECT INTO is handled per-dialect by IDbConnector.IsSelectIntoWrite.
        /// </summary>
        public static HashSet<string> CoreWriteKeywords() => new(System.StringComparer.OrdinalIgnoreCase)
        {
            "UPDATE", "DELETE", "INSERT", "MERGE", "ALTER", "DROP", "TRUNCATE",
            "CREATE", "EXEC", "EXECUTE", "GRANT", "REVOKE"
        };

        /// <summary>
        /// Engine-correct SQL literal for one result value, used by the results
        /// grid's "truncate + insert" export.
        /// <paramref name="hexLiteral"/> picks the binary form — 0xDEADBEEF for
        /// T-SQL, X'DEADBEEF' elsewhere. <paramref name="nativeBool"/> emits
        /// TRUE/FALSE (PostgreSQL) instead of the portable 1/0 that every other
        /// engine accepts for a boolean column. <paramref name="unicodePrefix"/>
        /// prefixes string literals with N (T-SQL) so non-ASCII text survives.
        /// <paramref name="backslashEscapes"/> doubles backslashes before the quote
        /// doubling, for engines where "\" is itself an escape character (MySQL and
        /// MariaDB, whose default sql_mode does not set NO_BACKSLASH_ESCAPES). Without
        /// it a value ending in "\" swallows the closing quote and the literal is
        /// broken — and a crafted value becomes an injection. Off for T-SQL, SQLite
        /// and PostgreSQL (standard_conforming_strings on): there "\" is literal and
        /// doubling it would corrupt the value.
        /// </summary>
        public static string FormatSqlLiteral(object? value, bool hexLiteral, bool nativeBool, bool unicodePrefix, bool backslashEscapes = false)
        {
            switch (value)
            {
                case null or DBNull:
                    return "NULL";
                case bool b:
                    return nativeBool ? (b ? "TRUE" : "FALSE") : (b ? "1" : "0");
                case byte[] bytes:
                    var hex = Convert.ToHexString(bytes);
                    return hexLiteral ? "0x" + hex : "X'" + hex + "'";
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";
                case float f:
                    return FormatFloat(f);
                case double d:
                    return FormatFloat(d);
                case decimal m:
                    return m.ToString(CultureInfo.InvariantCulture);
                case DateTime dt:
                    return "'" + dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "'";
                case DateTimeOffset dto:
                    return "'" + dto.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) + "'";
                case Guid g:
                    return "'" + g.ToString() + "'";
                default:
                    // Doubling the quote is the portable escape for every engine here;
                    // backslashes are doubled first (before quotes are added) only for
                    // the engines that treat "\" as an escape.
                    var text = value.ToString() ?? string.Empty;
                    if (backslashEscapes)
                        text = text.Replace("\\", "\\\\");
                    var prefix = unicodePrefix ? "N" : "";
                    return prefix + "'" + text.Replace("'", "''") + "'";
            }
        }

        /// <summary>The statement that empties a table: TRUNCATE, or DELETE for engines without it.</summary>
        public static string TruncateStatement(string quotedTable, bool supportsTruncate) =>
            supportsTruncate ? $"TRUNCATE TABLE {quotedTable};" : $"DELETE FROM {quotedTable};";

        // NaN/Infinity have no portable SQL literal; NULL is safer than invalid SQL.
        private static string FormatFloat(double value) =>
            double.IsNaN(value) || double.IsInfinity(value)
                ? "NULL"
                : value.ToString("R", CultureInfo.InvariantCulture);
    }
}