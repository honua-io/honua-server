// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Protocols.Cog;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Cog;

public sealed class CatalogRasterSourceResolverTests
{
    [UnitTest]
    public async Task ResolveLayerIdAsync_RasterIdOnly_ReturnsOwningLayerWithoutCloudReader()
    {
        var store = Substitute.For<ICogStore>();
        store.GetAsync(91, Arg.Any<CancellationToken>()).Returns(CreateRegistration(91, layerId: 42));
        await using var services = BuildServices(store, BuildSnapshot((42, 900)));
        var resolver = new CatalogRasterSourceResolver(services.GetRequiredService<IServiceScopeFactory>());

        var result = await resolver.ResolveLayerIdAsync(new RasterSourceReference(null, 91));

        result.Should().Be(RasterSourceLayerResolution.Success(900));
    }

    [UnitTest]
    public async Task ResolveLayerIdAsync_MismatchedHintAndUnknownRaster_AreIndistinguishable()
    {
        var store = Substitute.For<ICogStore>();
        store.GetAsync(91, Arg.Any<CancellationToken>()).Returns(CreateRegistration(91, layerId: 42));
        store.GetAsync(404, Arg.Any<CancellationToken>()).Returns((CogRegistration?)null);
        await using var services = BuildServices(store, BuildSnapshot((42, 900)));
        var resolver = new CatalogRasterSourceResolver(services.GetRequiredService<IServiceScopeFactory>());

        var mismatched = await resolver.ResolveLayerIdAsync(new RasterSourceReference(7, 91));
        var unknown = await resolver.ResolveLayerIdAsync(new RasterSourceReference(null, 404));

        mismatched.Should().Be(RasterSourceLayerResolution.NotFound());
        unknown.Should().Be(mismatched);
    }

    [UnitTest]
    public async Task ResolveAsync_ProjectsImmutableReferenceWithoutReadingAnyObjectRange()
    {
        var store = Substitute.For<ICogStore>();
        store.GetAsync(91, Arg.Any<CancellationToken>()).Returns(CreateRegistration(91, layerId: 42));
        var reader = Substitute.For<ICloudRangeReader>();
        reader.Provider.Returns(CloudStorageProvider.AwsS3);
        reader.GetObjectMetadataAsync("test-bucket", "test.tif", Arg.Any<CancellationToken>())
            .Returns(new CloudObjectMetadata
            {
                SizeBytes = 9_876_543_210,
                Version = "s3-version-9",
                ETag = "etag-9",
            });
        await using var services = BuildServices(store, BuildSnapshot((42, 900)), reader);
        var resolver = new CatalogRasterSourceResolver(services.GetRequiredService<IServiceScopeFactory>());

        var result = await resolver.ResolveAsync(new RasterSourceReference(900, 91));

        result.Found.Should().BeTrue();
        var descriptor = result.Descriptor.Should().BeOfType<ObjectStoreCogRasterSourceDescriptor>().Subject;
        descriptor.Provider.Should().Be(CloudStorageProvider.AwsS3);
        descriptor.StoreReference.Should().Be("test-bucket");
        descriptor.ObjectKey.Should().Be("test.tif");
        descriptor.Version.Should().Be("s3-version-9");
        descriptor.Content.SizeBytes.Should().Be(9_876_543_210);
        await reader.DidNotReceiveWithAnyArgs().ReadRangeAsync(default!, default!, default, default, default);
        await reader.DidNotReceiveWithAnyArgs().ReadRangeStreamAsync(default!, default!, default, default, default);
    }

    [UnitTest]
    public async Task ResolveLayerIdAsync_StorageLayerSelector_QueriesItsPublicationIndex()
    {
        var store = Substitute.For<ICogStore>();
        store.ListByLayerAsync(42, Arg.Any<CancellationToken>())
            .Returns([CreateRegistration(91, layerId: 42)]);
        await using var services = BuildServices(store, BuildSnapshot((42, 900)));
        var resolver = new CatalogRasterSourceResolver(services.GetRequiredService<IServiceScopeFactory>());

        var result = await resolver.ResolveLayerIdAsync(new RasterSourceReference(900, null));

        result.Should().Be(RasterSourceLayerResolution.Success(900));
        await store.Received(1).ListByLayerAsync(42, Arg.Any<CancellationToken>());
        await store.DidNotReceive().ListByLayerAsync(900, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ResolveLayerIdAsync_AmbiguousPublicationIndex_FailsClosed()
    {
        var store = Substitute.For<ICogStore>();
        store.GetAsync(91, Arg.Any<CancellationToken>()).Returns(CreateRegistration(91, layerId: 42));
        await using var services = BuildServices(store, BuildSnapshot((42, 900), (42, 901)));
        var resolver = new CatalogRasterSourceResolver(services.GetRequiredService<IServiceScopeFactory>());

        var result = await resolver.ResolveLayerIdAsync(new RasterSourceReference(null, 91));

        result.Should().Be(RasterSourceLayerResolution.NotFound());
    }

    [UnitTest]
    public async Task ResolveLayerIdAsync_PublicationCollisionWithMissingBinding_FailsClosed()
    {
        var store = Substitute.For<ICogStore>();
        store.GetAsync(91, Arg.Any<CancellationToken>()).Returns(CreateRegistration(91, layerId: 42));
        await using var services = BuildServices(
            store,
            BuildSnapshot((42, 900), (42, (int?)null)));
        var resolver = new CatalogRasterSourceResolver(services.GetRequiredService<IServiceScopeFactory>());

        var result = await resolver.ResolveLayerIdAsync(new RasterSourceReference(null, 91));

        result.Should().Be(RasterSourceLayerResolution.NotFound());
    }

    private static ServiceProvider BuildServices(
        ICogStore store,
        MetadataV2GraphSnapshot snapshot,
        params ICloudRangeReader[] readers)
    {
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton<IMetadataV2GraphProvider>(new StubGraphProvider(snapshot));
        foreach (var reader in readers)
        {
            services.AddSingleton(reader);
        }

        return services.BuildServiceProvider();
    }

    private static MetadataV2GraphSnapshot BuildSnapshot(
        params (int PublicationLayerId, int? StorageLayerId)[] layers)
    {
        var services = layers.Select((_, index) => new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = $"service-{index}", Name = $"service-{index}" },
        }).ToArray();
        var resources = layers.Select((_, index) => new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = $"resource-{index}", Name = $"resource-{index}" },
            StorageBindingIds = [$"binding-{index}"],
            PrimaryStorageBindingId = $"binding-{index}",
        }).ToArray();
        var bindings = layers.Select((layer, index) => new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = $"binding-{index}", Name = $"binding-{index}" },
            ResourceId = resources[index].Metadata.Id,
            StorageLayerId = layer.StorageLayerId,
        }).ToArray();
        var publications = layers.Select((layer, index) => new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = $"publication-{index}", Name = $"publication-{index}" },
            ServiceId = services[index].Metadata.Id,
            ResourceId = resources[index].Metadata.Id,
            StorageBindingId = bindings[index].Metadata.Id,
            LayerIndex = layer.PublicationLayerId,
        }).ToArray();

        return new MetadataV2GraphSnapshot(
            new MetadataV2Graph
            {
                Revision = 1,
                Services = services,
                Resources = resources,
                StorageBindings = bindings,
                Publications = publications,
            },
            "\"cog-resolver-tests\"",
            DateTimeOffset.UnixEpoch);
    }

    private static CogRegistration CreateRegistration(long rasterId, int layerId) => new()
    {
        Id = rasterId,
        LayerId = layerId,
        Name = "test-raster",
        Provider = CloudStorageProvider.AwsS3,
        Bucket = "test-bucket",
        ObjectKey = "test.tif",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class StubGraphProvider(MetadataV2GraphSnapshot snapshot) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
            long revision,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(
                revision == snapshot.Revision ? snapshot : null);
    }
}
