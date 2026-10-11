using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DbManager.Models;

namespace DbManager.Abstractions
{
    /// <summary>
    /// Engine-specific half of the dump feature: emits the whole database as an
    /// engine-native SQL script (DDL, optionally INSERT data) into the writer.
    /// One implementation per engine, registered as a singleton alongside its
    /// connector; the connection's lifetime is managed by IDbDumpService.
    /// </summary>
    public interface IDbDumper
    {
        string EngineKey { get; }

        /// <summary>
        /// Streams the script for the database the (open) connection points at.
        /// Never throws — errors are carried by the coordinator.
        /// </summary>
        Task<DumpStats> DumpAsync(IDbConnector connector, DbConnection conn, DumpOptions options,
            TextWriter output, IProgress<DumpProgress>? progress, CancellationToken ct = default);
    }
}