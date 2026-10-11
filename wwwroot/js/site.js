// Theme persistence for db-manager. The <html data-bs-theme> attribute is set
// pre-paint by the inline script in App.razor (reads the same key) so a dark
// session never flashes light on reload. Monaco picks vs/vs-dark from
// getTheme() at editor init.
window.dbmSite = (() => {
    const KEY = 'dbm.theme';

    return {
        getTheme: () => localStorage.getItem(KEY) === 'dark' ? 'dark' : 'light',

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
        toggleTheme: () => {
            const next = localStorage.getItem(KEY) === 'dark' ? 'light' : 'dark';
            localStorage.setItem(KEY, next);
            document.documentElement.setAttribute('data-bs-theme', next);
            // Monaco picks vs/vs-dark once at editor init; flip it live too.
            // The engine only exists after the SQL editor page was opened.
            if (window.dbSqlEditor) dbSqlEditor.setTheme(next === 'dark');
        }
    };
})();