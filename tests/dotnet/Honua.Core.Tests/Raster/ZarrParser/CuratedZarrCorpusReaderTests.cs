// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Core.Tests.Raster.ZarrParser;

/// <summary>
/// Reads the committed curated Zarr cube through the server's own reader (honua-server#4395).
/// </summary>
/// <remarks>
/// <para>
/// <c>CuratedCorpusTests.ZarrAsset_IsARealTwoSliceFloat32Cube</c> proves the fixture is a valid
/// two-slice Float32 cube — but it does so with its own <c>BitConverter</c> decode and never
/// touches <c>ZarrMetadataExtractor</c>, <c>ZarrSubsetReader</c> or the renderer. The fixture and
/// the reader were therefore each proven alone and never together, which is exactly the gap where
/// a reader that misinterprets chunk layout, dtype endianness, dimension order or the fill value
/// survives.
/// </para>
/// <para>
/// The expected values here are the same ones the corpus test asserts independently, so this is a
/// cross-check between two decoders, not a snapshot of the reader's own output.
/// </para>
/// </remarks>
public sealed class CuratedZarrCorpusReaderTests
{
    private const string Bucket = "curated";
    private const string Root = "sea-surface-temperature.zarr";
    private const string Variable = "temperature";

    [UnitTest]
    public async Task CuratedCube_ReadThroughTheServerReader_MatchesTheIndependentlyDecodedCells()
    {
        var (reader, _) = BuildReader();

        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, Bucket, Root);

        var array = metadata.Arrays.Should().ContainSingle(candidate => candidate.Name == Variable).Subject;
        array.Shape.Should().Equal([2, 3, 4], "the curated cube is time x latitude x longitude");
        array.DataType.Should().Be("<f4");
        array.DimensionNames.Should().Equal(["time", "latitude", "longitude"]);

        // Whole-cube read through the production subset reader.
        var subset = await new ZarrSubsetReader().ReadSubsetAsync(
            reader,
            Bucket,
            Root,
            metadata,
            new ZarrSubsetRequest
            {
                Variable = Variable,
                Start = [0, 0, 0],
                Stop = [2, 3, 4],
            });

        subset.Shape.Should().Equal([2, 3, 4]);
        subset.DataType.Should().Be("<f4");
        subset.Data.Length.Should().Be(24 * sizeof(float));

        // The same four cells CuratedCorpusTests decodes by hand, now via the server's reader.
        ReadSingle(subset.Data, 0).Should().Be(10f, "first cell of the first time slice");
        ReadSingle(subset.Data, 11).Should().Be(21f, "last cell of the first time slice");
        ReadSingle(subset.Data, 12).Should().Be(35f, "first cell of the second time slice");
        ReadSingle(subset.Data, 23).Should().Be(24f, "last cell of the second time slice");
    }

    /// <summary>
    /// A single time slice must come back as that slice, not as the head of the flattened cube.
    /// This is the read a datacube tile request performs, and the case that catches a reader
    /// which ignores the leading dimension's offset.
    /// </summary>
    [UnitTest]
    public async Task CuratedCube_SecondTimeSlice_ReadsThatSliceNotTheFirst()
    {
        var (reader, chunk) = BuildReader();

        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, Bucket, Root);

        var subset = await new ZarrSubsetReader().ReadSubsetAsync(
            reader,
            Bucket,
            Root,
            metadata,
            new ZarrSubsetRequest
            {
                Variable = Variable,
                Start = [1, 0, 0],
                Stop = [2, 3, 4],
            });

        subset.Shape.Should().Equal([1, 3, 4]);
        subset.Data.Length.Should().Be(12 * sizeof(float));

        // Oracle: cells 12..23 of the raw chunk, decoded independently of the reader.
        var expected = new List<float>();
        for (var index = 12; index < 24; index++)
        {
            expected.Add(ReadSingle(chunk, index));
        }

        var actual = new List<float>();
        for (var index = 0; index < 12; index++)
        {
            actual.Add(ReadSingle(subset.Data, index));
        }

        actual.Should().Equal(expected, "a start offset on the time axis must select the second slice");
        actual.Should().NotEqual(
            [ReadSingle(chunk, 0), ReadSingle(chunk, 1), ReadSingle(chunk, 2), ReadSingle(chunk, 3),
             ReadSingle(chunk, 4), ReadSingle(chunk, 5), ReadSingle(chunk, 6), ReadSingle(chunk, 7),
             ReadSingle(chunk, 8), ReadSingle(chunk, 9), ReadSingle(chunk, 10), ReadSingle(chunk, 11)],
            "a reader that ignored the time offset would return the FIRST slice");
    }

    /// <summary>
    /// Loads the digest-verified corpus objects into a range reader under the keys a Zarr store
    /// uses, so the extractor walks the same layout it would in object storage.
    /// </summary>
    private static (InMemoryZarrRangeReader Reader, byte[] Chunk) BuildReader()
    {
        var corpus = CuratedCorpus.Load();
        var chunk = corpus.ReadAllBytes("sst-temperature-chunk-0");

        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"{Root}/.zgroup"] = corpus.ReadAllBytes("sst-zarr-group"),
            [$"{Root}/.zattrs"] = corpus.ReadAllBytes("sst-zarr-group-attributes"),
            [$"{Root}/{Variable}/.zarray"] = corpus.ReadAllBytes("sst-temperature-array"),
            [$"{Root}/{Variable}/.zattrs"] = corpus.ReadAllBytes("sst-temperature-attributes"),
            [$"{Root}/{Variable}/0.0.0"] = chunk,
        };

        return (new InMemoryZarrRangeReader(objects), chunk);
    }

    private static float ReadSingle(byte[] bytes, int index)
        => BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index * sizeof(float), sizeof(float))));
}
