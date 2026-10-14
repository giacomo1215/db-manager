// db-manager result-grid helpers. Column resize, auto-fit and clipboard for the
// results table. Column widths are owned by C# state and rendered into a
// <colgroup>; this file only writes the live <col> during a drag (routing every
// pointermove through the SignalR circuit would lag) and reports the final width
// back through a JSInvokable, mirroring the split-divider pattern in sql-editor.js.
//
// A single pointerdown listener is delegated on the (stable) table element, so
// toggling the filter row — which re-renders the header — never detaches resizing.
window.dbResultGrid = (() => {
    const MIN_W = 40;
    const MAX_W = 1600;
    // Cell padding (2 × 10px) + the resize handle's width: kept in sync with
    // ResultGrid.razor.css so an auto-fit column never clips its content.
    const AUTO_EXTRA = 28;

    // Offscreen 2d context for text measurement (monospace grid font).
    const measureCanvas = document.createElement('canvas');
    const measureCtx = measureCanvas.getContext('2d');

    function clamp(v, min, max) {
        return Math.min(max, Math.max(min, v));
    }

    function colWidth(col) {
        const w = parseFloat(col.style.width);
        return isNaN(w) ? col.getBoundingClientRect().width : w;
    }

    // Table width must equal the sum of column widths under table-layout:fixed,
    // otherwise the browser redistributes the slack and columns jump.
    function syncTableWidth(table) {
        const cols = table.querySelector('colgroup').children;
        let sum = 0;
        for (const col of cols) sum += colWidth(col);
        table.style.width = Math.round(sum) + 'px';
    }

    // getComputedStyle().font can be "" when the shorthand is not fully
    // representable, which would silently keep the previous font. Rebuild the
    // string from the longhands when that happens.
    function fontOf(el) {
        const cs = getComputedStyle(el);
        if (cs.font) return cs.font;
        return `${cs.fontStyle} ${cs.fontWeight} ${cs.fontSize} ${cs.fontFamily}`;
    }

    function measure(text, font) {
        if (!text) return 0;
        measureCtx.font = font;
        return measureCtx.measureText(text).width;
    }

    return {
        // Delegated pointer-drag resize. Idempotent: safe to call on every render.
        initResize: (gridId, dotnetRef) => {
            const table = document.getElementById(gridId);
            if (!table || table.dataset.dbmResizeWired === '1') return;
            table.dataset.dbmResizeWired = '1';

            table.addEventListener('pointerdown', (e) => {
                const handle = e.target.closest('.dbm-col-resize');
                if (!handle) return;
                e.preventDefault();
                e.stopPropagation();

                const th = handle.parentElement;
                const index = Array.prototype.indexOf.call(th.parentNode.children, th);
                const cols = table.querySelector('colgroup').children;
                if (index < 0 || index >= cols.length) return;

                handle.setPointerCapture(e.pointerId);
                const startX = e.clientX;
                const startW = colWidth(cols[index]);
                document.body.style.cursor = 'col-resize';
                document.body.style.userSelect = 'none';

                const onMove = (ev) => {
                    const w = clamp(startW + (ev.clientX - startX), MIN_W, MAX_W);
                    cols[index].style.width = w + 'px';
                    syncTableWidth(table);
                };
                const onUp = (ev) => {
                    handle.releasePointerCapture(ev.pointerId);
                    handle.removeEventListener('pointermove', onMove);
                    handle.removeEventListener('pointerup', onUp);
                    document.body.style.cursor = '';
                    document.body.style.userSelect = '';
                    syncTableWidth(table);
                    dotnetRef.invokeMethodAsync('OnColumnResized', index, colWidth(cols[index]));
                };
                handle.addEventListener('pointermove', onMove);
                handle.addEventListener('pointerup', onUp);
            });
        },

        // Auto-fit a column to the widest of its header and its longest cell.
        // Measuring the DOM is impossible under virtualization (only visible
        // rows exist), so C# passes the longest strings and we measure those
        // exactly with the grid's real font. Returns the new width.
        autofit: (gridId, index, header, longestCell) => {
            const table = getId(gridId);
            if (!table) return 0;
            const cols = table.querySelector('colgroup').children;
            const th = table.querySelector('thead th:nth-child(' + (index + 1) + ')');
            if (!th || index >= cols.length) return 0;

            const headerFont = fontOf(th);
            const tdSample = table.querySelector('tbody td');
            const cellFont = tdSample ? fontOf(tdSample) : headerFont;

            const width = Math.max(measure(header, headerFont), measure(longestCell, cellFont)) + AUTO_EXTRA;
            const clamped = clamp(Math.ceil(width), MIN_W, MAX_W);
            cols[index].style.width = clamped + 'px';
            syncTableWidth(table);
            return clamped;
        },

        // Copy text to the clipboard. Uses the async Clipboard API (available in
        // secure contexts — true for the desktop build's 127.0.0.1 origin) and
        // falls back to a hidden textarea for the plain-http LAN server build.
        // Clears any selection left by the double-click so it never lingers.
        copyText: (text) => {
            const clearSelection = () => {
                const sel = window.getSelection();
                if (sel) sel.removeAllRanges();
            };
            const fallback = () => {
                try {
                    const ta = document.createElement('textarea');
                    ta.value = text;
                    ta.style.position = 'fixed';
                    ta.style.opacity = '0';
                    document.body.appendChild(ta);
                    ta.select();
                    document.execCommand('copy');
                    ta.remove();
                    clearSelection();
                    return true;
                } catch (err) {
                    return false;
                }
            };

            if (navigator.clipboard && window.isSecureContext) {
                return navigator.clipboard.writeText(text)
                    .then(() => { clearSelection(); return true; }, () => fallback());
            }
            return fallback();
        }
    };

    function getId(id) {
        return document.getElementById(id);
    }
})();
