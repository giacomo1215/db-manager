// db-manager SQL Editor helpers — Monaco Editor interop. Ported from the
// ZeatDashboard /sql-editor page, with three adaptations for the standalone
// multi-engine app: the global is dbSqlEditor, the editor draft is saved per
// connection profile, and identifier quoting / owner completion / the TOP-N
// snippet are driven by a per-dialect config pushed alongside the schema.
//
// WHY JS at all: Monaco is a browser library (the VS Code editor core), so the
// editor lifecycle lives in JS; Blazor drives it through IJSRuntime calls and
// receives events back through a DotNetObjectReference ([JSInvokable] methods
// on the page component).
//
// Loading strategy: App.razor includes only Monaco's tiny AMD loader.js (~3 KB)
// from the CDN. The ~2 MB editor engine is fetched on demand the FIRST time a
// user opens the editor page, via require(['vs/editor/editor.main']).
// Subsequent visits in the same browser session reuse the cached download.
window.dbSqlEditor = (() => {
    // divId -> { editor, dotnetRef } — one entry per mounted editor instance.
    const editors = new Map();

    // Schema for autocomplete, set from C# after the DB round trip.
    // Shape: [{ name, kind, schema, columns: [{ name, dataType }] }, ...]
    // `name` is the QUERY-READY identifier (bare in the default schema,
    // "schema.name" otherwise) — whatever completion inserts just works.
    let schema = [];

    // Per-dialect bits pushed from C# alongside the schema:
    // { quote: '['|'`'|'"', ownerHint: 'dbo'|null, topSnippet: '...' }
    const dialect = { quote: '[', ownerHint: 'dbo', topSnippet: 'SELECT TOP 20 *\nFROM ' };

    // Registered once: Monaco keeps completion providers per language globally,
    // so re-registering on every page visit would stack duplicates.
    let completionRegistered = false;

    // Draft autosave, debounced: a save per keystroke would hammer the main
    // thread; 800 ms after the last keystroke is plenty. The key is PER PROFILE
    // so switching connections swaps drafts too.
    let draftKey = 'dbm.draft';
    let saveTimer = null;

    function storageKeyFor(profileId) {
        return 'dbm.draft.' + profileId;
    }

    function saveTextDebounced(text) {
        clearTimeout(saveTimer);
        saveTimer = setTimeout(() => localStorage.setItem(draftKey, text), 800);
    }

    function findEditor(divId) {
        const entry = editors.get(divId);
        return entry ? entry.editor : null;
    }

    // --- Identifier quoting (dialect-aware) -----------------------------------
    // Bare identifiers insert as-is; anything else is wrapped in the dialect's
    // quote char with the right escaping ([..]] / `..`` / ".."").
    function sqlIdent(name) {
        if (/^[\w#]+$/.test(name)) return name;
        switch (dialect.quote) {
            case '`': return '`' + name.replace(/`/g, '``') + '`';
            case '"': return '"' + name.replace(/"/g, '""') + '"';
            default:  return '[' + name.replace(/\]/g, ']]') + ']';
        }
    }

    // A schema-qualified name ("myschema.mytable", plain unquoted parts) quotes
    // each dot-separated part on its own: "myschema"."mytable".
    function qualifiedIdent(name) {
        return name.split('.').map(sqlIdent).join('.');
    }

    // --- Monaco SQL IntelliSense ----------------------------------------------
    // Monaco's built-in SQL language ships keyword highlighting + keyword
    // completion, but knows nothing about THIS database. The providers below
    // add schema-aware suggestions on top: tables/views/procs, alias-aware
    // column completion, context detection (FROM/JOIN/ON/EXEC), snippets and
    // hover info. Keywords keep coming from the built-in provider; the two
    // lists are merged automatically by Monaco.

    // Lowercased object name -> schema object. Rebuilt on every setSchema.
    const tableIndex = new Map();

    // Words that can follow "FROM <table>" WITHOUT being an alias. Needed so
    // "FROM Products WHERE" does not register "WHERE" as alias "where".
    const SQL_STOPWORDS = new Set([
        'where', 'on', 'order', 'group', 'inner', 'left', 'right', 'full',
        'outer', 'cross', 'join', 'set', 'values', 'having', 'limit', 'union',
        'with', 'using', 'when', 'then', 'else', 'and', 'or', 'not', 'is', 'in',
        'between', 'like', 'select', 'from', 'as', 'desc', 'asc', 'top',
        'distinct', 'case', 'end', 'by', 'option', 'for', 'apply', 'only',
        'offset', 'fetch'
    ]);

    // One identifier in any of the three quoting styles, or bare.
    const IDENT = '(?:\\[([^\\]]+)\\]|`([^`]+)`|"([^"]+)"|([A-Za-z_][\\w#]*))';

    // Scans the document for "FROM [<owner>.]<table> [alias]" and the same for
    // JOIN. Returns { aliases: Map(lowerAlias -> qualifiedName), tables: [name] }.
    // Deliberately regex-based, not a real parser: an admin editor needs useful
    // hints, not a SQL compiler. Both the bare and the owner-qualified form are
    // collected, because objects outside the default schema are indexed under
    // their qualified name while default-schema objects are indexed bare.
    function extractFromClauses(text) {
        const aliases = new Map();
        const tables = [];
        const re = new RegExp(
            '(?:FROM|JOIN)\\s+(?:' + IDENT + '\\s*\\.\\s*)?' + IDENT + '\\s*(?:AS\\s+)?([A-Za-z_][\\w]*)?',
            'gi');
        let m;
        while ((m = re.exec(text)) !== null) {
            const owner = m[1] || m[2] || m[3] || m[4];
            const tableName = m[5] || m[6] || m[7] || m[8];
            if (!tableName) continue;
            const qualified = owner ? owner + '.' + tableName : null;
            tables.push(tableName);
            if (qualified) tables.push(qualified);
            if (m[9] && !SQL_STOPWORDS.has(m[9].toLowerCase()))
                aliases.set(m[9].toLowerCase(), qualified || tableName);
        }
        return { aliases: aliases, tables: tables };
    }

    function columnsOf(tableName) {
        const obj = tableIndex.get(String(tableName).toLowerCase());
        return obj ? obj.columns : [];
    }

    function suggestColumns(list, suggestions, range, prefix) {
        for (const col of list) {
            suggestions.push({
                label: col.name,
                kind: monaco.languages.CompletionItemKind.Field,
                detail: col.dataType,
                insertText: (prefix ? prefix + '.' : '') + sqlIdent(col.name),
                range: range
            });
        }
    }

    function suggestObjects(suggestions, range, kinds) {
        for (const obj of schema) {
            if (!kinds.includes(obj.kind)) continue;
            // Procedures and functions both surface as "Function" completion items
            // (Monaco has no Procedure kind); tables/views as "Class". For a
            // function, Columns holds its PARAMETERS, so the count/detail wording
            // switches to params.
            const isFn = obj.kind === 'Function';
            suggestions.push({
                label: obj.name,
                kind: (obj.kind === 'Procedure' || isFn)
                    ? monaco.languages.CompletionItemKind.Function
                    : monaco.languages.CompletionItemKind.Class,
                detail: isFn
                    ? obj.kind + (obj.columns.length ? ` (${obj.columns.length} param.)` : '')
                    : obj.kind + (obj.columns.length ? ` (${obj.columns.length} col.)` : ''),
                documentation: obj.columns.length
                    ? obj.columns.map(c => `${c.name} ${c.dataType}`).join('\n').slice(0, 800)
                    : undefined,
                // Qualified names need per-part quoting ("myschema"."mytable").
                insertText: qualifiedIdent(obj.name),
                range: range
            });
        }
    }

    function suggestSnippets(suggestions, range) {
        const snippetRule = monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet;
        const snippets = [
            { label: 'sel', detail: 'SELECT * FROM', text: 'SELECT *\nFROM ' },
            { label: 'top20', detail: 'TOP / LIMIT 20', text: dialect.topSnippet },
            { label: 'ijoin', detail: 'INNER JOIN', text: 'INNER JOIN ${1:table} ON ${2:condition}' },
            { label: 'ljoin', detail: 'LEFT JOIN', text: 'LEFT JOIN ${1:table} ON ${2:condition}' },
            { label: 'upd', detail: 'UPDATE', text: 'UPDATE ${1:table}\nSET ${2:column} = ${3:value}\nWHERE ${4:condition};' },
            { label: 'ins', detail: 'INSERT INTO', text: 'INSERT INTO ${1:table} (${2:columns})\nVALUES (${3:values});' },
            { label: 'del', detail: 'DELETE', text: 'DELETE FROM ${1:table}\nWHERE ${2:condition};' }
        ];
        for (const s of snippets) {
            suggestions.push({
                label: s.label,
                kind: monaco.languages.CompletionItemKind.Snippet,
                detail: s.detail,
                insertText: s.text,
                insertTextRules: snippetRule,
                range: range
            });
        }
    }

    function registerCompletion() {
        if (completionRegistered) return;
        completionRegistered = true;

        monaco.languages.registerCompletionItemProvider('sql', {
            triggerCharacters: ['.'],

            provideCompletionItems: (model, position) => {
                const word = model.getWordUntilPosition(position);
                // IRange uses startLineNumber/endLineNumber/startColumn/endColumn —
                // NOT {from, to}. The {from, to} shape silently produced NaN spans,
                // which is why suggestions never rendered even with schema loaded.
                const lineUntil = model.getValueInRange({
                    startLineNumber: position.lineNumber,
                    endLineNumber: position.lineNumber,
                    startColumn: 1,
                    endColumn: position.column
                });

                const range = {
                    startLineNumber: position.lineNumber,
                    endLineNumber: position.lineNumber,
                    startColumn: word.startColumn,
                    endColumn: word.endColumn
                };
                const suggestions = [];

                // --- Context 1: "something." → columns of that something. ---
                const beforeDot = lineUntil.match(/([A-Za-z_][\w#]*)\.\s*$/);
                if (beforeDot) {
                    const owner = beforeDot[1].toLowerCase();
                    const from = extractFromClauses(model.getValue());

                    // Alias wins ("FROM Products p … p."): resolve through the
                    // alias map first, then fall back to a literal table name.
                    const tableName = from.aliases.get(owner) || owner;
                    if (tableIndex.has(tableName)) {
                        suggestColumns(columnsOf(tableName), suggestions, range);
                        return { suggestions: suggestions };
                    }
                    // Owner hint (dbo. in SSMS) → schema objects, no-op where the
                    // dialect has no such convention.
                    if (dialect.ownerHint && owner === dialect.ownerHint.toLowerCase()) {
                        suggestObjects(suggestions, range, ['Table', 'View']);
                        return { suggestions: suggestions };
                    }
                    return { suggestions: suggestions };
                }

                // --- Context detection by last keyword on the line. ---
                const lastWord = lineUntil.slice(0, word.startColumn - 1)
                    .match(/([A-Za-z_][\w#]*)\s*$/);
                const lastToken = lastWord ? lastWord[1].toUpperCase() : '';

                const from = extractFromClauses(model.getValue());

                if (lastToken === 'FROM' || lastToken === 'JOIN' || lastToken === 'INTO') {
                    // Table position: offer tables + views (INTO can target both).
                    suggestObjects(suggestions, range, ['Table', 'View']);
                    return { suggestions: suggestions };
                }
                if (lastToken === 'EXEC' || lastToken === 'EXECUTE' || lastToken === 'CALL') {
                    suggestObjects(suggestions, range, ['Procedure']);
                    return { suggestions: suggestions };
                }
                if (['SELECT', 'WHERE', 'AND', 'OR', 'ON', 'ORDER', 'GROUP', 'BY', 'SET', 'HAVING'].includes(lastToken)) {
                    // Value position: columns of every table referenced in FROM/JOIN
                    // clauses, plus the tables themselves (for subqueries), plus
                    // snippets. Keywords come from Monaco's built-in provider.
                    const seen = new Set();
                    for (const t of from.tables) {
                        for (const col of columnsOf(t)) {
                            if (seen.has(col.name.toLowerCase())) continue;
                            seen.add(col.name.toLowerCase());
                            suggestions.push({
                                label: col.name,
                                kind: monaco.languages.CompletionItemKind.Field,
                                detail: col.dataType + ' — ' + t,
                                insertText: sqlIdent(col.name),
                                range: range
                            });
                        }
                    }
                    suggestObjects(suggestions, range, ['Table', 'View', 'Function']);
                    suggestSnippets(suggestions, range);
                    return { suggestions: suggestions };
                }

                // --- Default: tables + views + procs + functions + snippets. ---
                suggestObjects(suggestions, range, ['Table', 'View', 'Procedure', 'Function']);
                suggestSnippets(suggestions, range);
                return { suggestions: suggestions };
            }
        });

        // Hover: table/column name under the mouse shows what it is. Cheap to
        // compute from the same index the completion provider uses.
        monaco.languages.registerHoverProvider('sql', {
            provideHover: (model, position) => {
                const word = model.getWordAtPosition(position);
                if (!word) return null;
                const hit = tableIndex.get(word.word.toLowerCase());
                if (!hit) return null;

                if (hit.columns.length > 0) {
                    const lines = hit.columns.map(c => `  ${c.name} ${c.dataType}`);
                    const shown = lines.slice(0, 25).join('\n')
                        + (lines.length > 25 ? `\n  … +${lines.length - 25}` : '');
                    return {
                        contents: [
                            { value: `**${hit.kind} ${hit.name}**` },
                            { value: '```sql\n' + shown + '\n```' }
                        ]
                    };
                }
                return { contents: [{ value: `**${hit.kind}** ${hit.name}` }] };
            }
        });
    }

    return {
        // Creates the editor inside the div with the given id. Called from
        // OnAfterRenderAsync(firstRender) — by then the div exists in the DOM.
        // dotnetRef: DotNetObjectReference of the page; used for OnRunRequested.
        // profileId: connection profile the draft autosave keys off.
        init: (divId, dotnetRef, isDark, profileId) => {
            // Path where the AMD loader can find the rest of Monaco, matching the
            // pinned version loaded in App.razor. Must stay in sync with it.
            require.config({
                paths: { vs: 'https://cdn.jsdelivr.net/npm/monaco-editor@0.52.2/min/vs' }
            });
            require(['vs/editor/editor.main'], () => {
                const entry = editors.get(divId);
                if (entry) return; // double init guard (Blazor re-renders)

                draftKey = storageKeyFor(profileId);

                const editor = monaco.editor.create(document.getElementById(divId), {
                    value: localStorage.getItem(draftKey) || '-- Write a query and press Run (or F5)',
                    language: 'sql',                        // built-in SQL tokenizer: SSMS-like keyword coloring
                    theme: isDark ? 'vs-dark' : 'vs',        // matches the app's Bootstrap dark mode at init time
                    automaticLayout: true,                  // ResizeObserver: editor reflows when the split divider moves
                    minimap: { enabled: false },
                    fontSize: 13,
                    scrollBeyondLastLine: false,
                    renderLineHighlight: 'all',
                    tabSize: 4,
                    // IntelliSense behavior: auto-popup while typing identifiers,
                    // popup on the '.' trigger character, Tab accepts the top item.
                    quickSuggestions: { other: true, comments: false, strings: true },
                    suggestOnTriggerCharacters: true,
                    tabCompletion: 'on'
                });

                // Keybindings. addCommand registers a REAL binding, so Monaco
                // also prevents the browser default (F5 = page refresh) while the
                // editor has focus — same expectation as SSMS.
                const run = () => dotnetRef.invokeMethodAsync('OnRunRequested');
                editor.addCommand(monaco.KeyCode.F5, run);
                editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyE, run);
                editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, run);

                editor.onDidChangeModelContent(() =>
                    saveTextDebounced(editor.getValue()));

                editors.set(divId, { editor: editor, dotnetRef: dotnetRef });
                registerCompletion();

                // Tell Blazor the editor is ready, so the page can push schema
                // and enable the Run button (which waits on the editor existing).
                dotnetRef.invokeMethodAsync('OnEditorReady');
            });
        },

        // Live theme flip, called by dbmSite.toggleTheme (which must not
        // reload the page — the vault session is circuit-scoped). Monaco
        // picked vs/vs-dark at init; this retints every mounted instance.
        // No-op when the engine was never downloaded (editor page unopened).
        setTheme: (isDark) => {
            if (typeof monaco === 'undefined') return;
            monaco.editor.setTheme(isDark ? 'vs-dark' : 'vs');
        },

        // Schema + dialect push from C#: serialized payload
        // { schema: List<SchemaObject>, dialect: DialectConfig } (camelCase,
        // Kind as text). Rebuilds the lowercase lookup index used by the
        // completion + hover providers.
        setSchema: (json) => {
            const payload = JSON.parse(json);
            schema = payload.schema || [];
            if (payload.dialect) {
                if (payload.dialect.quote) dialect.quote = payload.dialect.quote;
                dialect.ownerHint = payload.dialect.ownerHint || null;
                if (payload.dialect.topSnippet) dialect.topSnippet = payload.dialect.topSnippet;
            }
            tableIndex.clear();
            for (const obj of schema)
                tableIndex.set(obj.name.toLowerCase(), obj);
        },

        // Connection switched: keep the editor, swap the draft the autosave
        // writes to, and load the new profile's draft as the content.
        loadDraft: (divId, profileId) => {
            draftKey = storageKeyFor(profileId);
            const editor = findEditor(divId);
            if (!editor) return;
            editor.setValue(localStorage.getItem(draftKey) || '');
            editor.focus();
        },

        // Inserts text at the caret (sidebar table/column clicks) and focuses
        // back, so the user lands in the editor ready to keep typing.
        insertAtCursor: (divId, text) => {
            const editor = findEditor(divId);
            if (!editor) return;
            const selection = editor.getSelection();
            editor.executeEdits('sidebar', [{
                range: selection,
                text: text,
                forceMoveMarkers: true
            }]);
            // Undo stop: insert becomes ONE undo step instead of merging with
            // the user's typing history.
            editor.pushUndoStop();
            editor.focus();
        },

        // Whole-content replace, used by the sidebar "TOP / LIMIT 20" quick action.
        setValue: (divId, text) => {
            const editor = findEditor(divId);
            if (!editor) return;
            editor.setValue(text);
            editor.focus();
        },

        getValue: (divId) => findEditor(divId)?.getValue() ?? '',

        // --- Split-pane divider drag -------------------------------------------
        // The divider between editor and results. JS handles the drag directly
        // (pointer capture, style update) because routing every mousemove through
        // the SignalR circuit would make the drag visibly laggy. C# only learns
        // the final percentage via OnSplitChanged, to persist it in component state.
        initSplit: (dividerId, paneId, dotnetRef) => {
            const divider = document.getElementById(dividerId);
            const pane = document.getElementById(paneId);
            if (!divider || !pane) return;

            divider.addEventListener('pointerdown', (e) => {
                e.preventDefault();
                divider.setPointerCapture(e.pointerId);
                document.body.style.cursor = 'row-resize';
                divider.style.userSelect = 'none';

                const onMove = (e) => {
                    const parent = pane.parentElement.getBoundingClientRect();
                    // The results pane sits BELOW the divider and grows downward
                    // from it, so its height = container MINUS the divider offset.
                    // (flex-basis of the bottom pane is its height, not its top
                    // offset — that inversion was the original flipped-drag bug.)
                    // Clamped 15–85% so neither pane can be dragged to zero.
                    const pct = 100 - ((e.clientY - parent.top) / parent.height) * 100;
                    const clamped = Math.min(85, Math.max(15, pct));
                    pane.style.flexBasis = clamped + '%';
                };
                const onUp = (e) => {
                    divider.releasePointerCapture(e.pointerId);
                    divider.removeEventListener('pointermove', onMove);
                    divider.removeEventListener('pointerup', onUp);
                    document.body.style.cursor = '';
                    // Report final position; StateHasChanged keeps it if Blazor
                    // ever re-renders the inline style from C# state.
                    dotnetRef.invokeMethodAsync('OnSplitChanged', parseFloat(pane.style.flexBasis));
                };
                divider.addEventListener('pointermove', onMove);
                divider.addEventListener('pointerup', onUp);
            });
        },

        // --- Sidebar resize drag -----------------------------------------------
        // Same pointer-capture pattern as the vertical divider, but horizontal:
        // the pointer X position directly becomes the sidebar width in px.
        initSidebarSplit: (draggerId, sidebarId, dotnetRef) => {
            const dragger = document.getElementById(draggerId);
            const sidebar = document.getElementById(sidebarId);
            if (!dragger || !sidebar) return;

            dragger.addEventListener('pointerdown', (e) => {
                e.preventDefault();
                dragger.setPointerCapture(e.pointerId);
                document.body.style.cursor = 'col-resize';
                dragger.style.userSelect = 'none';

                const onMove = (e) => {
                    const parent = sidebar.parentElement.getBoundingClientRect();
                    // Width = pointer distance from the row's left edge, clamped
                    // so the sidebar can neither vanish nor eat the editor.
                    const px = Math.min(480, Math.max(180, e.clientX - parent.left));
                    sidebar.style.width = px + 'px';
                };
                const onUp = (e) => {
                    dragger.releasePointerCapture(e.pointerId);
                    dragger.removeEventListener('pointermove', onMove);
                    dragger.removeEventListener('pointerup', onUp);
                    document.body.style.cursor = '';
                    dotnetRef.invokeMethodAsync('OnSidebarSplitChanged', parseFloat(sidebar.style.width));
                };
                dragger.addEventListener('pointermove', onMove);
                dragger.addEventListener('pointerup', onUp);
            });
        },

        dispose: (divId) => {
            const entry = editors.get(divId);
            if (entry) {
                entry.editor.dispose();
                editors.delete(divId);
            }
        }
    };
})();