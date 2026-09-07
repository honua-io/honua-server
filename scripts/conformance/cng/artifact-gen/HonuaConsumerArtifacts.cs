// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

// Honua-produced COG and Zarr artifacts for the CNG conformance lane (#4398).
//
// Before this, the lane's COG cell validated a file `rio_cogeo.cog_translate`
// wrote and its Zarr cell validated a store `xarray.to_zarr` wrote, so both were
// Python validating Python's own output with no Honua code in the loop. The
// direction is now inverted: third-party tooling produces the *inputs*, Honua's
// production cloud-native readers consume them over HTTP range requests, and the
// canonical clients validate what **Honua** emitted.
//
//   canonical.webmercator.cog.tif  --(CogMetadataExtractor + TileDecompressor
//                                     + CogTiffTileEncoder)-->  honua.cog.tif
//   canonical.zarr                 --(ZarrMetadataExtractor
//                                     + ZarrSubsetReader)-->  decoded subset
//
// Every byte both readers pull crosses a real HTTP/1.1 origin that counts
// requests, ranged requests, whole-object pulls and transferred bytes, so the
// `min_range_requests` / `max_full_object_downloads` budgets the lane declares
// are measured against observed traffic.

using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;

namespace Honua.Cng.ArtifactGen;

internal static class HonuaConsumerArtifacts
{
    private const string Bucket = "cng-conformance";
    internal const string CogSourceKey = "canonical.webmercator.cog.tif";
    internal const string ZarrRootKey = "canonical.zarr";
    internal const string ZarrVariable = "temperature";

    // The subset deliberately starts and stops off a chunk boundary on every axis so
    // a reader that silently widened the request to whole chunks would return the
    // wrong shape, and so chunk pruning is observable: 8 of the array's 32 chunks.
    internal static readonly int[] SubsetStart = [1, 2, 4];
    internal static readonly int[] SubsetStop = [3, 6, 12];

    /// <summary>
    /// Drives Honua's COG and Zarr readers over the canonical fixtures through a
    /// counting HTTP range origin, writes the transcoded COG tile, and returns the
    /// evidence document the validator consumes.
    /// </summary>
    internal static async Task<JsonObject> GenerateAsync(string fixtureDirectory, string outputDirectory)
    {
        var evidence = new JsonObject
        {
            ["schema"] = "honua-cng-consumer-evidence/v1",
            ["source_directory"] = Path.GetFileName(Path.GetFullPath(fixtureDirectory)),
        };

        evidence["cog"] = await TranscodeCogTileAsync(fixtureDirectory, outputDirectory).ConfigureAwait(false);
        evidence["zarr"] = await ReadZarrSubsetAsync(fixtureDirectory).ConfigureAwait(false);
        return evidence;
    }

    private static async Task<JsonObject> TranscodeCogTileAsync(string fixtureDirectory, string outputDirectory)
    {
        var counters = new TransferCounters();
        var origin = new LocalRangeHttpOrigin(fixtureDirectory, counters);
        await using (origin.ConfigureAwait(false))
        {
            using var client = new HttpClient { BaseAddress = origin.BaseAddress };
            var reader = new HttpRangeCloudReader(client);

            var metadata = await new CogMetadataExtractor()
                .ReadMetadataAsync(reader, Bucket, CogSourceKey)
                .ConfigureAwait(false);

            if (!TileDecompressor.IsSupported(metadata.Compression))
            {
                throw new InvalidOperationException(
                    $"Honua cannot serve tiles from a {metadata.Compression}-compressed COG.");
            }

            var baseLevel = metadata.OverviewLevels[0];
            if (baseLevel.TileOffsets.Length == 0 || baseLevel.TileByteCounts.Length == 0)
            {
                throw new InvalidDataException("The source COG's base level declares no tiles.");
            }

            // Tile 0 is the north-west tile of the base level; its extent follows from
            // the base extent and the pixel size, which is what a serving path resolves.
            var offset = baseLevel.TileOffsets[0];
            var byteCount = baseLevel.TileByteCounts[0];
            var compressed = await reader
                .ReadRangeAsync(Bucket, CogSourceKey, offset, byteCount)
                .ConfigureAwait(false);
            if (compressed.Length != byteCount)
            {
                throw new InvalidDataException(
                    $"Ranged tile read returned {compressed.Length} bytes, expected {byteCount}.");
            }

            var layout = new TilePixelLayout(
                metadata.TileWidth,
                metadata.BandCount,
                metadata.BitsPerSample,
                metadata.Predictor,
                metadata.IsLittleEndian);
            var (samples, _) = TileDecompressor.Decompress(compressed, metadata.Compression, layout);

            var pixelWidth = (metadata.Extent.XMax - metadata.Extent.XMin) / metadata.Width;
            var pixelHeight = (metadata.Extent.YMax - metadata.Extent.YMin) / metadata.Height;
            var tileExtent = new RasterExtent
            {
                XMin = metadata.Extent.XMin,
                YMax = metadata.Extent.YMax,
                XMax = metadata.Extent.XMin + (pixelWidth * metadata.TileWidth),
                YMin = metadata.Extent.YMax - (pixelHeight * metadata.TileHeight),
                Srid = metadata.Srid,
            };

            var tiff = CogTiffTileEncoder.Encode(samples, metadata, tileExtent)
                ?? throw new InvalidOperationException(
                    "Honua's GeoTIFF tile encoder refused the decoded tile layout; the lane has no transcoded artifact to validate.");

            var artifactPath = Path.Combine(outputDirectory, "honua.cog.tif");
            await File.WriteAllBytesAsync(artifactPath, tiff).ConfigureAwait(false);
            Console.WriteLine(
                $"Honua COG transcode: {artifactPath} ({tiff.Length} bytes, {metadata.TileWidth}x{metadata.TileHeight} EPSG:{metadata.Srid}) "
                + $"from {counters.Requests} HTTP requests / {counters.TransferredBytes} bytes");

            return new JsonObject
            {
                ["artifact"] = "honua.cog.tif",
                ["source_artifact"] = CogSourceKey,
                ["producer"] = "CogMetadataExtractor + TileDecompressor + CogTiffTileEncoder",
                ["source_metadata"] = new JsonObject
                {
                    ["srid"] = metadata.Srid,
                    ["width"] = metadata.Width,
                    ["height"] = metadata.Height,
                    ["tile_width"] = metadata.TileWidth,
                    ["tile_height"] = metadata.TileHeight,
                    ["band_count"] = metadata.BandCount,
                    ["pixel_type"] = metadata.PixelType,
                    ["compression"] = metadata.Compression,
                    ["predictor"] = metadata.Predictor,
                    ["nodata"] = metadata.NoData,
                    ["overview_levels"] = metadata.OverviewLevels.Length,
                },
                ["transcoded_extent"] = new JsonObject
                {
                    ["xmin"] = tileExtent.XMin,
                    ["ymin"] = tileExtent.YMin,
                    ["xmax"] = tileExtent.XMax,
                    ["ymax"] = tileExtent.YMax,
                    ["srid"] = tileExtent.Srid,
                },
                ["observed_transfer"] = ToJson(counters.ToEvidence()),
            };
        }
    }

    private static async Task<JsonObject> ReadZarrSubsetAsync(string fixtureDirectory)
    {
        var counters = new TransferCounters();
        var origin = new LocalRangeHttpOrigin(fixtureDirectory, counters);
        await using (origin.ConfigureAwait(false))
        {
            using var client = new HttpClient { BaseAddress = origin.BaseAddress };
            var reader = new HttpRangeCloudReader(client);

            var store = await new ZarrMetadataExtractor()
                .ReadMetadataAsync(reader, Bucket, ZarrRootKey)
                .ConfigureAwait(false);
            var array = store.Arrays.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, ZarrVariable, StringComparison.Ordinal))
                ?? throw new InvalidDataException(
                    $"Honua's Zarr reader did not discover the '{ZarrVariable}' array in the canonical store.");

            var metadataRequests = counters.Requests;

            var subset = await new ZarrSubsetReader()
                .ReadSubsetAsync(
                    reader,
                    Bucket,
                    ZarrRootKey,
                    store,
                    new ZarrSubsetRequest { Variable = ZarrVariable, Start = SubsetStart, Stop = SubsetStop })
                .ConfigureAwait(false);

            var values = DecodeFloat32(subset.Data, subset.DataType);
            var chunkObjects = counters.DistinctChunkObjects($"{ZarrRootKey}/{ZarrVariable}/");
            var totalChunks = 1L;
            for (var i = 0; i < array.Shape.Length; i++)
            {
                totalChunks *= (array.Shape[i] + array.Chunks[i] - 1) / array.Chunks[i];
            }

            Console.WriteLine(
                $"Honua Zarr subset: {ZarrVariable}[{string.Join(",", SubsetStart)}..{string.Join(",", SubsetStop)}] "
                + $"-> shape [{string.Join(",", subset.Shape)}] {subset.DataType}, "
                + $"{chunkObjects}/{totalChunks} chunks over {counters.Requests} HTTP requests");

            return new JsonObject
            {
                ["artifact"] = "decoded-subset",
                ["source_artifact"] = ZarrRootKey,
                ["producer"] = "ZarrMetadataExtractor + ZarrSubsetReader",
                ["variable"] = subset.Variable,
                ["start"] = ToJson(SubsetStart),
                ["stop"] = ToJson(SubsetStop),
                ["shape"] = ToJson(subset.Shape),
                ["data_type"] = subset.DataType,
                ["values"] = ToJson(values),
                ["store_metadata"] = new JsonObject
                {
                    ["zarr_format"] = (int)array.ZarrFormat,
                    ["shape"] = ToJson(array.Shape),
                    ["chunks"] = ToJson(array.Chunks),
                    ["dtype"] = array.DataType,
                    ["chunk_count"] = totalChunks,
                    ["compressor"] = array.Compressor,
                    ["dimension_names"] = ToJson(array.DimensionNames),
                },
                ["chunk_reads"] = new JsonObject
                {
                    ["chunk_objects_read"] = chunkObjects,
                    ["chunk_objects_total"] = totalChunks,
                    ["metadata_requests"] = metadataRequests,
                },
                ["observed_transfer"] = ToJson(counters.ToEvidence()),
            };
        }
    }

    private static double[] DecodeFloat32(byte[] data, string dataType)
    {
        if (!string.Equals(dataType, "<f4", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The canonical Zarr fixture must be little-endian float32; Honua read '{dataType}'.");
        }

        if (data.Length % sizeof(float) != 0)
        {
            throw new InvalidDataException(
                $"Zarr subset payload of {data.Length} bytes is not a whole number of float32 samples.");
        }

        var values = new double[data.Length / sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(i * sizeof(float), sizeof(float)));
        }

        return values;
    }

    private static JsonArray ToJson(IEnumerable<int> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonArray ToJson(IEnumerable<double> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonArray ToJson(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonObject ToJson(Dictionary<string, object> values)
    {
        var node = new JsonObject();
        foreach (var (key, value) in values)
        {
            node[key] = value switch
            {
                int number => JsonValue.Create(number),
                long number => JsonValue.Create(number),
                double number => JsonValue.Create(number),
                _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
            };
        }

        return node;
    }
}
