// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Protocols.Cog;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        var resolver = new CatalogRasterSourceResolver(
            services.GetRequiredService<IServiceScopeFactory>(),
            new CogDecodedSizeInspector(),
            Options.Create(new CatalogRasterSourceOptions()));

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
        var resolver = new CatalogRasterSourceResolver(
            services.GetRequiredService<IServiceScopeFactory>(),
            new CogDecodedSizeInspector(),
            Options.Create(new CatalogRasterSourceOptions()));

        var mismatched = await resolver.ResolveLayerIdAsync(new RasterSourceReference(7, 91));
        var unknown = await resolver.ResolveLayerIdAsync(new RasterSourceReference(null, 404));

        mismatched.Should().Be(RasterSourceLayerResolution.NotFound());
        unknown.Should().Be(mismatched);
    }

    [UnitTest]
    public async Task ResolveLayerIdAsync_StorageLayerSelector_QueriesItsPublicationIndex()
    {
        var store = Substitute.For<ICogStore>();
        store.ListByLayerAsync(42, Arg.Any<CancellationToken>())
            .Returns([CreateRegistration(91, layerId: 42)]);
        await using var services = BuildServices(store, BuildSnapshot((42, 900)));
        var resolver = new CatalogRasterSourceResolver(
            services.GetRequiredService<IServiceScopeFactory>(),
            new CogDecodedSizeInspector(),
            Options.Create(new CatalogRasterSourceOptions()));

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
        var resolver = new CatalogRasterSourceResolver(
            services.GetRequiredService<IServiceScopeFactory>(),
            new CogDecodedSizeInspector(),
            Options.Create(new CatalogRasterSourceOptions()));

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
        var resolver = new CatalogRasterSourceResolver(
            services.GetRequiredService<IServiceScopeFactory>(),
            new CogDecodedSizeInspector(),
            Options.Create(new CatalogRasterSourceOptions()));

        var result = await resolver.ResolveLayerIdAsync(new RasterSourceReference(null, 91));

        result.Should().Be(RasterSourceLayerResolution.NotFound());
    }

    private static ServiceProvider BuildServices(
        ICogStore store,
        MetadataV2GraphSnapshot snapshot) =>
        new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton<IMetadataV2GraphProvider>(new StubGraphProvider(snapshot))
            .BuildServiceProvider();

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

    // --- #3090 decoded-size guard (integration through ResolveAsync) ---
    private const long GuardCompressedCap = 50L * 1024 * 1024; // matches the artifact ceiling
    private const long GuardDecodedCap = 64L * 1024 * 1024;

    [UnitTest]
    public async Task ResolveAsync_DeclaredHugeDecodedGrid_FailsClosedBeforeMaterializing()
    {
        // 50000 x 50000 x 1 x uint8 -> ~2.3 GiB decoded, but a tiny compressed artifact.
        var tiff = BuildClassicTiff(width: 50_000, height: 50_000, samplesPerPixel: 1, bitsPerSample: 8);
        var reader = new RecordingRangeReader(tiff);
        var resolver = CreateGuardResolver(reader);

        var resolution = await resolver.ResolveAsync(new RasterSourceReference(LayerId: 7, RasterId: 42), GuardCompressedCap);

        resolution.Found.Should().BeFalse();
        resolution.Bytes.Should().BeNull();
        resolution.FailureReason.Should().Contain("decoded size");

        // The whole-object materializing read must never have happened: only bounded header/IFD
        // probe reads were issued, never a read of the full artifact length.
        reader.FullObjectReadCount.Should().Be(0);
    }

    [UnitTest]
    public async Task ResolveAsync_NormalRaster_ResolvesBytes()
    {
        var tiff = BuildClassicTiff(width: 512, height: 512, samplesPerPixel: 1, bitsPerSample: 8);
        var reader = new RecordingRangeReader(tiff);
        var resolver = CreateGuardResolver(reader);

        var resolution = await resolver.ResolveAsync(new RasterSourceReference(LayerId: 7, RasterId: 42), GuardCompressedCap);

        resolution.Found.Should().BeTrue();
        resolution.Bytes.Should().NotBeNull();
        resolution.Bytes!.Length.Should().Be(tiff.Length);
        reader.FullObjectReadCount.Should().Be(1);
    }

    // The COG registration's LayerId (7) is a service-local publication index; the resolver
    // maps it to a storage layer via the metadata catalog, and the LayerId hint on the
    // reference must equal that storage layer. Publish (7 -> 7) so resolution reaches the
    // decoded-size guard on the whole-COG path (#3090).
    private static CatalogRasterSourceResolver CreateGuardResolver(RecordingRangeReader reader)
    {
        var registration = new CogRegistration
        {
            Id = 42,
            LayerId = 7,
            Name = "test",
            Provider = CloudStorageProvider.AwsS3,
            Bucket = "bucket",
            ObjectKey = "raster.tif",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var services = new ServiceCollection()
            .AddScoped<ICogStore>(_ => new FakeCogStore(registration))
            .AddSingleton<ICloudRangeReader>(reader)
            .AddSingleton<IMetadataV2GraphProvider>(new StubGraphProvider(BuildSnapshot((7, 7))))
            .BuildServiceProvider();

        var options = Options.Create(new CatalogRasterSourceOptions { MaxDecodedRasterBytes = GuardDecodedCap });
        return new CatalogRasterSourceResolver(
            services.GetRequiredService<IServiceScopeFactory>(),
            new CogDecodedSizeInspector(),
            options);
    }

    private static byte[] BuildClassicTiff(int width, int height, int samplesPerPixel, int bitsPerSample)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((ushort)0x4949);
        writer.Write((ushort)42);
        writer.Write((uint)8);

        const int entryCount = 9;
        writer.Write((ushort)entryCount);

        WriteEntry(writer, 256, 4, 1, (uint)width);
        WriteEntry(writer, 257, 4, 1, (uint)height);
        WriteEntry(writer, 258, 3, 1, (uint)(ushort)bitsPerSample);
        WriteEntry(writer, 259, 3, 1, 1);
        WriteEntry(writer, 277, 3, 1, (uint)samplesPerPixel);
        WriteEntry(writer, 322, 4, 1, 256);
        WriteEntry(writer, 323, 4, 1, 256);
        WriteEntry(writer, 324, 4, 1, 5000);
        WriteEntry(writer, 325, 4, 1, 1000);

        writer.Write((uint)0);
        return ms.ToArray();
    }

    private static void WriteEntry(BinaryWriter writer, ushort tag, ushort type, uint count, uint valueOrOffset)
    {
        writer.Write(tag);
        writer.Write(type);
        writer.Write(count);
        writer.Write(valueOrOffset);
    }

    private sealed class FakeCogStore(CogRegistration registration) : ICogStore
    {
        public Task<CogRegistration?> GetAsync(long id, CancellationToken cancellationToken = default)
            => Task.FromResult(id == registration.Id ? registration : null);

        public Task<CogRegistration[]> ListByLayerAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(layerId == registration.LayerId ? new[] { registration } : []);

        public Task<CogRegistration> RegisterAsync(CogRegistrationRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> UnregisterAsync(long id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task UpdateMetadataAsync(long id, CogMetadata metadata, byte[]? ifdCache, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingRangeReader(byte[] data) : ICloudRangeReader
    {
        // The resolver materializes the whole object with a single read of exactly the object
        // length at offset 0; the bounded probe never issues a read of that exact length.
        public int FullObjectReadCount { get; private set; }

        public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

        public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
        {
            if (offset == 0 && length == data.Length)
            {
                FullObjectReadCount++;
            }

            var available = Math.Max(0, data.Length - (int)offset);
            var bytesToRead = Math.Min(length, available);
            var result = new byte[bytesToRead];
            if (bytesToRead > 0)
            {
                Buffer.BlockCopy(data, (int)offset, result, 0, bytesToRead);
            }

            return Task.FromResult(result);
        }

        public Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => Task.FromResult((long)data.Length);
    }
}
