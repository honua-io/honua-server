// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.TileCachePackage.Abstractions;
using Honua.Core.Features.TileCachePackage.Domain;
using Honua.Core.Features.TileCachePackage.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.TileCachePackage;

/// <summary>
/// Package re-import replaces tile bytes and drops coordinates the new package
/// no longer contains (#6175).
/// </summary>
public sealed class TileCachePackageImportServiceTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ImportAsync_ReimportSamePackageName_ReplacesBytesAndDropsStaleTiles()
    {
        var sink = new MemoryTileSink();
        var reader = new ScriptedPackageReader();
        var service = new TileCachePackageImportService(reader, sink, NullLogger<TileCachePackageImportService>.Instance);

        reader.Tiles.Add(Tile(x: 0, y: 0, [1, 1, 1]));
        reader.Tiles.Add(Tile(x: 1, y: 0, [2, 2, 2]));
        var first = await service.ImportAsync(Request());

        first.Success.Should().BeTrue();
        first.TilesImported.Should().Be(2);
        first.TilesSkipped.Should().Be(0);

        reader.Tiles.Clear();
        reader.Tiles.Add(Tile(x: 0, y: 0, [9, 9, 9]));
        var second = await service.ImportAsync(Request());

        second.Success.Should().BeTrue();
        second.TilesImported.Should().Be(1);
        second.TilesSkipped.Should().Be(0);
        sink.Tiles.Should().ContainKey((0, 0, 0));
        sink.Tiles[(0, 0, 0)].Should().Equal([9, 9, 9]);
        sink.Tiles.Should().NotContainKey((Z: 0, X: 1, Y: 0));
    }

    private static TileCachePackageImportRequest Request()
        => new()
        {
            Package = new MemoryStream([0x50, 0x4B]),
            FileName = "basemap.tpkx",
            TilesetId = "basemap"
        };

    private static TileCachePackageTile Tile(int x, int y, byte[] content)
        => new() { Z = 0, X = x, Y = y, Content = content };

    private sealed class ScriptedPackageReader : ITileCachePackageReader
    {
        public List<TileCachePackageTile> Tiles { get; } = [];

        public bool CanRead(string fileName) => true;

        public Task<TileCachePackageDescriptor> ReadDescriptorAsync(
            Stream package,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TileCachePackageDescriptor
            {
                StorageFormat = TileCacheStorageFormat.CompactV2,
                DataType = TileCacheDataType.Raster,
                ContentType = "image/png",
                TileMatrixSetIdentifier = "WebMercatorQuad",
                Wkid = 3857,
                TileSize = 256,
                MinLevel = 0,
                MaxLevel = 0,
                TileBundlesPath = "tile"
            });

        public async IAsyncEnumerable<TileCachePackageTile> ReadTilesAsync(
            Stream package,
            TileCachePackageDescriptor descriptor,
            int minZoom,
            int maxZoom,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (var tile in Tiles)
            {
                yield return tile;
            }
        }
    }

    private sealed class MemoryTileSink : IOgcTileCacheSink
    {
        public Dictionary<(int Z, int X, int Y), byte[]> Tiles { get; } = [];

        public Task<string> EnsureTileCacheAsync(
            OgcTileCacheDescriptor descriptor,
            CancellationToken cancellationToken = default)
            => Task.FromResult("tilecache:basemap");

        public Task<OgcTileCacheWriteStatus> WriteTileAsync(
            OgcTileCacheRecord record,
            CancellationToken cancellationToken = default)
        {
            var key = (record.Z, record.X, record.Y);
            if (Tiles.ContainsKey(key))
            {
                return Task.FromResult(OgcTileCacheWriteStatus.AlreadyPresent);
            }

            Tiles[key] = record.Content;
            return Task.FromResult(OgcTileCacheWriteStatus.Inserted);
        }

        public Task<OgcTileCacheWriteStatus> ReplaceTileAsync(
            OgcTileCacheRecord record,
            CancellationToken cancellationToken = default)
        {
            var key = (record.Z, record.X, record.Y);
            var replaced = Tiles.ContainsKey(key);
            Tiles[key] = record.Content.ToArray();
            return Task.FromResult(replaced
                ? OgcTileCacheWriteStatus.Replaced
                : OgcTileCacheWriteStatus.Inserted);
        }

        public Task DeleteTilesExceptAsync(
            string tileCacheId,
            IReadOnlyList<OgcTileCoordinate> retained,
            CancellationToken cancellationToken = default)
        {
            var keep = retained.Select(static coordinate => (coordinate.Z, coordinate.X, coordinate.Y)).ToHashSet();
            foreach (var key in Tiles.Keys.Where(key => !keep.Contains(key)).ToArray())
            {
                Tiles.Remove(key);
            }

            return Task.CompletedTask;
        }
    }
}
