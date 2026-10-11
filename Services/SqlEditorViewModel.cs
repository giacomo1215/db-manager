using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DbManager.Abstractions;
using DbManager.Models;

namespace DbManager.Services
{
    /// <summary>
    /// Per-circuit state of the SQL Editor, ported from the ZeatDashboard
    /// SqlEditorViewModel: which profile is active and its cached schema (the
    /// catalog does not change during a session, so re-querying it on every
    /// sidebar interaction would be pointless).
    /// </summary>
    public class SqlEditorViewModel : ISqlEditorViewModel
    {
        private readonly ISqlEditorService _service;

        public SqlEditorViewModel(ISqlEditorService service) => _service = service;

        public ConnectionProfile? ActiveProfile { get; private set; }

        public List<SchemaObject> Schema { get; private set; } = [];

        public bool SchemaLoaded { get; private set; }

        public string? SchemaError { get; private set; }

        public async Task LoadSchemaAsync(ConnectionProfile profile, CancellationToken ct = default)
        {
            // Switching profiles invalidates the cache first; re-loading the same
            // profile (circuit reconnection re-runs OnInitializedAsync) is a no-op.
            if (ActiveProfile?.Id == profile.Id)
                return;

            ActiveProfile = profile;
            Schema = [];
            SchemaLoaded = false;
            SchemaError = null;

            try
            {
                Schema = await _service.GetSchemaAsync(profile, ct);
                SchemaLoaded = true;
            }
            catch (Exception ex)
            {
                // Unreachable host, wrong credentials, timeout: same never-throw
                // contract as SqlEditorService.ExecuteAsync — an unhandled
                // exception in a Blazor Server event handler kills the whole
                // circuit. The editor keeps working without a schema.
                SchemaError = ex.Message;
            }
        }

        public Task<SqlEditorResult> ExecuteAsync(string sql, CancellationToken ct = default) =>
            ActiveProfile is null
                ? Task.FromResult(new SqlEditorResult { Error = "No connection selected." })
                : _service.ExecuteAsync(ActiveProfile, sql, ct);

        public bool IsWriteStatement(string sql) =>
            ActiveProfile is not null && _service.IsWriteStatement(ActiveProfile, sql);
    }
}