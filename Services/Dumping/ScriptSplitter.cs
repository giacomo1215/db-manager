using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DbManager.Services.Dumping
{
    /// <summary>How statements are separated in a script.</summary>
    public enum SqlSplitMode
    {
        /// <summary>T-SQL "GO" batch lines (same rule as the SQL editor).</summary>
        GoBatches,

        /// <summary>Statement semicolons outside strings, comments, and dollar-quotes.</summary>
        Semicolons
    }

    /// <summary>
    /// Streams a script file as individual statements without ever holding the
    /// whole script in memory. Exported routine bodies contain embedded
    /// semicolons (BEGIN…END, dollar-quoted PL/pgSQL); our dumps wrap every
    /// routine in "-- dbm:routine-start" / "-- dbm:routine-end" marker lines,
    /// and the splitter switches to raw mode between them — an engine-neutral
    /// replacement for MySQL's DELIMITER.
    /// </summary>
    public static class ScriptSplitter
    {
        public const string RoutineStartMarker = "-- dbm:routine-start";
        public const string RoutineEndMarker = "-- dbm:routine-end";

        // Same rule as SqlEditorService.GoBatchRegex (its accepted heuristic:
        // a GO inside a string/comment would split; our exports emit clean GO lines).
        private static readonly Regex GoLineRegex = new(@"^\s*GO\s*$", RegexOptions.IgnoreCase);

        public static async IAsyncEnumerable<string> ReadStatementsAsync(TextReader reader, SqlSplitMode mode,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            string? line;
            if (mode == SqlSplitMode.GoBatches)
            {
                var batch = new StringBuilder();
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    if (GoLineRegex.IsMatch(line))
                    {
                        var text = batch.ToString();
                        batch.Clear();
                        if (!string.IsNullOrWhiteSpace(text))
                            yield return text;
                    }
                    else
                    {
                        batch.Append(line).Append('\n');
                    }
                }
                var tail = batch.ToString();
                if (!string.IsNullOrWhiteSpace(tail))
                    yield return tail;
                yield break;
            }

            // --- semicolon mode: line-oriented scanner ---------------------------------
            // Line granularity makes comments and markers easy: a "--" comment
            // can't eat a marker line, and marker lines are recognized whole.
            // String/block-comment/dollar-quote state persists across lines.
            var scanner = new SemicolonScanner();
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (scanner.RawMode)
                {
                    if (line.Trim() == RoutineEndMarker)
                    {
                        scanner.RawMode = false;
                        if (scanner.TryTakeStatement(out var routine))
                            yield return routine;
                    }
                    else
                    {
                        scanner.AppendRaw(line);
                    }
                    continue;
                }

                if (line.Trim() == RoutineStartMarker)
                {
                    // A marker between statements ends the pending one (should
                    // be blank in our exports; hand-written scripts may differ).
                    if (scanner.TryTakeStatement(out var pending))
                        yield return pending;
                    scanner.RawMode = true;
                    continue;
                }

                scanner.ScanLine(line);
                while (scanner.TryTakeCompleted(out var statement))
                    yield return statement;
            }
            if (scanner.TryTakeStatement(out var last))
                yield return last;
        }

        /// <summary>Per-run state of the semicolon scanner (kept internal to this file).</summary>
        private sealed class SemicolonScanner
        {
            private readonly StringBuilder _statement = new();
            private readonly Queue<string> _completed = new();
            private char _quote;                 // 0 = not inside a quoted literal/identifier
            private bool _escaped;               // MySQL backslash escape inside '...'
            private bool _inBlockComment;
            private string? _dollarTag;         // "$function$" while inside a dollar-quote

            public bool RawMode { get; set; }

            /// <summary>Statements a marker line (or EOF) has closed — queue first, then the buffer.</summary>
            public bool TryTakeStatement(out string statement)
            {
                if (TryTakeCompleted(out statement))
                    return true;
                statement = _statement.ToString().Trim();
                _statement.Clear();
                return statement.Length > 0;
            }

            /// <summary>Statements closed by semicolons during the last scanned line(s).</summary>
            public bool TryTakeCompleted(out string statement)
            {
                if (_completed.Count > 0)
                {
                    statement = _completed.Dequeue();
                    return true;
                }
                statement = string.Empty;
                return false;
            }

            public void AppendRaw(string line) => _statement.Append(line).Append('\n');

            /// <summary>
            /// Scans one line, queueing every statement an unquoted semicolon
            /// closes; the un-closed remainder stays buffered. A ';' inside a
            /// -- comment, /* */ comment, quoted literal/identifier or dollar
            /// quote is text, never a splitter.
            /// </summary>
            public void ScanLine(string line)
            {
                for (var i = 0; i < line.Length; i++)
                {
                    var c = line[i];
                    _statement.Append(c);

                    if (_quote != 0)
                    {
                        if (_escaped)
                        {
                            _escaped = false; // previous char was a backslash
                        }
                        else if (c == '\\' && _quote == '\'')
                        {
                            _escaped = true; // MySQL: \' stays inside the literal
                        }
                        else if (c == _quote)
                        {
                            // Doubled quote = escaped, anything else closes.
                            if (i + 1 < line.Length && line[i + 1] == _quote)
                            {
                                _statement.Append(line[++i]);
                            }
                            else
                            {
                                _quote = '\0';
                            }
                        }
                        continue;
                    }

                    if (_dollarTag is not null)
                    {
                        if (c == _dollarTag[^1] && EndsWith(_statement, _dollarTag))
                            _dollarTag = null;
                        continue;
                    }

                    if (_inBlockComment)
                    {
                        if (c == '/' && _statement[^2] == '*')
                            _inBlockComment = false;
                        continue;
                    }

                    switch (c)
                    {
                        case '\'' or '"' or '`':
                            _quote = c;
                            break;

                        case ';':
                            // Drop the semicolon; close the statement. Blank
                            // buffers (whitespace between statements) skip.
                            _statement.Remove(_statement.Length - 1, 1);
                            var text = _statement.ToString();
                            _statement.Clear();
                            if (!string.IsNullOrWhiteSpace(text))
                                _completed.Enqueue(text.Trim());
                            break;

                        case '-' when i + 1 < line.Length && line[i + 1] == '-':
                            // Line comment: append the rest verbatim and stop —
                            // a ';' in a comment is text, not a splitter.
                            _statement.Append(line[++i..]).Append('\n');
                            return;

                        case '/' when i + 1 < line.Length && line[i + 1] == '*':
                            _inBlockComment = true;
                            _statement.Append(line[++i]);
                            break;

                        case '$':
                            TryOpenDollarQuote();
                            break;
                    }
                }

                _statement.Append('\n');
            }

            /// <summary>When the buffer ends in $tag$, records the tag and enters the dollar-quote.</summary>
            private void TryOpenDollarQuote()
            {
                // The opening delimiter is $tag$; find the previous '$' with
                // only [A-Za-z0-9_] between. Plain "$$" (empty tag) is valid too.
                var sb = _statement;
                var i = sb.Length - 1; // at the second '$'
                var tagEnd = i;
                while (i > 0 && (char.IsAsciiLetterOrDigit(sb[i - 1]) || sb[i - 1] == '_'))
                    i--;
                if (i == 0 || sb[i - 1] != '$')
                    return; // not a $tag$ opener
                _dollarTag = sb.ToString(i - 1, tagEnd - i + 2);
            }

            private static bool EndsWith(StringBuilder sb, string suffix)
            {
                if (sb.Length < suffix.Length)
                    return false;
                for (var i = 0; i < suffix.Length; i++)
                    if (sb[sb.Length - suffix.Length + i] != suffix[i])
                        return false;
                return true;
            }
        }
    }
}