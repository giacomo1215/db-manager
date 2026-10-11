using System;
using System.Collections.Generic;

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
    }
}