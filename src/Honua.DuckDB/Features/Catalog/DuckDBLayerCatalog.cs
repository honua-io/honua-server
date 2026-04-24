// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.DuckDB;
using Microsoft.Extensions.Logging;

namespace Honua.DuckDB.Features.Catalog;

/// <summary>
/// Configuration-driven layer catalog for DuckDB.
/// Layer and service definitions are read from <see cref="DuckDBOptions"/> at startup.
/// </summary>
internal sealed class DuckDBLayerCatalog : ILayerCatalog
{
    private readonly FrozenDictionary<int, LayerDefinition> _layers;
    private readonly FrozenDictionary<string, ServiceDefinition> _services;

    public DuckDBLayerCatalog(
        IEnumerable<LayerDefinition> layers,
        IEnumerable<ServiceDefinition> services,
        ILogger<DuckDBLayerCatalog> logger)
    {
        _layers = layers.ToFrozenDictionary(l => l.Id);
        _services = services.ToFrozenDictionary(
            s => s.Name,
            StringComparer.OrdinalIgnoreCase);

        DuckDbLog.LayerCatalogInitialized(logger, _layers.Count, _services.Count);
    }

    /// <inheritdoc />
    public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_layers.TryGetValue(layerId, out var layer) ? layer : null);
    }

    /// <inheritdoc />
    public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_layers.Values.ToArray());
    }

    /// <inheritdoc />
    public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_services.TryGetValue(serviceName, out var service) ? service : null);
    }

    /// <inheritdoc />
    public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_services.Values.ToArray());
    }

    /// <inheritdoc />
    public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_layers.ContainsKey(layerId));
    }

    /// <inheritdoc />
    public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_services.ContainsKey(serviceName));
    }

    /// <inheritdoc />
    public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
    {
        // DuckDB provider does not support relationships in V1
        return Task.FromResult<Relationship?>(null);
    }

    /// <inheritdoc />
    public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Array.Empty<Relationship>());
    }
}
