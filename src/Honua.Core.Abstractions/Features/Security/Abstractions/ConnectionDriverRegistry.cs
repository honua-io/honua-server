// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Security.Abstractions;

public sealed class ConnectionDriverRegistry : IConnectionDriverRegistry
{
    private readonly Dictionary<string, IConnectionDriver> _byProvider;

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

    public bool Supports(string? provider) => Find(provider) is not null;

    public IConnectionDriver? Find(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        return _byProvider.TryGetValue(DataProviderNames.Normalize(provider), out var driver) ? driver : null;
    }
}
