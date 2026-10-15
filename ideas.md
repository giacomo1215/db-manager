# Ideas

Candidate features that aren't built yet, roughly in the order they'd pay off.
Each entry names the main design question, not just the wish — so the next pass
starts from a decision, not a blank page.

Already shipped (kept here only for context): multi-engine editor, schema-aware
IntelliSense, tabbed queries, SSMS-style multi-result grid with CSV/TXT/JSON/XML/SQL
export, saved-query library, encrypted connections vault, per-engine backup/restore,
execution plan (EXPLAIN/SHOWPLAN), rolling query history, interactive transaction mode,
and a live "running" indicator with Cancel.

---

## Near term

### Table data editor — MSSQL done, other engines pending
Sidebar "Data" button opens an editable grid over a table: change cells, add rows,
delete rows, then Apply generates INSERT/UPDATE/DELETE and runs it. Shipped for
**SQL Server only** (`IDbConnector.SupportsTableEditing`); the button is hidden for
MySQL/PostgreSQL/SQLite.

**What remains:** the `GetTableColumnsAsync` catalog query (PK + identity + nullability)
for the other three engines, each verified against a live server before flipping
`SupportsTableEditing` to true. The grid itself is engine-agnostic — it goes through
`Connector.QuoteIdentifier`/`QuoteQualifiedName`/`FormatSqlLiteral`, so only the catalog
queries are per-engine.

**Known v1 limits:** empty cell → NULL (documented in the footer); edits are sent as
string literals and rely on the server's implicit conversion; the row load is capped
(200) and the grid is not virtualized; no INSERT/UPDATE/DELETE is built for a table
without a primary key (view-only).

### ERD / schema diagram
Read FK relationships from the catalogs and render a draggable graph. High "wow",
moderate effort; the catalog queries for foreign keys are simpler and more uniform
than the PK/identity ones the table editor needs, so this could land first.

### Connection groups / folders + color tags
The vault already stores the profile list; add a `Group` and `Color` field to
`ConnectionProfile` and group the Connections page and the editor's connection dropdown.

## Medium

### SSH tunnel for MySQL/PostgreSQL
Add `SshHost/Port/User/Auth` fields to the connection form and tunnel through an
`Renci.SshNet` `ForwardedPortLocal` before opening the DB connection. Unlocks remote
databases that are only reachable from a jump box. Security-sensitive: key handling,
host-key verification, and tunnel lifetime all need care.

### Data diff
Compare two result sets (or two tables across connections) row-by-row and highlight
added / removed / changed rows. Reuses the grid; the hard part is choosing a key to
align rows on (again PK-shaped metadata).

### Actual execution plans + runtime stats
Today's plan view is estimated only (SHOWPLAN / plain EXPLAIN). Offer `EXPLAIN ANALYZE`
(PG), `EXPLAIN ANALYZE` (MySQL 8), and `SET STATISTICS XML ON` (MSSQL) for real timings
and row counts. Note the trade-off: those *execute* the query, so the button must warn
and be barred for writes.

### Excel (.xlsx) export
The grid already exports CSV/TXT/JSON/XML/SQL. Add a real `.xlsx` via a small writer
(e.g. ClosedXML) for people who need formatting-preserving spreadsheets.

### Query parameters / snippets
Let a script declare `${name:default}` and prompt for values before running, then
substitute (safely quoted per dialect). Monaco already supports snippet placeholders.

### Scheduled / saved dumps
Re-run a saved dump (schema, or schema+data) on a schedule and drop the file in a
folder — a lightweight "backup job". Needs a scheduler in the host and a place to store
job definitions (the vault or a small JSON).

## Advanced

### Migration / schema-diff generator
Diff two schemas' catalogs and emit the ALTER script to move one to the other. Reuses
the dumper's catalog introspection; the generation logic is the real work.

### AI SQL assist
Natural-language → SQL, or "explain this query / plan". Fits the app but adds an API
dependency, per-request cost, and a place to configure a key. Keep it strictly opt-in.

### Multi-server / central connection directory
Share the connection vault across a team (read-only profiles, per-user secrets) instead
of local-only `~/.db-manager`. A much larger architectural change than the rest here.
