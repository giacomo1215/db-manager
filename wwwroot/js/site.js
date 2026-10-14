// Theme helpers for db-manager. The <html data-bs-theme> attribute is rendered
// SERVER-side from the persisted preference (App.razor), so there is never a
// flash and the login page is themed too. Monaco picks vs/vs-dark from this same
// attribute at editor init. Nothing here touches localStorage anymore.
window.dbmSite = (() => {
    return {
        getTheme: () => document.documentElement.getAttribute('data-bs-theme') === 'dark' ? 'dark' : 'light',

        // Blob download for backups/exports. Blazor can't trigger a "Save as"
        // itself, so the server hands the text over the circuit and the
        // browser saves it.
        downloadTextFile: (fileName, mimeType, content) => {
            const blob = new Blob([content], { type: mimeType });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = fileName;
            document.body.appendChild(a);
            a.click();
            a.remove();
            URL.revokeObjectURL(url);
        },

        // Chunked download for large database dumps: interop calls ride SignalR
        // (max message size 32 KB), so the server streams the script in small
        // chunks and the Blob is only assembled at the end.
        _download: null,
        beginDownload: (fileName, mimeType) => {
            dbmSite._download = { fileName, mimeType, parts: [] };
        },
        appendDownloadChunk: (text) => {
            if (dbmSite._download) dbmSite._download.parts.push(text);
        },
        finishDownload: () => {
            const d = dbmSite._download;
            dbmSite._download = null;
            if (!d) return;
            dbmSite.downloadTextFile(d.fileName, d.mimeType, d.parts.join(''));
        },
        abortDownload: () => {
            dbmSite._download = null;
        },

        // Live theme switch: every surface is var-driven (--dbm-*/--bs-*
        // tokens hang off <html data-bs-theme>), so one attribute swap
        // restyles the whole app instantly. NO location.reload() here — a
        // reload tears down the circuit, and the vault unlock state is
        // circuit-scoped (SessionVault), so it would lock the user out.
        // Persistence is the caller's job (MainLayout/Settings write the
        // setting to the SQLite store); this only applies it to the DOM and
        // keeps Monaco in sync so the two can never disagree.
        applyTheme: (next) => {
            document.documentElement.setAttribute('data-bs-theme', next === 'dark' ? 'dark' : 'light');
            // Monaco picks vs/vs-dark once at editor init; flip it live too.
            // The engine only exists after the SQL editor page was opened.
            if (window.dbSqlEditor) dbSqlEditor.setTheme(next === 'dark');
        },

        // Native <dialog> helpers: showModal() gives the modal + ::backdrop that
        // a plain "open" attribute cannot. Guarded so a double call never throws.
        openDialog: (id) => {
            const el = document.getElementById(id);
            if (el && !el.open) el.showModal();
        },
        closeDialog: (id) => {
            const el = document.getElementById(id);
            if (el && el.open) el.close();
        }
    };
})();