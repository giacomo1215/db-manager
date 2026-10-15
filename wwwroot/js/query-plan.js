// db-manager execution-plan helpers — SSMS-style graphical plan.
//
// WHY JS at all: html-query-plan (the global `QP`) is a browser library that
// parses SQL Server ShowPlan XML into an operator graph (nodes, arrows, cost
// %, row estimates, operator icons). Blazor interop hands it a container id
// and the raw plan XML; everything visual happens here.
//
// The plan XML is SQL Server only — the other engines (mysql/postgres/sqlite)
// return a text plan, which the page keeps rendering as preformatted text.
window.dbmQueryPlan = (() => {
    return {
        // True when the container already holds a rendered graph. Blazor recreates
        // the results pane when the user switches query tabs, leaving an empty
        // container behind an unchanged render key — the caller uses this to tell
        // "already drawn" from "present but blank" and redraw accordingly.
        isRendered(containerId) {
            const el = document.getElementById(containerId);
            return !!el && el.childElementCount > 0;
        },

        // Renders `planXml` into the element with `containerId`.
        // Returns an empty string on success, or a human-readable error when the
        // library is unavailable (e.g. CDN offline) or the XML cannot be parsed —
        // the caller then falls back to the text view and can surface the reason.
        render(containerId, planXml) {
            const el = document.getElementById(containerId);
            if (!el)
                return "plan container not found";
            if (typeof QP === "undefined" || typeof QP.showPlan !== "function")
                return "execution-plan renderer not loaded (html-query-plan)";

            // Re-rendering into a stale graph would stack two plans; start clean.
            el.innerHTML = "";
            try {
                QP.showPlan(el, planXml);
                return "";
            } catch (e) {
                console.error("Execution plan render failed", e);
                el.innerHTML = "";
                return (e && e.message) ? e.message : String(e);
            }
        }
    };
})();
