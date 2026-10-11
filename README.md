# db-manager

A standalone, multi-engine SQL editor. Connect to just about any database, save the connection, and go.

## Engines

- **SQL Server** (`Microsoft.Data.SqlClient`)
- **MySQL / MariaDB** (`MySqlConnector`)
- **PostgreSQL** (`Npgsql`)
- **SQLite** (`Microsoft.Data.Sqlite`)

Adding another engine means one class implementing `IDbConnector` (Providers/) plus one DI registration in `Program.cs` — nothing else changes: schema introspection, identifier quoting, TOP-N vs LIMIT, GO batches, write-keyword detection and the Monaco dialect config all live in that one class.

## Security model

- Single-user tool: there is **no login** — whoever can reach the app can use it. Run it locally or on a trusted LAN.
- Saved connection credentials (full connection strings, passwords included) are stored in an **encrypted vault** at `~/.db-manager/connections.json` (override with `ConnectionStore:Path` in `appsettings.json`).
  - Encryption: AES-GCM, key derived from a **master password** via PBKDF2-SHA512 (≥210k iterations, params stored in the file so they can be raised later).
  - The master password is never stored anywhere. Losing it means losing the vault.
  - The derived key lives only in the per-circuit session (each browser tab unlocks separately); the navbar **Lock** button, or closing the tab, clears it from memory.
  - A wrong master password simply fails AES-GCM tag verification.

## Features

- Monaco editor (VS Code core) with schema-aware SQL IntelliSense: table/column completion with alias tracking, context-aware suggestions (FROM/JOIN/EXEC), snippets, hover tooltips; dialect-aware quoting per engine.
- F5 / Ctrl+E / Ctrl+Enter to run; draft autosaved per connection.
- Schema browser sidebar: tables/views/procedures/functions, search, expandable columns, insert-at-cursor, quick "TOP 20"/"LIMIT 20" peek.
- SSMS-style results: multiple result sets as tabs, virtualized grid, right-aligned numerics, italic NULLs; GO batch splitting (T-SQL); write statements gated behind a confirmation.
- Write-gate keyword heuristics per dialect; connection switching with schema + draft swap.
- Light/dark theme (Bootstrap 5.3 `data-bs-theme`).

## Backup & schema export (SQL Editor page → cloud-arrow button)

The panel on the SQL Editor page operates on the **active connection**:

- **Export**: "Schema + data" (a full backup) or "Schema only" (DDL without rows), as an engine-native SQL script — restorable through the panel's import or any SQL client. Exports stream to a server temp file and download in chunks, so multi-hundred-MB dumps never sit in memory as one string.
- **Import**: pick a `.sql` file; the panel counts its statements and asks for confirmation before executing. Objects with the same names are **dropped and replaced** (every CREATE is preceded by `DROP … IF EXISTS`), so a restore is idempotent and safe to re-run.
- **Connections vault backup**: a separate export/import on the Connections page covers the saved-connections vault itself (encrypted copy or plaintext JSON).

### Per-engine behavior and limitations

| Engine | DDL source | Import transaction | Notes / limitations |
|---|---|---|---|
| SQLite | `sqlite_master.sql` verbatim | Yes (all-or-nothing; `PRAGMA defer_foreign_keys` defers FK checks to COMMIT so any table order loads) | AUTOINCREMENT counters replayed via `sqlite_sequence`. FTS virtual tables' shadow tables are dumped as real tables and collide with what `CREATE VIRTUAL TABLE` recreates — don't round-trip FTS databases. |
| MySQL | `SHOW CREATE TABLE/VIEW/PROCEDURE/FUNCTION/TRIGGER` | No — DDL auto-commits | A failed import can leave the database **partially restored**; re-running the script is safe thanks to `DROP IF EXISTS`. `DEFINER=` clauses and `AUTO_INCREMENT=` counters are stripped. Zero dates require `AllowZeroDateTime` in the connection string or the dump errors out. |
| SQL Server | composed from `sys.*` catalogs | Yes (GO batches, all-or-nothing) | FKs are added after data (`ALTER TABLE … ADD CONSTRAINT`), so dependency order can't fail. `SET IDENTITY_INSERT` brackets identity data. Column **collations** are not emitted (server default applies), and XML/spatial indexes are skipped. |
| PostgreSQL | `pg_get_*def` catalog functions | Yes (all-or-nothing) | Sequences (incl. identity) recreated with `setval` after data; views topologically sorted; `GENERATED ALWAYS` identity data restored with `OVERRIDING SYSTEM VALUE`, stored generated columns skipped in INSERTs. Partition **child tables** are not dumped. Functions use `CREATE OR REPLACE` — a changed signature leaves the old version behind. Managed-hosting internal schemas (Supabase `auth`/`storage`/`realtime`/`vault`/…, `extensions`) are excluded — the dump is your own schemas only. |

## Run

```bash
dotnet run
```

Then open the URL from the console output. First visit: create the master password and add a connection (structured fields or a raw connection string; "Test connection" checks it before saving).

## Layout

```
Abstractions/   interfaces (IDbConnector, ISqlEditorService, IVaultService, …)
Models/         schema models, connection profiles, vault file + dialect payloads
Providers/      one connector per engine
Services/       SQL executor (provider-agnostic), vault crypto, toasts, viewmodel
Components/     Blazor pages (Connections, SqlEditor) + layout
wwwroot/js/     Monaco interop (sql-editor.js), theme (site.js)
```
