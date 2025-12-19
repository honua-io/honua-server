// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Npgsql;

namespace Honua.Postgres.Features.Catalog;

/// <summary>
/// Simple PostgreSQL implementation of layer catalog for testing compilation
/// </summary>
internal sealed class PostgresLayerCatalogSimple : ILayerCatalog
{
#pragma warning disable IDE0052 // Remove unread private members - Will be used in full implementation
    private readonly NpgsqlDataSource _dataSource;
#pragma warning restore IDE0052

    public PostgresLayerCatalogSimple(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        // Simple stub implementation
        return Task.FromResult<LayerDefinition?>(LayerDefinition.CreateBasic(
            layerId,
            $"Layer {layerId}",
            GeometryType.Point,
            SpatialReference.WGS84));
    }

    public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<LayerDefinition[]>([]);
    }

    public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ServiceDefinition?>(null);
    }

    public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ServiceDefinition[]>([]);
    }

    public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }
}
