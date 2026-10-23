// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.MapServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

/// <summary>Shared lifecycle and data helpers for the partitioned MapServer endpoint suites.</summary>
public abstract class MapServerEndpointTestBase : IAsyncLifetime
{
    protected WebAppFixture Fixture { get; } = new();
    private string? _generateKmlServiceName;

    /// <inheritdoc />
    public async Task InitializeAsync() => await Fixture.InitializeAsync();

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        try
        {
            if (_generateKmlServiceName is { } serviceName && Fixture.CurrentSchema is { } schema)
            {
                await Fixture.Postgres.ApplyGlobalSeedSqlAsync(
                    """
                    DELETE FROM honua.layer_fields WHERE layer_id BETWEEN 110 AND 113;
                    DELETE FROM honua.service_layers WHERE layer_id BETWEEN 110 AND 113;
                    DELETE FROM honua.layers WHERE layer_id BETWEEN 110 AND 113;
                    DELETE FROM honua.services WHERE service_name = @serviceName;
                    """,
                    command => command.Parameters.AddWithValue("serviceName", serviceName),
                    schema);
            }
        }
        finally
        {
            await Fixture.DisposeAsync();
        }
    }

    protected async Task<HttpResponseMessage> PostTextPlainJsonAsync(string operationPath)
    {
        using var content = new StringContent("""{"f":"json"}""", Encoding.UTF8, "text/plain");
        return await Fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer{operationPath}",
            content);
    }

    protected async Task<string> SeedGenerateKmlGeometryServiceAsync()
    {
        var schema = Fixture.CurrentSchema ?? throw new InvalidOperationException("Test schema not initialized.");
        var serviceName = $"kml_{Guid.NewGuid().ToString("N")[..8]}";
        _generateKmlServiceName = serviceName;

        var sql = $$"""
            INSERT INTO honua.services (
                service_name,
                description,
                srid,
                supported_formats,
                capabilities,
                service_extent
            )
            VALUES (
                '{{serviceName}}',
                'MapServer generateKml geometry test service',
                4326,
                ARRAY['JSON', 'GeoJSON'],
                ARRAY['Query', 'Extract'],
                ST_MakeEnvelope(-180, -90, 180, 90, 4326)
            );

            INSERT INTO honua.layers (
                layer_id,
                layer_name,
                description,
                table_schema,
                table_name,
                geometry_type,
                srid,
                extent,
                default_visibility
            )
            VALUES
                (110, 'KML Point Layer', 'Point geometry test layer', current_schema(), 'features', 'Point', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), true),
                (111, 'KML Line Layer', 'Line geometry test layer', current_schema(), 'features', 'LineString', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), true),
                (112, 'KML Polygon Layer', 'Polygon geometry test layer', current_schema(), 'features', 'Polygon', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), true),
                (113, 'KML Projected Point Layer', 'Projected point geometry test layer', current_schema(), 'features', 'Point', 3857, ST_MakeEnvelope(-180, -90, 180, 90, 4326), true);

            INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
            VALUES
                ('{{serviceName}}', 110, 0),
                ('{{serviceName}}', 111, 1),
                ('{{serviceName}}', 112, 2),
                ('{{serviceName}}', 113, 3);

            INSERT INTO honua.layer_fields (
                layer_id,
                field_name,
                field_type,
                field_order,
                max_length,
                nullable,
                description
            )
            VALUES
                (110, 'objectid', 'Integer', 0, null, false, 'Object ID'),
                (110, 'name', 'String', 1, 255, true, 'Name'),
                (110, 'shape', 'Geometry', 2, null, true, 'Geometry'),
                (111, 'objectid', 'Integer', 0, null, false, 'Object ID'),
                (111, 'name', 'String', 1, 255, true, 'Name'),
                (111, 'shape', 'Geometry', 2, null, true, 'Geometry'),
                (112, 'objectid', 'Integer', 0, null, false, 'Object ID'),
                (112, 'name', 'String', 1, 255, true, 'Name'),
                (112, 'shape', 'Geometry', 2, null, true, 'Geometry'),
                (113, 'objectid', 'Integer', 0, null, false, 'Object ID'),
                (113, 'name', 'String', 1, 255, true, 'Name'),
                (113, 'shape', 'Geometry', 2, null, true, 'Geometry');

            INSERT INTO features (objectid, layer_id, geometry, attributes)
            VALUES
                (90110, 110, ST_SetSRID(ST_MakePoint(-157.80, 21.30), 4326), jsonb_build_object('objectid', 90110, 'name', 'KML Point Feature')),
                (90111, 111, ST_GeomFromText('LINESTRING(-157.9 21.2,-157.7 21.4,-157.5 21.3)', 4326), jsonb_build_object('objectid', 90111, 'name', 'KML Line Feature')),
                (90112, 112, ST_GeomFromText('POLYGON((-157.9 21.2,-157.7 21.2,-157.7 21.4,-157.9 21.4,-157.9 21.2))', 4326), jsonb_build_object('objectid', 90112, 'name', 'KML Polygon Feature')),
                (90113, 113, ST_Transform(ST_SetSRID(ST_MakePoint(-157.80, 21.30), 4326), 3857), jsonb_build_object('objectid', 90113, 'name', 'KML Projected Point Feature'));
            """;

        // #2020: route the global honua.services/layers/service_layers/layer_fields seed through
        // the schema-mutation advisory lock (combined statement also seeds the per-schema features).
        await Fixture.Postgres.ApplyGlobalSeedSqlAsync(sql, schema);
        await SeedGenerateKmlMetadataV2GraphAsync(serviceName);
        return serviceName;
    }

    private async Task SeedGenerateKmlMetadataV2GraphAsync(string serviceName)
    {
        var provider = Fixture.GetService<TestMetadataV2GraphProvider>();
        var snapshot = await provider.GetCurrentAsync();
        var serviceId = $"svc-{serviceName}-map";

        var resources = snapshot.Graph.Resources.ToList();
        var bindings = snapshot.Graph.StorageBindings.ToList();
        var publications = snapshot.Graph.Publications.ToList();

        AddGenerateKmlLayer(resources, bindings, publications, serviceId, 110, "KML Point Layer", MetadataV2GeometryType.Point, 4326);
        AddGenerateKmlLayer(resources, bindings, publications, serviceId, 111, "KML Line Layer", MetadataV2GeometryType.LineString, 4326);
        AddGenerateKmlLayer(resources, bindings, publications, serviceId, 112, "KML Polygon Layer", MetadataV2GeometryType.Polygon, 4326);
        AddGenerateKmlLayer(resources, bindings, publications, serviceId, 113, "KML Projected Point Layer", MetadataV2GeometryType.Point, 3857);

        provider.SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Resources = resources,
            StorageBindings = bindings,
            Services = snapshot.Graph.Services
                .Append(new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = serviceId, Name = serviceName },
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
                    Protocols = [ServiceProtocols.MapServer],
                    SpatialReference = MetadataV2SpatialReference.Wgs84,
                    Settings = new MetadataV2ServiceSettings { MaxFeaturesPerLayer = 10_000 },
                })
                .ToArray(),
            Publications = publications,
        });
    }

    private static void AddGenerateKmlLayer(
        List<MetadataV2Resource> resources,
        List<MetadataV2StorageBinding> bindings,
        List<MetadataV2Publication> publications,
        string serviceId,
        int layerId,
        string name,
        MetadataV2GeometryType geometryType,
        int srid)
    {
        var resourceId = $"res-{serviceId}-{layerId}";
        var bindingId = $"bind-{serviceId}-{layerId}";

        resources.Add(new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = resourceId, Name = name },
            Type = MetadataV2ResourceType.FeatureDataset,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            StorageBindingIds = [bindingId],
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true },
                new MetadataV2Field
                {
                    Name = "shape",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = true,
                    SemanticRoles = ["geometry"],
                },
            ],
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = new MetadataV2SpatialReference
                {
                    Srid = srid,
                    Crs = $"EPSG:{srid}",
                    IsGeographic = srid == 4326,
                },
                GeometryType = geometryType,
                PrimaryGeometryField = "shape",
            },
            Display = new MetadataV2ResourceDisplay
            {
                DefaultVisibility = true,
                DisplayField = "name",
            },
        });

        bindings.Add(new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = bindingId, Name = bindingId },
            ResourceId = resourceId,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            Locator = "features",
            StorageLayerId = layerId,
        });

        publications.Add(new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = $"pub-{serviceId}-{layerId}", Name = name },
            ServiceId = serviceId,
            ResourceId = resourceId,
            StorageBindingId = bindingId,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            PublicationType = MetadataV2PublicationType.EsriMapLayer,
            Identifier = new MetadataV2PublicationIdentifier
            {
                Value = layerId.ToString(CultureInfo.InvariantCulture),
                IsNumeric = true,
            },
        });
    }

    protected static string BuildSimpleRendererDynamicLayersJson(int dynamicLayerId)
        => $"[{BuildSimpleRendererDynamicLayerObjectJson(dynamicLayerId)}]";

    protected static string BuildSimpleRendererDynamicLayerObjectJson(int dynamicLayerId)
        => $$"""
             {
               "id": {{dynamicLayerId}},
               "source": {
                 "type": "mapLayer",
                 "mapLayerId": {{WebAppFixture.TestLayerId}}
               },
               "definitionExpression": "1=1",
               "drawingInfo": {
                 "renderer": {
                   "type": "simple",
                   "symbol": {
                     "type": "esriSMS",
                     "style": "esriSMSCircle",
                     "color": [255, 0, 0, 255],
                     "size": 12,
                     "outline": {
                       "type": "esriSLS",
                       "style": "esriSLSSolid",
                       "color": [255, 0, 0, 255],
                       "width": 1
                     }
                   }
                 }
               }
             }
             """;

}
