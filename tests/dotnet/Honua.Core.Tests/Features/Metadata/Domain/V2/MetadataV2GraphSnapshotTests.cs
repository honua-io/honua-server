// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

/// <summary>
/// Tests for the canonical snapshot wrapper, its lookup indexes, and consumer helpers.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class MetadataV2GraphSnapshotTests
{
    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Index_Build_PopulatesAllLookups()
    {
        var graph = SampleGraph();

        var index = MetadataV2GraphIndex.Build(graph);

        index.ResourcesById.Should().ContainKey("resource.parcels");
        index.ResourcesByName.Should().ContainKey("parcels");
        index.ServicesById.Should().ContainKey("service.features");
        index.ServicesByName.Should().ContainKey("Features");
        index.PublicationsById.Should().ContainKey("pub.parcels.features");
        index.PublicationsByService["service.features"].Should().ContainSingle();
        index.PublicationsByResource["resource.parcels"].Should().ContainSingle();
        index.StorageBindingsById.Should().ContainKey("storage.parcels.postgis");
        index.StorageBindingsByResource["resource.parcels"].Should().ContainSingle();
        index.ConnectionsById.Should().ContainKey("conn.postgres");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Snapshot_ExposesGraphMetadataAndEtag()
    {
        var graph = SampleGraph();
        var snapshot = new MetadataV2GraphSnapshot(graph, "\"abc\"", DateTimeOffset.Parse("2026-05-20T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

        snapshot.Etag.Should().Be("\"abc\"");
        snapshot.Revision.Should().Be(graph.Revision);
        snapshot.LoadedAt.Should().Be(DateTimeOffset.Parse("2026-05-20T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Extensions_FindServiceAndPublication_ReturnExpectedEntities()
    {
        var snapshot = new MetadataV2GraphSnapshot(SampleGraph(), "\"abc\"", DateTimeOffset.UtcNow);

        var service = snapshot.FindService("Features");
        service.Should().NotBeNull();
        service!.Metadata.Id.Should().Be("service.features");

        var pub = snapshot.FindPublicationOnService("service.features", "parcels");
        pub.Should().NotBeNull();
        pub!.ResourceId.Should().Be("resource.parcels");

        var byIndex = snapshot.FindPublicationByLayerIndex("service.features", 0);
        byIndex.Should().NotBeNull();
        byIndex!.Metadata.Id.Should().Be("pub.parcels.features");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Extensions_ResolveStorageBinding_PrefersPublicationOverride()
    {
        var graph = SampleGraph() with { };
        var snapshot = new MetadataV2GraphSnapshot(graph, "\"abc\"", DateTimeOffset.UtcNow);
        var pub = snapshot.Index.PublicationsById["pub.parcels.features"];

        var binding = snapshot.ResolveStorageBinding(pub);

        binding.Should().NotBeNull();
        binding!.Metadata.Id.Should().Be("storage.parcels.postgis");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Extensions_ResolveConnection_ReturnsBackingConnection()
    {
        var snapshot = new MetadataV2GraphSnapshot(SampleGraph(), "\"abc\"", DateTimeOffset.UtcNow);
        var binding = snapshot.Index.StorageBindingsById["storage.parcels.postgis"];

        var connection = snapshot.ResolveConnection(binding);

        connection.Should().NotBeNull();
        connection!.Metadata.Id.Should().Be("conn.postgres");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Extensions_FieldsWithSemanticRole_FiltersByRole()
    {
        var snapshot = new MetadataV2GraphSnapshot(SampleGraph(), "\"abc\"", DateTimeOffset.UtcNow);
        var resource = snapshot.Index.ResourcesById["resource.parcels"];

        var geometryFields = resource.FieldsWithSemanticRole("geometry.primary").ToList();
        var idFields = resource.FieldsWithSemanticRole("id.primary").ToList();
        var missing = resource.FieldsWithSemanticRole("nope").ToList();

        geometryFields.Should().ContainSingle(f => f.Name == "shape");
        idFields.Should().ContainSingle(f => f.Name == "parcel_id");
        missing.Should().BeEmpty();
    }

    private static MetadataV2Graph SampleGraph()
    {
        return new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            GeneratedAt = DateTimeOffset.Parse("2026-05-20T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            Connections =
            [
                new MetadataV2Connection
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "conn.postgres", Name = "postgres" },
                    Type = MetadataV2ConnectionType.Managed,
                    Provider = "postgres",
                }
            ],
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource.parcels", Name = "parcels" },
                    Type = MetadataV2ResourceType.FeatureDataset,
                    StorageBindingIds = ["storage.parcels.postgis"],

                    SchemaFields =
                    [
                        new MetadataV2Field { Name = "parcel_id", Type = MetadataV2FieldType.String, SemanticRoles = ["id.primary"] },
                        new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, SemanticRoles = ["geometry.primary"] },
                    ],
                }
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "storage.parcels.postgis", Name = "parcels-postgis" },
                    ResourceId = "resource.parcels",
                    ConnectionId = "conn.postgres",
                    StorageType = MetadataV2StorageType.RelationalTable,
                    Locator = "public.parcels",
                    StorageLayerId = 0,
                }
            ],
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service.features", Name = "Features" },
                    Protocols = [ServiceProtocols.OgcFeatures],
                    Route = "/ogc/features",
                }
            ],
            Publications =
            [
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub.parcels.features", Name = "parcels" },
                    ResourceId = "resource.parcels",
                    ServiceId = "service.features",
                    StorageBindingId = "storage.parcels.postgis",
                    PublicationType = MetadataV2PublicationType.OgcCollection,
                    Identifier = new MetadataV2PublicationIdentifier
                    {
                        Value = "0",
                        IsNumeric = true,
                        PathOverride = "/collections/parcels",
                    },
                }
            ],
        };
    }
}
