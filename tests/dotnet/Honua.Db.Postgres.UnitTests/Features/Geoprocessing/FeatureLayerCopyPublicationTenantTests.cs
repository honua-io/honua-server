// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Db.Postgres.Features.Geoprocessing;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.Geoprocessing;

[Trait("Tier", "Fast")]
public sealed class FeatureLayerCopyPublicationTenantTests
{
    private readonly IAdoNetDatabaseConnectionProvider _connections = Substitute.For<IAdoNetDatabaseConnectionProvider>();
    private readonly InvalidOperationException _checkpoint = new("Stop after source binding selection.");
    private FeatureProviderBinding? _selected;

    [Theory]
    [InlineData(false, "tenant-b")]
    [InlineData(true, "tenant-b")]
    [InlineData(false, "tenant-a")]
    [InlineData(true, "tenant-a")]
    public async Task CopyAsync_AlternatePublicationBinding_SelectsRequestedSourceRegardlessOfOrder(bool authorizedFirst, string alternateTenant)
    {
        var copier = CreateCopier(CreateSnapshot(authorizedFirst, alternateTenant));
        using var scope = JobSecurityScope.Begin(new JobSecurityContext("actor", "tenant-a", []));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            copier.CopyAsync(1, "copy", new FeatureQuery(), "job", 1024, CancellationToken.None));

        Assert.Same(_checkpoint, error);
        Assert.Equal("binding-allowed", _selected?.StorageBinding.Metadata.Id);
        Assert.Equal(1, _selected?.StorageLayerId);
    }

    [Fact]
    public async Task CopyAsync_PublicationTenantRevoked_DoesNotResolveOrOpenReader()
    {
        var snapshot = CreateSnapshot(true, "tenant-b");
        var graph = snapshot.Graph with
        {
            Publications = snapshot.Graph.Publications.Select(publication => publication with
            {
                Metadata = publication.Metadata with { Tenant = "tenant-b" }
            }).ToArray()
        };
        var copier = CreateCopier(new MetadataV2GraphSnapshot(graph, "revoked", DateTimeOffset.UtcNow));
        using var scope = JobSecurityScope.Begin(new JobSecurityContext("actor", "tenant-a", []));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            copier.CopyAsync(1, "copy", new FeatureQuery(), "job", 1024, CancellationToken.None));

        Assert.Null(_selected);
        await _connections.DidNotReceive().OpenConnectionAsync(Arg.Any<CancellationToken>());
    }

    private PostgresFeatureLayerCopyService CreateCopier(MetadataV2GraphSnapshot snapshot)
    {
        var reader = Substitute.For<IFeatureReader, IStreamingFeatureStore>();
        var provider = Substitute.For<IFeatureDataProvider, IBindableFeatureDataProvider>();
        provider.ProviderName.Returns(DataProviderNames.Postgis);
        provider.Capabilities.Returns(FeatureProviderCapabilities.ReadOnlyAnalytical);
        ((IBindableFeatureDataProvider)provider).CreateReaderForBinding(Arg.Any<FeatureProviderBinding>())
            .Returns(call => { _selected = call.Arg<FeatureProviderBinding>(); return reader; });
        var router = new FeatureProviderQueryRouter(Substitute.For<ISecureConnectionRegistry>(), new FeatureDataProviderRegistry([provider]));
        var metadata = Substitute.For<IMetadataV2GraphStore>();
        metadata.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<MetadataV2GraphSnapshot>(snapshot));
        _connections.OpenConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<System.Data.Common.DbConnection>(_checkpoint));
        return new PostgresFeatureLayerCopyService(router, _connections, metadata, Substitute.For<ILayerPublishingService>());
    }

    private static MetadataV2GraphSnapshot CreateSnapshot(bool authorizedFirst, string alternateTenant)
    {
        var allowed = CreatePublication("allowed", "tenant-a");
        var hidden = CreatePublication("hidden", alternateTenant);
        return new MetadataV2GraphSnapshot(new MetadataV2Graph
        {
            Services = [new MetadataV2Service { Metadata = new MetadataV2ObjectMetadata { Id = "service", Name = "shared" } }],
            Resources = [CreateResource()],
            StorageBindings = [CreateBinding("allowed", 1), CreateBinding("hidden", 999)],
            Publications = authorizedFirst ? [allowed, hidden] : [hidden, allowed]
        }, "test", DateTimeOffset.UtcNow);
    }

    private static MetadataV2Resource CreateResource() => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "resource", Name = "shared" },
        Type = MetadataV2ResourceType.FeatureDataset,
        PrimaryStorageBindingId = "binding-allowed",
        StorageBindingIds = ["binding-allowed", "binding-hidden"],
        SchemaFields = [new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger, SemanticRoles = ["id.primary"] }],
        Spatial = new MetadataV2ResourceSpatial { SpatialReference = MetadataV2SpatialReference.Wgs84, PrimaryGeometryField = "geometry" }
    };

    private static MetadataV2StorageBinding CreateBinding(string suffix, int storageLayerId) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "binding-" + suffix },
        ResourceId = "resource",
        StorageType = MetadataV2StorageType.RelationalTable,
        StorageLayerId = storageLayerId,
        Locator = "public." + suffix
    };

    private static MetadataV2Publication CreatePublication(string suffix, string tenant) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "publication-" + suffix, Tenant = tenant },
        ResourceId = "resource",
        ServiceId = "service",
        StorageBindingId = "binding-" + suffix,
        LayerIndex = suffix == "allowed" ? 1 : 999,
        PublicationType = MetadataV2PublicationType.EsriFeatureLayer
    };
}
