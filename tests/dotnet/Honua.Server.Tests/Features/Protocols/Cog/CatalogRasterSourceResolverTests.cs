// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Protocols.Cog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.Cog;

/// <summary>
/// Submit-side tests for <see cref="CatalogRasterSourceResolver"/> proving the decoded-size
/// guard (#3090): a registered COG whose header declares an enormous decoded grid is rejected
/// before its bytes are materialized, while a normal COG resolves to its bytes.
/// </summary>
public class CatalogRasterSourceResolverTests
{
    private const long CompressedCap = 50L * 1024 * 1024; // matches the artifact ceiling
    private const long DecodedCap = 64L * 1024 * 1024;

    [Fact]
    public async Task ResolveAsync_DeclaredHugeDecodedGrid_FailsClosedBeforeMaterializing()
    {
        // 50000 x 50000 x 1 x uint8 -> ~2.3 GiB decoded, but a tiny compressed artifact.
        var tiff = BuildClassicTiff(width: 50_000, height: 50_000, samplesPerPixel: 1, bitsPerSample: 8);
        var reader = new RecordingRangeReader(tiff);
        var resolver = CreateResolver(reader, out _);

        var resolution = await resolver.ResolveAsync(new RasterSourceReference(LayerId: 7, RasterId: 42), CompressedCap);

        resolution.Found.Should().BeFalse();
        resolution.Bytes.Should().BeNull();
        resolution.FailureReason.Should().Contain("decoded size");

        // The whole-object materializing read must never have happened: only bounded header/IFD
        // probe reads were issued, never a read of the full artifact length.
        reader.FullObjectReadCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_NormalRaster_ResolvesBytes()
    {
        var tiff = BuildClassicTiff(width: 512, height: 512, samplesPerPixel: 1, bitsPerSample: 8);
        var reader = new RecordingRangeReader(tiff);
        var resolver = CreateResolver(reader, out _);

        var resolution = await resolver.ResolveAsync(new RasterSourceReference(LayerId: 7, RasterId: 42), CompressedCap);

        resolution.Found.Should().BeTrue();
        resolution.Bytes.Should().NotBeNull();
        resolution.Bytes!.Length.Should().Be(tiff.Length);
        reader.FullObjectReadCount.Should().Be(1);
    }

    private static CatalogRasterSourceResolver CreateResolver(RecordingRangeReader reader, out IServiceProvider provider)
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

        var services = new ServiceCollection();
        services.AddScoped<ICogStore>(_ => new FakeCogStore(registration));
        services.AddSingleton<ICloudRangeReader>(reader);
        provider = services.BuildServiceProvider();

        var options = Options.Create(new CatalogRasterSourceOptions { MaxDecodedRasterBytes = DecodedCap });
        return new CatalogRasterSourceResolver(
            provider.GetRequiredService<IServiceScopeFactory>(),
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
