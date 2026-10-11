using System.Collections.Generic;
using System.Data;

namespace DbManager.Models
{
    /// <summary>
    /// One column of a table/view as shown in the SQL Editor sidebar.
    /// Kept deliberately flat (name + type string) because it is only used for
    /// display, insert-at-cursor and Monaco autocomplete — never for data access.
    /// For functions, this same shape carries the PARAMETERS (same name + type
    /// is all the sidebar and IntelliSense need, whether column or param).
    /// </summary>
    public class SchemaColumn
    {
        /// <summary>Column name as declared in the database.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Engine data type (e.g. "int", "nvarchar", "decimal").</summary>
        public string DataType { get; set; } = string.Empty;
    }

    /// <summary>
    /// Kind of object listed in the SQL Editor sidebar. Each connector maps its
    /// catalog queries onto these four kinds.
    /// </summary>
    public enum SchemaObjectKind
    {
        Table,
        View,
        Procedure,
        Function
    }

    /// <summary>
    /// A table, view, procedure or function of the active database, with its
    /// columns (or parameters, for functions) when applicable.
    /// <see cref="Name"/> is the QUERY-READY identifier: bare when the object
    /// lives in the engine's default schema (dbo/public/none), qualified as
    /// "schema.name" when it does not — so whatever the sidebar shows can be
    /// pasted into a query and work. <see cref="Schema"/> keeps the raw owning
    /// schema name for display/grouping.
    /// The whole schema is loaded once per circuit and kept in memory — lazy
    /// per-table loading would add complexity for no gain in an editor tool.
    /// </summary>
    public class SchemaObject
    {
        /// <summary>Query-ready object name (bare or schema-qualified, unquoted).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Owning schema name ("dbo", "public", …) or empty when the engine has none.</summary>
        public string Schema { get; set; } = string.Empty;

        /// <summary>Whether this is a table, view, procedure or function.</summary>
        public SchemaObjectKind Kind { get; set; }

        /// <summary>Columns of the table/view. Parameters for functions. Empty for procedures.</summary>
        public List<SchemaColumn> Columns { get; set; } = [];
    }

    /// <summary>
    /// Outcome of an arbitrary SQL execution. The SQL Editor cannot know the
    /// result shape at compile time, so results are carried as DataTables —
    /// one per result set — plus a DML row count.
    /// </summary>
    public class SqlEditorResult
    {
        /// <summary>
        /// One DataTable per result set returned by the batch.
        /// A batch like "SELECT 1 AS a; SELECT 2 AS b" produces two entries,
        /// rendered as tabs in the results pane (same UX as SSMS).
        /// </summary>
        public List<DataTable> ResultSets { get; } = [];

        /// <summary>
        /// Total rows affected by non-row-returning statements (INSERT/UPDATE/DELETE),
        /// summed across batches. Providers report -1 for pure SELECT batches,
        /// so treat -1 as "no DML info".
        /// </summary>
        public int RowsAffected { get; set; }

        /// <summary>Server execution time in milliseconds, for the footer stats bar.</summary>
        public long ElapsedMs { get; set; }

        /// <summary>
        /// Error message when execution failed (DbException.Message). Carried in the
        /// result instead of thrown so the page can render it inline in the results
        /// pane — the SSMS convention — instead of a generic toast-only flow.
        /// </summary>
        public string? Error { get; set; }
    }
}