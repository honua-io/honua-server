// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Layer catalog for spatial reference integration tests using non-4326 SRIDs.
/// </summary>
public sealed class SpatialReferenceTestLayerCatalog : ILayerCatalog
{
    public const string ServiceId = "srid-test";
    public const int PointLayerId = 101;
    public const int LineLayerId = 102;
    public const int PolygonLayerId = 103;
    public const int LayerSrid = 3857;

    private readonly ServiceDefinition _service;
    private readonly LayerDefinition[] _layers;

    public SpatialReferenceTestLayerCatalog()
    {
        var fields = new[]
        {
            new FieldDefinition("objectid", FieldType.Integer, null, false, null, "Object ID"),
            new FieldDefinition("name", FieldType.String, 255, true, null, "Name"),
            new FieldDefinition("shape", FieldType.Geometry, null, false, null, "Geometry")
        };

        var spatialReference = new SpatialReference(LayerSrid);

        _layers =
        [
            new LayerDefinition(
                PointLayerId,
                "SRID Test Points",
                "Point layer for SRID testing",
                GeometryType.Point,
                spatialReference,
                fields),
            new LayerDefinition(
                LineLayerId,
                "SRID Test Lines",
                "Line layer for SRID testing",
                GeometryType.LineString,
                spatialReference,
                fields),
            new LayerDefinition(
                PolygonLayerId,
                "SRID Test Polygons",
                "Polygon layer for SRID testing",
                GeometryType.Polygon,
                spatialReference,
                fields)
        ];

        _service = new ServiceDefinition(
            ServiceId,
            "Spatial reference test service",
            _layers,
            spatialReference);
    }

    public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(_layers.FirstOrDefault(layer => layer.Id == layerId));

    public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_layers);

    public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Equals(serviceName, ServiceId, StringComparison.OrdinalIgnoreCase) ? _service : null);

    public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new[] { _service });

    public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(_layers.Any(layer => layer.Id == layerId));

    public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Equals(serviceName, ServiceId, StringComparison.OrdinalIgnoreCase));

    public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
        => Task.FromResult<Relationship?>(null);

    public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<Relationship>());
}
