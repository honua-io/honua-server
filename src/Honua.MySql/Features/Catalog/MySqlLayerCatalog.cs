// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.MySql.Features.Catalog;

/// <summary>
/// Configuration-driven layer catalog for the MySQL/MariaDB provider.
/// Layer and service definitions come from <see cref="MySqlOptions"/> at startup.
/// Relationships are not supported in this slice.
/// </summary>
internal sealed class MySqlLayerCatalog : ILayerCatalog
{
    private readonly FrozenDictionary<int, LayerDefinition> _layers;
    private readonly FrozenDictionary<string, ServiceDefinition> _services;

    public MySqlLayerCatalog(
        IEnumerable<LayerDefinition> layers,
        IEnumerable<ServiceDefinition> services,
        ILogger<MySqlLayerCatalog> logger)
    {
        _layers = layers.ToFrozenDictionary(l => l.Id);
        _services = services.ToFrozenDictionary(
            s => s.Name,
            StringComparer.OrdinalIgnoreCase);

        MySqlLog.LayerCatalogInitialized(logger, _layers.Count, _services.Count);
    }

    /// <inheritdoc />
    public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(_layers.TryGetValue(layerId, out var layer) ? layer : null);

    /// <inheritdoc />
    public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_layers.Values.ToArray());

    /// <inheritdoc />
    public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromResult(_services.TryGetValue(serviceName, out var service) ? service : null);

    /// <inheritdoc />
    public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_services.Values.ToArray());

    /// <inheritdoc />
    public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(_layers.ContainsKey(layerId));

    /// <inheritdoc />
    public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromResult(_services.ContainsKey(serviceName));

    /// <inheritdoc />
    public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
        => Task.FromResult<Relationship?>(null);

    /// <inheritdoc />
    public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<Relationship>());
}
