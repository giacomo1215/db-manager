using System.Threading;
using System.Threading.Tasks;
using DbManager.Models;

namespace DbManager.Abstractions
{
    /// <summary>
    /// Database-level backup coordinator: resolves the engine's connector and
    /// dumper, owns connection/temp-file/timeout/progress plumbing, and runs
    /// imports. The connection-vault backup (IBackupService) is a different
    /// concern — this one dumps the connected DATABASE itself.
    ///
    /// Never-throw contract like ISqlEditorService: errors land in the result
    /// objects, never in a Blazor event handler.
    /// </summary>
    public interface IDbDumpService
    {
        /// <summary>Exports the profile's database to a temp .sql file (returned in DumpResult).</summary>
        Task<DumpResult> DumpAsync(ConnectionProfile profile, DumpOptions options,
            IProgress<DumpProgress>? progress, CancellationToken ct = default);

        /// <summary>Pass-1 scan of a script file: statement count for the confirm gate.</summary>
        Task<ImportPreview> AnalyzeScriptAsync(string scriptPath, ConnectionProfile profile, CancellationToken ct = default);

        /// <summary>Executes a script file against the profile's database (whole script, one connection).</summary>
        Task<ImportResult> ImportAsync(ConnectionProfile profile, string scriptPath,
            IProgress<DumpProgress>? progress, CancellationToken ct = default);
    }
}