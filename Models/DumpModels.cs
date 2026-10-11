using System;

namespace DbManager.Models
{
    /// <summary>The SQL dialect a dump script targets — drives literal formatting.</summary>
    public enum SqlDialect
    {
        MsSql,
        MySql,
        Postgres,
        Sqlite
    }

    /// <summary>What a dump should contain.</summary>
    public class DumpOptions
    {
        public bool IncludeData { get; set; } = true;

        /// <summary>Rows per multi-row INSERT statement. Larger = smaller script, more RAM per statement.</summary>
        public int InsertBatchSize { get; set; } = 250;

        /// <summary>ADO command timeout in seconds; 0 = infinite (dumps routinely exceed the editor's 60 s).</summary>
        public int CommandTimeout { get; set; } = 0;
    }

    /// <summary>Live progress from a running dump/import (reported through IProgress).</summary>
    public record DumpProgress(string Stage, string? CurrentObject, long RowsDone, int ObjectsDone, int ObjectsTotal);

    /// <summary>Object/row counts of a completed dump.</summary>
    public class DumpStats
    {
        public int Tables { get; set; }
        public int Views { get; set; }
        public int Routines { get; set; }
        public int Indexes { get; set; }
        public long Rows { get; set; }
    }

    /// <summary>Outcome of an export (never throws; errors land in Error).</summary>
    public class DumpResult : DumpStats
    {
        /// <summary>Server temp file holding the script; the UI streams it to the browser and deletes it.</summary>
        public string? TempScriptPath { get; set; }

        public long ScriptBytes { get; set; }

        public long ElapsedMs { get; set; }

        public string? Error { get; set; }
    }

    /// <summary>Pass-1 scan of an uploaded script for the import confirm gate.</summary>
    public class ImportPreview
    {
        public int Statements { get; set; }

        public long Bytes { get; set; }

        public string? Error { get; set; }
    }

    /// <summary>Outcome of an import run (never throws; errors land in Error).</summary>
    public class ImportResult
    {
        public int StatementsExecuted { get; set; }

        public long ElapsedMs { get; set; }

        public string? Error { get; set; }

        /// <summary>First ~300 characters of the statement that failed, for the inline error.</summary>
        public string? FailedStatementPreview { get; set; }

        /// <summary>1-based index of the failing statement.</summary>
        public int FailedStatementIndex { get; set; }

        /// <summary>True when the all-or-nothing transaction rolled the whole script back (MySQL: never).</summary>
        public bool RolledBack { get; set; }
    }
}