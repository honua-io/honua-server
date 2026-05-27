// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Features.Catalog;

/// <summary>
/// Configuration-driven layer catalog shared by read-only providers (DuckDB, MySQL/MariaDB).
/// Layer and service definitions are supplied at construction (read from provider options at
/// startup) and exposed through immutable <see cref="FrozenDictionary{TKey, TValue}"/> lookups.
/// Relationships are not supported and always resolve to empty results.
/// </summary>
public sealed class ConfigurationLayerCatalog : ILayerCatalog
{
    private readonly FrozenDictionary<int, LayerDefinition> _layers;
    private readonly FrozenDictionary<string, ServiceDefinition> _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationLayerCatalog"/> class.
    /// </summary>
    /// <param name="layers">Layer definitions, keyed by <see cref="LayerDefinition.Id"/>.</param>
    /// <param name="services">Service definitions, keyed case-insensitively by name.</param>
    /// <param name="onInitialized">
    /// Optional callback invoked once after the lookups are built, with the layer and service
    /// counts. Providers use this to emit their own source-generated initialization log message
    /// so the log category, event id, and wording remain provider-specific.
    /// </param>
    public ConfigurationLayerCatalog(
        IEnumerable<LayerDefinition> layers,
        IEnumerable<ServiceDefinition> services,
        Action<int, int>? onInitialized = null)
    {
        _layers = layers.ToFrozenDictionary(l => l.Id);
        _services = services.ToFrozenDictionary(
            s => s.Name,
            StringComparer.OrdinalIgnoreCase);

        onInitialized?.Invoke(_layers.Count, _services.Count);
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
