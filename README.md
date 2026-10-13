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
- Saved-query library: name and store the editor content, reload it from the header dropdown, move a single query to/from a `.sql` file, and back the whole library up.
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

## Saved queries

The bookmark button in the SQL Editor header opens the **saved-query library**
(global across connections; each query is tagged with the connection it was saved
from):

- **Save current** — stores the editor content under the typed name. Click a query
  to load it back into the editor (with a confirm when the editor already has
  content). Rename and delete inline.
- **Export .sql / Import .sql** — download a single query as a `.sql` file, or load
  a `.sql` file into the editor. Import only loads text; it never executes.
- **Backup** — the "Saved Queries Backup" card on the Connections page exports the
  whole library as an **encrypted copy** (vault master password) or a **plaintext
  JSON**, and restores either — mirroring the connections vault backup. Restore
  replaces the entire library.

Storage: a small SQLite file at `~/.db-manager/queries.db` (override with
`QueryStore:Path`). Query **names and connection tags are plaintext** for fast
listing; each query's **SQL text is AES-GCM-encrypted under the session vault
key**, so the library is only readable while the vault is unlocked. Changing the
master password, or restoring a different connections vault, re-encrypts the
library under the new key automatically.

## Run

### Development

```bash
dotnet run
```

Then open the URL from the console output. First visit: create the master password and add a connection (structured fields or a raw connection string; "Test connection" checks it before saving).

### Desktop release (single PC, native window)

```bash
dotnet publish -c Release -p:PublishProfile=desktop
```

Produces a self-contained single-file `publish/desktop/win-x64/DbManager.exe`. Double-click it: a native OS window opens on the app — no browser, no console, no .NET install required. Kestrel still hosts Blazor Server internally, but bound to `127.0.0.1` on a random port, so it is invisible to the network. Closing the window exits the app.

Windows requires the **WebView2 Runtime** (preinstalled on Windows 11; downloadable for older systems).

### Server release (shared on a LAN)

```bash
dotnet publish -c Release -p:PublishProfile=server
```

Produces `publish/server/win-x64/DbManager.Server.exe`. Run it and the console prints the listening URL; with no configuration it binds `http://0.0.0.0:8080` so anyone on the LAN can reach it in a browser. Override with `--urls` / `ASPNETCORE_URLS`:

```bash
DbManager.Server.exe --urls http://0.0.0.0:9000
```

HTTPS is supported the usual Kestrel way (configure a cert via `Kestrel:Endpoints` in `appsettings.json` or `ASPNETCORE_Kestrel__...` env vars); the https→http redirect is enabled only when an `https://` endpoint actually exists. Windows Firewall prompts on first run — allow it for the LAN's network profile.

## Layout

```
Abstractions/   interfaces (IDbConnector, ISqlEditorService, IVaultService, …)
Models/         schema models, connection profiles, vault file + dialect payloads
Providers/      one connector per engine
Services/       SQL executor (provider-agnostic), vault crypto, toasts, viewmodel
Components/     Blazor pages (Connections, SqlEditor) + layout
wwwroot/js/     Monaco interop (sql-editor.js), theme (site.js)
```
