using System.Collections.Generic;
using System.Linq;
using DbManager.Abstractions;

namespace DbManager.Services
{
    /// <summary>
    /// Built from the DI-registered connector singletons. A new engine needs no
    /// change here — just another AddSingleton&lt;IDbConnector, …&gt; registration.
    /// </summary>
    public class ConnectorRegistry : IDbConnectorRegistry
    {
        public IReadOnlyList<IDbConnector> Connectors { get; }

        public ConnectorRegistry(IEnumerable<IDbConnector> connectors) =>
            Connectors = connectors.ToList();

        public IDbConnector? Resolve(string engineKey) =>
            Connectors.FirstOrDefault(c => c.EngineKey.Equals(engineKey, System.StringComparison.OrdinalIgnoreCase));
    }
}