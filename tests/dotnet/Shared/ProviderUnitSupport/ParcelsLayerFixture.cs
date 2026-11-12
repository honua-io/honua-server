// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;

namespace Honua.Tests.ProviderUnitSupport;

internal sealed record ParcelsLayerSnapshot(
    MetadataV2GraphSnapshot Snapshot,
    MetadataV2Service Service,
    MetadataV2Object Resource,
    MetadataV2Layer Publication);

// One feature layer ("Parcels") published on one service and stored in one relational table,
// shaped per provider through the init properties.
internal sealed record ParcelsLayerFixture(string ProviderAlias, string Locator)
{
    public int LayerId { get; init; } = 1;

    public string ObjectIdField { get; init; } = "OBJECTID";

    public string GeometryField { get; init; } = "SHAPE";

    public MetadataV2FieldType GeometryFieldType { get; init; } = MetadataV2FieldType.Geometry;

    public MetadataV2GeometryType GeometryType { get; init; } = MetadataV2GeometryType.Polygon;

    public IReadOnlyList<MetadataV2Field> AttributeFields { get; init; } =
        [new MetadataV2Field { Name = "NAME", Type = MetadataV2FieldType.String }];

    public string ConnectionName { get; init; } = "secure";

    public IReadOnlyDictionary<string, JsonElement> StorageOptions { get; init; } = new Dictionary<string, JsonElement>();

    public FeatureProviderBinding CreateBinding(IFeatureDataProvider provider, DataConnection? connection)
    {
        var (snapshot, service, resource, publication) = CreateSnapshot(connection?.ConnectionId ?? Guid.NewGuid());
        var storageBinding = snapshot.ResolveStorageBinding(publication)
            ?? throw new InvalidOperationException("Test snapshot did not include a storage binding.");

        return new FeatureProviderBinding(
            service,
            resource,
            publication,
            storageBinding,
            FeatureStorageMapping.FromMetadata(resource, storageBinding),
            LayerId,
            provider,
            connection);
    }

    public ParcelsLayerSnapshot CreateSnapshot(Guid connectionId)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-parcels", Name = "Parcels" },
            SpatialReference = MetadataV2SpatialReference.Wgs84
        };
        var resource = CreateResource();
        var storageBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-parcels", Name = "binding-parcels" },
            ResourceId = resource.Metadata.Id,
            ConnectionId = connectionId.ToString(),
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = Locator,
            StorageLayerId = LayerId,
            Options = StorageOptions
        };
        var metadataConnection = new MetadataV2Connection
        {
            Metadata = new MetadataV2ObjectMetadata { Id = connectionId.ToString(), Name = ConnectionName },
            Provider = ProviderAlias
        };
        var publication = new MetadataV2Layer
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub-parcels", Name = "Parcels" },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            StorageBindingId = storageBinding.Metadata.Id,
            Identifier = new MetadataV2LayerIdentifier { Value = LayerId.ToString(CultureInfo.InvariantCulture), IsNumeric = true }
        };
        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            Resources = [resource],
            Connections = [metadataConnection],
            StorageBindings = [storageBinding],
            Services = [service],
            Publications = [publication]
        };

        return new ParcelsLayerSnapshot(new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UtcNow), service, resource, publication);
    }

    private MetadataV2Object CreateResource() => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "res-parcels", Name = "Parcels" },
        Type = MetadataV2ObjectType.FeatureDataset,
        StorageBindingIds = ["binding-parcels"],
        SchemaFields =
        [
            new MetadataV2Field
            {
                Name = ObjectIdField,
                Type = MetadataV2FieldType.BigInteger,
                Nullable = false,
                SemanticRoles = ["id.primary"]
            },
            new MetadataV2Field
            {
                Name = GeometryField,
                Type = GeometryFieldType,
                Nullable = false,
                SemanticRoles = ["geometry.primary"]
            },
            .. AttributeFields
        ],
        Spatial = new MetadataV2ObjectSpatial
        {
            SpatialReference = MetadataV2SpatialReference.Wgs84,
            GeometryType = GeometryType,
            PrimaryGeometryField = GeometryField
        }
    };
}
