using System;
using System.Globalization;
using DbManager.Models;

namespace DbManager.Services.Dumping
{
    /// <summary>
    /// Renders an ADO value as an SQL literal for a given dialect. Values are
    /// read from readers via GetValue, so dispatch is on the RUNTIME type.
    ///
    /// One escaping rule everywhere: strings only double their single quotes.
    /// That stays correct under MySQL's NO_BACKSLASH_ESCAPES, Postgres'
    /// standard_conforming_strings and MSSQL/SQLite defaults alike, and raw
    /// newlines/tabs are legal literal characters in all four engines — so
    /// there is no engine-specific string branch at all.
    /// </summary>
    public static class SqlLiteral
    {
        public static void Write(System.Text.StringBuilder sb, object? value, SqlDialect dialect)
        {
            switch (value)
            {
                case null or DBNull:
                    sb.Append("NULL");
                    break;

                case bool b:
                    // Postgres has a real boolean type; the others store 0/1.
                    if (dialect == SqlDialect.Postgres)
                        sb.Append(b ? "TRUE" : "FALSE");
                    else
                        sb.Append(b ? '1' : '0');
                    break;

                case float f:
                    sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    break;

                case double d:
                    // "R" round-trips the exact double through the parser.
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;

                case decimal m:
                    // Plain invariant ToString preserves the stored scale —
                    // important for decimal(38,10) columns.
                    sb.Append(m.ToString(CultureInfo.InvariantCulture));
                    break;

                case string s:
                    WriteString(sb, s, dialect);
                    break;

                case char c:
                    WriteString(sb, c.ToString(), dialect);
                    break;

                case DateTime dt:
                    // ISO-8601 with 'T' is language-neutral on SQL Server even
                    // for old datetime; the others use the space form ADO emits.
                    if (dialect == SqlDialect.MsSql)
                        sb.Append($"'{dt:yyyy-MM-ddTHH:mm:ss.fffffff}'");
                    else
                        sb.Append($"'{dt:yyyy-MM-dd HH:mm:ss.ffffff}'");
                    break;

                case DateTimeOffset dto:
                    if (dialect == SqlDialect.MsSql || dialect == SqlDialect.Postgres)
                        sb.Append($"'{dto:yyyy-MM-ddTHH:mm:ss.fffffffzzz}'");
                    else
                        // No native type in MySQL/SQLite: store as UTC —
                        // documented lossy (offset is dropped).
                        sb.Append($"'{dto.UtcDateTime:yyyy-MM-dd HH:mm:ss.ffffff}'");
                    break;

                case TimeSpan ts:
                    if (dialect == SqlDialect.Postgres)
                    {
                        // Postgres interval input parses UNCAPPED hours (even
                        // negative), so days fold into hours and nothing in the
                        // value range is lost. (The "hh" format specifier would
                        // silently truncate at 24 h.)
                        var sign = ts < TimeSpan.Zero ? "-" : "";
                        var abs = ts.Duration();
                        var hours = (long)abs.TotalHours;
                        var frac = abs.Ticks % TimeSpan.TicksPerSecond; // 7 digits' worth of sub-second ticks
                        sb.Append($"'{sign}{hours:00}:{abs.Minutes:00}:{abs.Seconds:00}.{frac:0000000}'");
                    }
                    else
                        // time(7) columns cap at 24 h anyway; TEXT storage just
                        // keeps the string as-is.
                    {
                        sb.Append($"'{ts:hh\\:mm\\:ss\\.fffffff}'");
                    }
                    break;

                case Guid g:
                    // Implicitly casts to uniqueidentifier/uuid/char(36) in all four.
                    WriteString(sb, g.ToString(), dialect);
                    break;

                case byte[] bytes:
                    WriteBytes(sb, bytes, dialect);
                    break;

                case MySqlConnector.MySqlDateTime mydt:
                    WriteMySqlDateTime(sb, mydt);
                    break;

                default:
                    // sql_variant / hierarchyid / geometry / engine-specific CLR
                    // types: best-effort text. Documented lossy for those.
                    WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, dialect);
                    break;
            }
        }

        private static void WriteString(System.Text.StringBuilder sb, string s, SqlDialect dialect)
        {
            // NVARCHAR literal prefix on SQL Server so Unicode survives; the
            // others are natively UTF-8/UTF-16.
            if (dialect == SqlDialect.MsSql)
                sb.Append('N');
            sb.Append('\'');
            sb.Append(s.Replace("'", "''"));
            sb.Append('\'');
        }

        private static void WriteBytes(System.Text.StringBuilder sb, byte[] bytes, SqlDialect dialect)
        {
            switch (dialect)
            {
                case SqlDialect.MsSql:
                    sb.Append("0x");
                    sb.Append(Convert.ToHexString(bytes));
                    break;
                case SqlDialect.Postgres:
                    // bytea hex format — valid with standard_conforming_strings on.
                    sb.Append("'\\x");
                    sb.Append(Convert.ToHexString(bytes).ToLowerInvariant());
                    sb.Append('\'');
                    break;
                default: // MySQL and SQLite share X'..' notation.
                    sb.Append("X'");
                    sb.Append(Convert.ToHexString(bytes));
                    sb.Append('\'');
                    break;
            }
        }

        private static void WriteMySqlDateTime(System.Text.StringBuilder sb, MySqlConnector.MySqlDateTime dt)
        {
            // Zero dates only surface when the connection string opted into
            // AllowZeroDateTime; without it the provider throws and the error
            // surfaces in DumpResult.Error (documented limitation).
            if (!dt.IsValidDateTime)
                sb.Append("'0000-00-00 00:00:00'");
            else
                sb.Append($"'{dt.GetDateTime():yyyy-MM-dd HH:mm:ss.ffffff}'");
        }
    }
}