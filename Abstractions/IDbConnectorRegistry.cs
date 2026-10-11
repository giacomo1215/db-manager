using System.Collections.Generic;
using DbManager.Models;

namespace DbManager.Abstractions
{
    /// <summary>
    /// Lookup of registered engines, built from the DI-registered connector
    /// singletons. Connection profiles reference engines by EngineKey; this is
    /// where that key resolves back to behavior.
    /// </summary>
    public interface IDbConnectorRegistry
    {
        /// <summary>All registered engines, in registration order.</summary>
        IReadOnlyList<IDbConnector> Connectors { get; }

        /// <summary>Connector for a profile's engine, or null when the key is unknown.</summary>
        IDbConnector? Resolve(string engineKey);
    }
}