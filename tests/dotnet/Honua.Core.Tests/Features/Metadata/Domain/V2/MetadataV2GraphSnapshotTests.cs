// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

/// <summary>
/// Tests for the canonical snapshot wrapper, its lookup indexes, and consumer helpers.
/// </summary>
[Protocol(ProtocolNames.TestQuality)]
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
    public void Extensions_FindPublication_SkipsUnroutableDuplicate()
    {
        var graph = SampleGraph();
        var active = graph.Publications.Single();
        var draft = active with
        {
            Metadata = active.Metadata with { Id = "pub.parcels.draft" },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Draft }
        };
        var snapshot = new MetadataV2GraphSnapshot(
            graph with { Publications = [draft, active] },
            "\"duplicates\"",
            DateTimeOffset.UtcNow);

        snapshot.FindPublicationOnService("service.features", "parcels")
            .Should().BeSameAs(active);
        snapshot.FindPublicationByLayerIndex("service.features", 0)
            .Should().BeSameAs(active);
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

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void IsRoutable_RequiresServingPublicationAndResourceLifecycle()
    {
        var active = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active };
        var deprecated = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Deprecated };
        var draft = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Draft };
        var retired = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Retired };
        var archived = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Archived };
        var publication = new MetadataV2Publication { Status = active };
        var resource = new MetadataV2Resource { Status = active };

        publication.IsRoutable(resource).Should().BeTrue();
        (publication with { Status = deprecated }).IsRoutable(resource).Should().BeTrue();
        publication.IsRoutable(resource with { Status = deprecated }).Should().BeTrue();
        (publication with { Status = draft }).IsRoutable(resource).Should().BeFalse();
        publication.IsRoutable(resource with { Status = draft }).Should().BeFalse();
        (publication with { Status = retired }).IsRoutable(resource).Should().BeFalse();
        publication.IsRoutable(resource with { Status = retired }).Should().BeFalse();
        (publication with { Status = archived }).IsRoutable(resource).Should().BeFalse();
        publication.IsRoutable(resource with { Status = archived }).Should().BeFalse();
        publication.IsRoutable(resource: null).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void SnapshotIsRoutable_RequiresServingResolvedBindingLifecycle()
    {
        var graph = SampleGraph();
        var publication = graph.Publications.Single();
        var binding = graph.StorageBindings.Single();

        var nonServingSnapshots = new[]
            {
                MetadataV2LifecycleStatus.Draft,
                MetadataV2LifecycleStatus.Retired,
                MetadataV2LifecycleStatus.Archived,
            }
            .Select(lifecycle => new MetadataV2GraphSnapshot(
                graph with
                {
                    StorageBindings =
                    [
                        binding with { Status = new MetadataV2Status { Lifecycle = lifecycle } }
                    ]
                },
                $"\"{lifecycle}\"",
                DateTimeOffset.UtcNow));

        nonServingSnapshots.Should().OnlyContain(snapshot => !snapshot.IsRoutable(publication));

        var deprecatedSnapshot = new MetadataV2GraphSnapshot(
            graph with
            {
                StorageBindings =
                [
                    binding with
                    {
                        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Deprecated }
                    }
                ]
            },
            "\"deprecated\"",
            DateTimeOffset.UtcNow);
        deprecatedSnapshot.IsRoutable(publication).Should().BeTrue();

        var resource = graph.Resources.Single();
        var bindinglessPublication = publication with { StorageBindingId = null };
        var bindinglessSnapshot = new MetadataV2GraphSnapshot(
            graph with
            {
                Resources =
                [
                    resource with
                    {
                        Type = MetadataV2ResourceType.Document,
                        StorageBindingIds = [],
                        PrimaryStorageBindingId = null,
                    }
                ],
                StorageBindings = [],
                Publications = [bindinglessPublication],
            },
            "\"bindingless\"",
            DateTimeOffset.UtcNow);
        bindinglessSnapshot.IsRoutable(bindinglessPublication).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void SnapshotIsRoutable_RequiresServingResolvedServiceLifecycle()
    {
        var graph = SampleGraph();
        var publication = graph.Publications.Single();
        var service = graph.Services.Single();

        var nonServingSnapshots = new[]
            {
                MetadataV2LifecycleStatus.Draft,
                MetadataV2LifecycleStatus.Retired,
                MetadataV2LifecycleStatus.Archived,
            }
            .Select(lifecycle => new MetadataV2GraphSnapshot(
                graph with
                {
                    Services =
                    [
                        service with { Status = new MetadataV2Status { Lifecycle = lifecycle } }
                    ]
                },
                $"\"{lifecycle}\"",
                DateTimeOffset.UtcNow));

        nonServingSnapshots.Should().OnlyContain(snapshot => !snapshot.IsRoutable(publication));

        var deprecatedSnapshot = new MetadataV2GraphSnapshot(
            graph with
            {
                Services =
                [
                    service with
                    {
                        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Deprecated }
                    }
                ]
            },
            "\"deprecated-service\"",
            DateTimeOffset.UtcNow);
        deprecatedSnapshot.IsRoutable(publication).Should().BeTrue();

        var missingServiceSnapshot = new MetadataV2GraphSnapshot(
            graph with { Services = [] },
            "\"missing-service\"",
            DateTimeOffset.UtcNow);
        missingServiceSnapshot.IsRoutable(publication).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void IsRoutable_RequiresServingBindingAndResourceLifecycle()
    {
        var active = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active };
        var deprecated = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Deprecated };
        var draft = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Draft };
        var retired = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Retired };
        var archived = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Archived };
        var binding = new MetadataV2StorageBinding { Status = active };
        var resource = new MetadataV2Resource { Status = active };

        binding.IsRoutable(resource).Should().BeTrue();
        (binding with { Status = deprecated }).IsRoutable(resource).Should().BeTrue();
        binding.IsRoutable(resource with { Status = deprecated }).Should().BeTrue();
        (binding with { Status = draft }).IsRoutable(resource).Should().BeFalse();
        binding.IsRoutable(resource with { Status = draft }).Should().BeFalse();
        (binding with { Status = retired }).IsRoutable(resource).Should().BeFalse();
        binding.IsRoutable(resource with { Status = retired }).Should().BeFalse();
        (binding with { Status = archived }).IsRoutable(resource).Should().BeFalse();
        binding.IsRoutable(resource with { Status = archived }).Should().BeFalse();
        binding.IsRoutable(resource: null).Should().BeFalse();
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
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
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
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
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
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
                }
            ],
            Publications =
            [
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub.parcels.features", Name = "parcels" },
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
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

    // ---- Aliased-publication storage handle resolution (SEC-4) -----------

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ResolveStorageLayerId_AliasedPublication_PrefersTheBoundStorageHandleOverTheLayerIndex()
    {
        var snapshot = AliasedSnapshot();
        var publication = snapshot.Index.PublicationsById["pub.parcels.aliased"];
        var resource = snapshot.Index.ResourcesById["resource.parcels"];

        publication.LayerIndex.Should().Be(
            AliasedCollidingStorageLayerId,
            "the fixture's whole point is a service-local index that names another resource's storage handle");

        snapshot.ResolveStorageLayerId(publication, resource)
            .Should().Be(AliasedParcelsStorageLayerId);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ResolveStorageLayerId_AliasedPublication_DoesNotResolveToTheCollidingResource()
    {
        var snapshot = AliasedSnapshot();
        var parcels = snapshot.Index.PublicationsById["pub.parcels.aliased"];
        var permits = snapshot.Index.PublicationsById["pub.permits"];

        var parcelsHandle = snapshot.ResolveStorageLayerId(parcels, snapshot.ResolveResource(parcels));
        var permitsHandle = snapshot.ResolveStorageLayerId(permits, snapshot.ResolveResource(permits));

        parcelsHandle.Should().NotBe(permitsHandle);
        permitsHandle.Should().Be(AliasedCollidingStorageLayerId);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ResolveStorageLayerId_ResolvesTheResourceWhenTheCallerDoesNotSupplyIt()
    {
        var snapshot = AliasedSnapshot();
        var publication = snapshot.Index.PublicationsById["pub.parcels.aliased"];

        snapshot.ResolveStorageLayerId(publication, resource: null)
            .Should().Be(AliasedParcelsStorageLayerId);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ResolveStorageLayerId_BindinglessPublication_FallsBackToTheLayerIndex()
    {
        // The last resort stays: graphs that carry no storage handle at all are still
        // addressable by their service-local index.
        var snapshot = AliasedSnapshot();
        var publication = snapshot.Index.PublicationsById["pub.documents"];

        snapshot.ResolveStorageLayerId(publication, snapshot.ResolveResource(publication))
            .Should().Be(77);
    }

    private const int AliasedParcelsStorageLayerId = 7;
    private const int AliasedCollidingStorageLayerId = 3;

    /// <summary>
    /// Graph in which <c>resource.parcels</c> is published with the service-local index
    /// <see cref="AliasedCollidingStorageLayerId"/> while its storage binding carries
    /// <see cref="AliasedParcelsStorageLayerId"/>, and a second resource,
    /// <c>resource.permits</c>, owns <see cref="AliasedCollidingStorageLayerId"/> as its
    /// storage handle. Reading the index as a storage handle therefore lands on the other
    /// resource. Manifest-authored, release-authored and imported graphs produce this shape
    /// (#4065); the admin publish path does not.
    /// </summary>
    internal static MetadataV2GraphSnapshot AliasedSnapshot()
        => new(AliasedGraph(), "\"aliased\"", DateTimeOffset.UtcNow);

    private static MetadataV2Graph AliasedGraph()
    {
        return new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            GeneratedAt = DateTimeOffset.Parse("2026-09-21T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
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
                FeatureResource("resource.parcels", "parcels", "storage.parcels"),
                FeatureResource("resource.permits", "permits", "storage.permits"),
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "resource.documents", Name = "documents" },
                    Type = MetadataV2ResourceType.Document,
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
                    StorageBindingIds = [],
                    SchemaFields = [],
                },
            ],
            StorageBindings =
            [
                Binding("storage.parcels", "resource.parcels", "public.parcels", AliasedParcelsStorageLayerId),
                Binding("storage.permits", "resource.permits", "public.permits", AliasedCollidingStorageLayerId),
            ],
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service.features", Name = "Features" },
                    Protocols = [ServiceProtocols.OgcFeatures],
                    Route = "/ogc/features",
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
                }
            ],
            Publications =
            [
                Publication("pub.parcels.aliased", "resource.parcels", "storage.parcels", AliasedCollidingStorageLayerId),
                Publication("pub.permits", "resource.permits", "storage.permits", 9),
                Publication("pub.documents", "resource.documents", storageBindingId: null, layerIndex: 77),
            ],
        };
    }

    private static MetadataV2Resource FeatureResource(string id, string name, string storageBindingId)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = name },
            Type = MetadataV2ResourceType.FeatureDataset,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            StorageBindingIds = [storageBindingId],
            PrimaryStorageBindingId = storageBindingId,
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, SemanticRoles = ["id.primary"] },
                new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, SemanticRoles = ["geometry.primary"] },
            ],
        };

    private static MetadataV2StorageBinding Binding(string id, string resourceId, string locator, int storageLayerId)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            ResourceId = resourceId,
            ConnectionId = "conn.postgres",
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = locator,
            StorageLayerId = storageLayerId,
        };

    private static MetadataV2Publication Publication(
        string id,
        string resourceId,
        string? storageBindingId,
        int layerIndex)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            ResourceId = resourceId,
            ServiceId = "service.features",
            StorageBindingId = storageBindingId,
            PublicationType = MetadataV2PublicationType.OgcCollection,
            IsPrimary = true,
            Identifier = new MetadataV2PublicationIdentifier
            {
                Value = layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                IsNumeric = true,
            },
        };
}
