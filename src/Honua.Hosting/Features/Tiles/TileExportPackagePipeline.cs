// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.IO.Compression;

namespace Honua.Infrastructure.Tiles;

/// <summary>
/// Writes the durable tile-export package formats from one canonical ordered tile stream.
/// </summary>
internal static class TileExportPackagePipeline
{
    private static readonly DateTimeOffset DeterministicTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static async Task<TileExportPackageFacts> WriteAsync(
        TileExportJobPlan plan,
        Stream destination,
        IAsyncEnumerable<TilePackageWriter.PackagedTile> tiles,
        CancellationToken cancellationToken,
        CompactTilePackageLimits? compactLimits = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(tiles);

        // Format admissions happen before the renderer-backed stream is enumerated or the
        // destination is changed. The CompactV2 writer retains its own defensive checks.
        TileExportExecutionSpecBuilder.Validate(plan);
        if (!destination.CanWrite)
            throw new ArgumentException("Tile-export destination must be writable.", nameof(destination));
        if (plan.PackageFormat == TileExportPackageFormat.Tpkx && plan.ZoomLevels.Length < 2)
            throw new ArgumentException("TPKX export requires at least two ordered zoom levels.", nameof(plan));
        if (plan.PackageFormat == TileExportPackageFormat.Zip && plan.TileImageFormat == "MIXED")
            throw new ArgumentException("Flat ZIP export requires one deterministic tile image extension.", nameof(plan));

        var startingPosition = destination.CanSeek ? destination.Position : (long?)null;
        var admittedTiles = new CanonicalTileStream(plan);
        var count = plan.PackageFormat switch
        {
            TileExportPackageFormat.Zip => await WriteFlatZipAsync(
                destination,
                GetExtension(plan.TileImageFormat),
                admittedTiles.ValidateAsync(tiles, cancellationToken),
                cancellationToken).ConfigureAwait(false),
            TileExportPackageFormat.Tpkx => await CompactTilePackageWriter.WriteAsync(
                destination,
                plan.ResourceId,
                plan.TileImageFormat,
                [plan.West, plan.South, plan.East, plan.North],
                admittedTiles.ValidateAsync(tiles, cancellationToken),
                cancellationToken,
                compactLimits).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(plan), "Unsupported tile package format.")
        };

        long? sizeBytes = startingPosition is { } start && destination.CanSeek
            ? destination.Position - start
            : null;
        return new TileExportPackageFacts(plan.PackageFormat, count, sizeBytes);
    }

    private static async Task<int> WriteFlatZipAsync(
        Stream destination,
        string extension,
        IAsyncEnumerable<TilePackageWriter.PackagedTile> tiles,
        CancellationToken cancellationToken)
    {
        var count = 0;
        using (var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
        {
            await foreach (var tile in tiles.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{tile.Level}/{tile.Column}/{tile.Row}.{extension}");
                var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                entry.LastWriteTime = DeterministicTimestamp;
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(tile.Bytes, cancellationToken).ConfigureAwait(false);
                count++;
            }
        }

        return count;
    }

    private static string GetExtension(string format)
        => format == "JPEG" ? "jpg" : "png";

    private sealed class CanonicalTileStream(TileExportJobPlan plan)
    {
        private TileOrderKey? _previous;
        private long _count;

        internal async IAsyncEnumerable<TilePackageWriter.PackagedTile> ValidateAsync(
            IAsyncEnumerable<TilePackageWriter.PackagedTile> tiles,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using var enumerator = tiles.GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    yield break;
                cancellationToken.ThrowIfCancellationRequested();
                var tile = enumerator.Current;
                ValidateCoordinate(tile);
                if (tile.Bytes is null || tile.Bytes.Length == 0)
                    throw new InvalidOperationException("Canonical tile streams cannot contain empty tile payloads.");

                var current = TileOrderKey.From(tile);
                if (_previous is { } previous && current.CompareTo(previous) <= 0)
                    throw new InvalidOperationException("Canonical tile streams must be unique and ordered by level, bundle row, bundle column, row, and column.");
                _previous = current;

                _count++;
                if (_count > plan.MaxTiles)
                    throw new InvalidOperationException($"Tile-export tile limit of {plan.MaxTiles} was exceeded.");
                yield return tile;
            }
        }

        private void ValidateCoordinate(TilePackageWriter.PackagedTile tile)
        {
            if (!plan.ZoomLevels.Contains(tile.Level))
                throw new InvalidOperationException($"Tile level {tile.Level} is not admitted by the export plan.");
            var dimension = 1L << tile.Level;
            if (tile.Row < 0 || tile.Column < 0 || tile.Row >= dimension || tile.Column >= dimension)
                throw new InvalidOperationException("Tile coordinates fall outside the WebMercatorQuad level bounds.");
        }
    }

    private readonly record struct TileOrderKey(int Level, int BundleRow, int BundleColumn, int Row, int Column)
        : IComparable<TileOrderKey>
    {
        internal static TileOrderKey From(TilePackageWriter.PackagedTile tile)
            => new(tile.Level, tile.Row / 128, tile.Column / 128, tile.Row, tile.Column);

        public int CompareTo(TileOrderKey other)
        {
            var comparison = Level.CompareTo(other.Level);
            if (comparison != 0) return comparison;
            comparison = BundleRow.CompareTo(other.BundleRow);
            if (comparison != 0) return comparison;
            comparison = BundleColumn.CompareTo(other.BundleColumn);
            if (comparison != 0) return comparison;
            comparison = Row.CompareTo(other.Row);
            return comparison != 0 ? comparison : Column.CompareTo(other.Column);
        }
    }
}

internal readonly record struct TileExportPackageFacts(
    TileExportPackageFormat Format,
    long TileCount,
    long? SizeBytes);
