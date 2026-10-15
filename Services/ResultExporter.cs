using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace DbManager.Services
{
    /// <summary>
    /// Serializes a result-set view (the rows the grid is currently showing —
    /// sorted and filtered) to the export formats offered by the results grid.
    /// Pure string building, no I/O: the caller streams the text to the browser.
    ///
    /// <paramref name="headers"/> applies to the flat formats (CSV, TXT) only;
    /// JSON and XML always carry the column names as keys/elements.
    /// </summary>
    public static class ResultExporter
    {
        /// <summary>Value rendered for a cell, matching the grid's display rules.</summary>
        public static string Display(object? value) => value switch
        {
            null or DBNull => string.Empty,
            byte[] bytes => Convert.ToBase64String(bytes),
            IFormattable f when value is not string => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

        /// <summary>
        /// RFC-4180 CSV. Fields are quoted when they contain a delimiter, a quote
        /// or a line break; embedded quotes are doubled. A UTF-8 BOM is prepended
        /// so Excel reads the encoding correctly.
        /// </summary>
        public static string ToCsv(IReadOnlyList<string> columns, IEnumerable<DataRow> rows, bool headers)
        {
            var sb = new StringBuilder();
            sb.Append('﻿');
            if (headers && columns.Count > 0)
                sb.Append(string.Join(",", Quoted(columns))).Append("\r\n");

            foreach (var row in rows)
            {
                for (var i = 0; i < columns.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Quote(Display(row[i])));
                }
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Tab-separated text. Tabs and line breaks inside values are replaced by
        /// spaces so a value can never break the column alignment.
        /// </summary>
        public static string ToTxt(IReadOnlyList<string> columns, IEnumerable<DataRow> rows, bool headers)
        {
            var sb = new StringBuilder();
            if (headers && columns.Count > 0)
                sb.Append(string.Join("\t", columns.Select(Flatten))).Append("\r\n");

            foreach (var row in rows)
            {
                for (var i = 0; i < columns.Count; i++)
                {
                    if (i > 0) sb.Append('\t');
                    sb.Append(Flatten(Display(row[i])));
                }
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>
        /// JSON array of objects keyed by column name. DBNull → null; numbers,
        /// booleans and dates keep their type; NaN/Infinity (not valid JSON) → null.
        /// </summary>
        public static string ToJson(IReadOnlyList<string> columns, IEnumerable<DataRow> rows)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartArray();
                foreach (var row in rows)
                {
                    writer.WriteStartObject();
                    for (var i = 0; i < columns.Count; i++)
                        WriteJsonValue(writer, columns[i], row[i]);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// XML document: &lt;result&gt;&lt;row&gt;&lt;column&gt;value&lt;/column&gt;…&lt;/row&gt;&lt;/result&gt;.
        /// Element names are sanitized with <see cref="XmlConvert.EncodeLocalName"/>
        /// (names may be empty, start with a digit or contain spaces); values are
        /// escaped by the writer. DBNull → an empty element.
        /// </summary>
        public static string ToXml(IReadOnlyList<string> columns, IEnumerable<DataRow> rows)
        {
            var names = new string[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                var encoded = XmlConvert.EncodeLocalName(columns[i]);
                names[i] = string.IsNullOrEmpty(encoded) ? $"column{i + 1}" : encoded;
            }

            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            };

            using var stream = new MemoryStream();
            using (var writer = XmlWriter.Create(stream, settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("result");
                foreach (var row in rows)
                {
                    writer.WriteStartElement("row");
                    for (var i = 0; i < columns.Count; i++)
                    {
                        writer.WriteStartElement(names[i]);
                        var value = row[i];
                        if (value is not (null or DBNull))
                            writer.WriteString(Display(value));
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                writer.WriteEndDocument();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// A reload script for <paramref name="quotedTable"/>: optionally the
        /// caller's truncate statement, then multi-row INSERTs of the current
        /// rows. Dialect specifics (identifier quoting, value literals, the
        /// truncate form) are supplied by the caller so the script runs on the
        /// source engine. Rows are grouped into INSERT statements of at most
        /// <paramref name="rowsPerStatement"/> tuples to stay readable.
        /// </summary>
        public static string ToTruncateInsert(
            IReadOnlyList<string> columns,
            IEnumerable<DataRow> rows,
            string quotedTable,
            Func<string, string> quoteIdentifier,
            Func<object?, string> formatLiteral,
            string? truncateStatement,
            int rowsPerStatement = 1000)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(truncateStatement))
                sb.Append(truncateStatement).Append("\r\n");

            if (columns.Count == 0)
                return sb.ToString();

            var columnList = string.Join(", ", columns.Select(quoteIdentifier));
            var prefix = $"INSERT INTO {quotedTable} ({columnList}) VALUES\r\n";

            // Materialized so the final row is known: the last tuple must close
            // with ';' even when the batch is only partially filled.
            var list = rows as IReadOnlyList<DataRow> ?? rows.ToList();
            for (var n = 0; n < list.Count; n++)
            {
                if (n % rowsPerStatement == 0)
                    sb.Append(prefix);

                sb.Append("    (");
                for (var i = 0; i < columns.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(formatLiteral(list[n][i]));
                }
                sb.Append(')');

                var closesStatement = (n + 1) % rowsPerStatement == 0 || n == list.Count - 1;
                sb.Append(closesStatement ? ";\r\n" : ",\r\n");
            }
            return sb.ToString();
        }

        // --- internals -----------------------------------------------------------

        private static void WriteJsonValue(Utf8JsonWriter writer, string name, object? value)
        {
            if (value is null or DBNull)
            {
                writer.WriteNull(name);
                return;
            }
            switch (value)
            {
                case bool b: writer.WriteBoolean(name, b); break;
                case byte:
                case sbyte:
                case short:
                case ushort:
                case int:
                case uint:
                case long:
                case ulong:
                    writer.WriteNumber(name, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    break;
                case float f: WriteFloat(writer, name, f); break;
                case double d: WriteFloat(writer, name, d); break;
                case decimal m: writer.WriteNumber(name, m); break;
                case DateTime dt: writer.WriteString(name, dt.ToString("o", CultureInfo.InvariantCulture)); break;
                case DateTimeOffset dto: writer.WriteString(name, dto.ToString("o", CultureInfo.InvariantCulture)); break;
                case byte[] bytes: writer.WriteString(name, Convert.ToBase64String(bytes)); break;
                default: writer.WriteString(name, Display(value)); break;
            }
        }

        // NaN/Infinity have no JSON representation; emit null instead of throwing.
        private static void WriteFloat(Utf8JsonWriter writer, string name, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                writer.WriteNull(name);
            else
                writer.WriteNumber(name, value);
        }

        private static string Quote(string field)
        {
            var needsQuotes = field.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                || field.Length > 0 && (field[0] == ' ' || field[^1] == ' ');
            if (!needsQuotes) return field;
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        private static IEnumerable<string> Quoted(IReadOnlyList<string> columns)
        {
            for (var i = 0; i < columns.Count; i++)
                yield return Quote(columns[i]);
        }

        private static string Flatten(string value) =>
            value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}
