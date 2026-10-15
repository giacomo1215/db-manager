using System.Collections.Generic;
using System.Data;

namespace DbManager.Models
{
    /// <summary>
    /// One column of a table as the data editor needs it: on top of the name/type
    /// the sidebar already shows, the editor needs the flags that decide whether a
    /// column can be written (identity/auto-increment) and how a row is addressed
    /// (primary key). Only produced for engines with
    /// <see cref="Abstractions.IDbConnector.SupportsTableEditing"/>.
    /// </summary>
    public class TableColumnInfo
    {
        public string Name { get; set; } = string.Empty;

        public string DataType { get; set; } = string.Empty;

        /// <summary>Whether the column accepts NULL (drives the empty-cell → NULL rule).</summary>
        public bool IsNullable { get; set; } = true;

        /// <summary>
        /// Identity / auto-increment / GENERATED column: the engine supplies the
        /// value, so the editor omits it from INSERTs.
        /// </summary>
        public bool IsIdentity { get; set; }

        /// <summary>Member of the primary key — the key the editor addresses rows by.</summary>
        public bool IsPrimaryKey { get; set; }
    }

    /// <summary>
    /// One load of table data for the editor: the column metadata plus the rows as a
    /// DataTable (same shape the results grid already consumes). A table with no
    /// primary key loads fine but the editor refuses to write — a row cannot be
    /// addressed without one.
    /// </summary>
    public class TableDataResult
    {
        public List<TableColumnInfo> Columns { get; } = [];

        /// <summary>The rows, capped at the requested limit.</summary>
        public DataTable Rows { get; set; } = new();

        /// <summary>True when at least one primary-key column was found.</summary>
        public bool HasPrimaryKey { get; set; }

        /// <summary>The row cap that was applied, for the "showing first N" hint.</summary>
        public int LoadLimit { get; set; }

        /// <summary>Error message when the load failed, or null.</summary>
        public string? Error { get; set; }
    }
}
