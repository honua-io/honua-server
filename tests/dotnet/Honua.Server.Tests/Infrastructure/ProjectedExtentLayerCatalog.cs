// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Tests.Infrastructure;

internal sealed class ProjectedExtentLayerCatalog : ILayerCatalog
{
    public const string ServiceId = "projected-metadata";
    public const int LayerId = 2001;
    public const int LayerSrid = 26910;

    private readonly LayerDefinition _layer;
    private readonly ServiceDefinition _service;

    public ProjectedExtentLayerCatalog()
    {
        var fields = new[]
        {
            new FieldDefinition("objectid", FieldType.Integer, null, false, null, "Object ID"),
            new FieldDefinition("name", FieldType.String, 255, true, null, "Name"),
            new FieldDefinition("shape", FieldType.Geometry, null, false, null, "Geometry")
        };

        var spatialReference = SpatialReference.Create(LayerSrid);
        var extent = FeatureExtent.Create(500_000d, 4_100_000d, 600_000d, 4_200_000d, LayerSrid);

        _layer = new LayerDefinition(
            Id: LayerId,
            Name: "Projected Metadata Layer",
            Description: "Projected layer used to validate metadata extent fallback paths.",
            GeometryType: GeometryType.Point,
            SpatialReference: spatialReference,
            Fields: fields,
            Extent: extent,
            MinScale: null,
            MaxScale: null,
            DefaultVisibility: true);

        _service = new ServiceDefinition(
            Name: ServiceId,
            Description: "Projected metadata service",
            Layers: [_layer],
            SpatialReference: spatialReference,
            SupportedFormats: Array.Empty<string>(),
            Capabilities: Array.Empty<string>(),
            ServiceExtent: extent);
    }

    public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(layerId == LayerId ? _layer : null);

    public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new[] { _layer });

    public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromResult(
            string.Equals(serviceName, ServiceId, StringComparison.OrdinalIgnoreCase)
                ? _service
                : null);

    public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new[] { _service });

    public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(layerId == LayerId);

    public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Equals(serviceName, ServiceId, StringComparison.OrdinalIgnoreCase));

    public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
        => Task.FromResult<Relationship?>(null);

    public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<Relationship>());
}
