// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Default <see cref="IConnectionDriverRegistry"/> backed by every <see cref="IConnectionDriver"/> the server
/// composes. Drivers are indexed by their normalized <see cref="IConnectionDriver.Provider"/>; lookups
/// normalize the requested provider through <see cref="DataProviderNames.Normalize"/> so callers can pass the
/// raw stored/requested value (e.g. "postgis", "mssql", "mariadb").
/// </summary>
public sealed class ConnectionDriverRegistry : IConnectionDriverRegistry
{
    private readonly Dictionary<string, IConnectionDriver> _byProvider;

    /// <summary>
    /// Initializes the registry by indexing each supplied driver under its normalized provider key.
    /// </summary>
    /// <param name="drivers">The connection drivers composed by the host.</param>
    public ConnectionDriverRegistry(IEnumerable<IConnectionDriver> drivers)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        var map = new Dictionary<string, IConnectionDriver>(StringComparer.OrdinalIgnoreCase);
        foreach (var driver in drivers)
        {
            // Last registration wins, mirroring DI override semantics; normalize so e.g. "postgis"/"postgresql"
            // map onto the same canonical key the driver advertises.
            map[DataProviderNames.Normalize(driver.Provider)] = driver;
        }

        _byProvider = map;
    }

    /// <inheritdoc />
    public bool Supports(string? provider) => Find(provider) is not null;

    /// <inheritdoc />
    public IConnectionDriver? Find(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        return _byProvider.TryGetValue(DataProviderNames.Normalize(provider), out var driver) ? driver : null;
    }
}
