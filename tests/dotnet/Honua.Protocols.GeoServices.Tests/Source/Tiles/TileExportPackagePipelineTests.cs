// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.IO.Compression;
using FluentAssertions;
using Honua.Infrastructure.Tiles;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Tiles;

[Protocol(TestProtocols.MapServer)]
public sealed class TileExportPackagePipelineTests
{
    [UnitTest]
    [Operation(Operations.Export)]
    public async Task WriteAsync_FlatZip_WritesDeterministicOrderedEntriesAndFacts()
    {
        var tiles = new[]
        {
            new TilePackageWriter.PackagedTile(0, 0, 0, [0x01]),
            new TilePackageWriter.PackagedTile(1, 0, 0, [0x02, 0x03]),
            new TilePackageWriter.PackagedTile(1, 1, 0, [0x04]),
        };

        var first = await WriteAsync(CreatePlan(TileExportPackageFormat.Zip), tiles);
        var second = await WriteAsync(CreatePlan(TileExportPackageFormat.Zip), tiles);

        first.Bytes.Should().Equal(second.Bytes);
        first.Facts.Should().Be(new TileExportPackageFacts(TileExportPackageFormat.Zip, 3, first.Bytes.Length));
        using var archive = new ZipArchive(new MemoryStream(first.Bytes), ZipArchiveMode.Read);
        archive.Entries.Select(static entry => entry.FullName).Should().Equal("0/0/0.png", "1/0/0.png", "1/1/0.png");
        archive.Entries.Should().OnlyContain(static entry => entry.LastWriteTime.DateTime == new DateTime(1980, 1, 1));
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task WriteAsync_Tpkx_DelegatesToCompactV2WriterForSparseBundles()
    {
        var tiles = new[]
        {
            new TilePackageWriter.PackagedTile(0, 0, 0, [0x01]),
            new TilePackageWriter.PackagedTile(8, 128, 0, [0x02]),
            new TilePackageWriter.PackagedTile(8, 130, 129, [0x03]),
        };

        var result = await WriteAsync(CreatePlan(TileExportPackageFormat.Tpkx), tiles);

        result.Facts.TileCount.Should().Be(3);
        using var archive = new ZipArchive(new MemoryStream(result.Bytes), ZipArchiveMode.Read);
        archive.GetEntry("tile/L00/R0000C0000.bundle").Should().NotBeNull();
        archive.GetEntry("tile/L08/R0000C0080.bundle").Should().NotBeNull();
        archive.GetEntry("tile/L08/R0080C0080.bundle").Should().NotBeNull();
        archive.GetEntry("root.json").Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task WriteAsync_TpkxWithOneLevel_RejectsBeforeEnumerationOrWriting()
    {
        var enumerated = false;
        var plan = CreatePlan(TileExportPackageFormat.Tpkx) with { ZoomLevels = [0] };
        using var destination = new MemoryStream();

        var act = () => TileExportPackagePipeline.WriteAsync(plan, destination, ObserveEnumeration(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*at least two*");
        enumerated.Should().BeFalse();
        destination.Length.Should().Be(0);

        async IAsyncEnumerable<TilePackageWriter.PackagedTile> ObserveEnumeration()
        {
            enumerated = true;
            yield return new(0, 0, 0, [0x01]);
            await Task.CompletedTask;
        }
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task WriteAsync_OutOfCanonicalOrder_RejectsStream()
    {
        var tiles = new[]
        {
            new TilePackageWriter.PackagedTile(8, 128, 0, [0x01]),
            new TilePackageWriter.PackagedTile(8, 0, 0, [0x02]),
        };
        using var destination = new MemoryStream();

        var act = () => TileExportPackagePipeline.WriteAsync(
            CreatePlan(TileExportPackageFormat.Tpkx), destination, ToAsync(tiles), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ordered by level*");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task WriteAsync_Cancellation_StopsTileEnumeration()
    {
        using var cancellation = new CancellationTokenSource();
        var enumerated = 0;
        using var destination = new CancelAfterFirstWriteStream(cancellation);

        var act = () => TileExportPackagePipeline.WriteAsync(
            CreatePlan(TileExportPackageFormat.Zip), destination, Tiles(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        enumerated.Should().Be(1);

        async IAsyncEnumerable<TilePackageWriter.PackagedTile> Tiles()
        {
            enumerated++;
            yield return new(0, 0, 0, [0x01]);
            await Task.Yield();
            enumerated++;
            yield return new(1, 0, 0, [0x02]);
        }
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task WriteAsync_DestinationLimitFailure_PropagatesWithoutBufferingPackage()
    {
        await using var destination = new FailAfterStream(32);

        var act = () => TileExportPackagePipeline.WriteAsync(
            CreatePlan(TileExportPackageFormat.Zip),
            destination,
            ToAsync([new(0, 0, 0, new byte[256])]),
            CancellationToken.None);

        await act.Should().ThrowAsync<IOException>().WithMessage("*limit*");
    }

    private static async Task<(byte[] Bytes, TileExportPackageFacts Facts)> WriteAsync(
        TileExportJobPlan plan,
        IEnumerable<TilePackageWriter.PackagedTile> tiles)
    {
        using var destination = new MemoryStream();
        var facts = await TileExportPackagePipeline.WriteAsync(plan, destination, ToAsync(tiles), CancellationToken.None);
        return (destination.ToArray(), facts);
    }

    private static TileExportJobPlan CreatePlan(TileExportPackageFormat format) => new()
    {
        SourceKind = TileExportSourceKind.Map,
        ResourceId = "world",
        Source = new TileExportMapSourceDescriptor(
            1,
            [new TileExportMapLayerSelection("0", "default", 1)],
            "watermark",
            null),
        ZoomLevels = ImmutableArray.Create(0, 1, 8),
        West = -180,
        South = -85,
        East = 180,
        North = 85,
        TileImageFormat = "PNG",
        PackageFormat = format,
        MaxTiles = 100,
        MaxArtifactBytes = 1024 * 1024,
        RetentionSeconds = 3600
    };

    private static async IAsyncEnumerable<TilePackageWriter.PackagedTile> ToAsync(
        IEnumerable<TilePackageWriter.PackagedTile> tiles)
    {
        foreach (var tile in tiles)
        {
            yield return tile;
            await Task.Yield();
        }
    }

    private sealed class FailAfterStream(long maximumBytes) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureCapacity(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        private void EnsureCapacity(int count)
        {
            if (Position > maximumBytes - count)
                throw new IOException("test destination limit exceeded");
        }
    }

    private sealed class CancelAfterFirstWriteStream(CancellationTokenSource cancellation) : MemoryStream
    {
        private bool _cancelled;

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            base.Write(buffer);
            Cancel();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var write = base.WriteAsync(buffer, cancellationToken);
            Cancel();
            return write;
        }

        private void Cancel()
        {
            if (_cancelled)
                return;
            _cancelled = true;
            cancellation.Cancel();
        }
    }
}
