// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
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
    private const string GroupRoot = "sea-surface-temperature.zarr";
    private const string Variable = "temperature";

    /// <summary>
    /// The array is addressed directly rather than discovered from the group. The curated cube is
    /// CF-conventional (<c>Conventions: CF-1.8</c>, <c>_ARRAY_DIMENSIONS</c>) and carries neither
    /// a Honua-specific <c>variables</c> attribute nor a consolidated <c>.zmetadata</c>, which are
    /// the only two things <c>ZarrMetadataExtractor.ResolveVariables</c> reads. Pinned by
    /// <see cref="CuratedGroupRoot_HasNoDiscoverableVariables_SoTheArrayMustBeAddressedDirectly"/>.
    /// </summary>
    private const string ArrayRoot = GroupRoot + "/" + Variable;

    [UnitTest]
    public async Task CuratedCube_ReadThroughTheServerReader_MatchesTheIndependentlyDecodedCells()
    {
        var (reader, _) = BuildReader();

        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, Bucket, ArrayRoot);

        var array = metadata.Arrays.Should().ContainSingle().Subject;
        array.Shape.Should().Equal([2, 3, 4], "the curated cube is time x latitude x longitude");
        array.DataType.Should().Be("<f4");
        array.DimensionNames.Should().Equal(["time", "latitude", "longitude"]);

        // Whole-cube read through the production subset reader.
        var subset = await new ZarrSubsetReader().ReadSubsetAsync(
            reader,
            Bucket,
            ArrayRoot,
            metadata,
            new ZarrSubsetRequest
            {
                Variable = array.Name,
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

        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, Bucket, ArrayRoot);

        var subset = await new ZarrSubsetReader().ReadSubsetAsync(
            reader,
            Bucket,
            ArrayRoot,
            metadata,
            new ZarrSubsetRequest
            {
                Variable = metadata.Arrays[0].Name,
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
    /// Pins why the tests above address the array directly: a CF-conventional Zarr group is not
    /// enough for the extractor to find its variables (honua-server#4395).
    /// </summary>
    /// <remarks>
    /// <c>ZarrMetadataExtractor.ResolveVariables</c> discovers variables only from a
    /// <c>variables</c> array in the group's <c>.zattrs</c> or from a consolidated
    /// <c>.zmetadata</c>. The curated cube — which is what ordinary CF/xarray-written Zarr looks
    /// like — has neither, so opening it at the group root falls through to the
    /// "root is itself a single array" path and fails looking for a <c>.zarray</c> that is one
    /// level down. This test records the current behaviour rather than endorsing it; whether the
    /// extractor should also enumerate child groups is a product question, and the PR that added
    /// this test raises it.
    /// </remarks>
    [UnitTest]
    public async Task CuratedGroupRoot_HasNoDiscoverableVariables_SoTheArrayMustBeAddressedDirectly()
    {
        var (reader, _) = BuildReader();

        var groupRootFailure = await Assert.ThrowsAsync<InvalidDataException>(
            () => new ZarrMetadataExtractor().ReadMetadataAsync(reader, Bucket, GroupRoot));
        groupRootFailure.Message.Should().Contain(".zarray");

        // The same store opened at the array path resolves.
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, Bucket, ArrayRoot);
        metadata.Arrays.Should().ContainSingle();
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
            [$"{GroupRoot}/.zgroup"] = corpus.ReadAllBytes("sst-zarr-group"),
            [$"{GroupRoot}/.zattrs"] = corpus.ReadAllBytes("sst-zarr-group-attributes"),
            [$"{ArrayRoot}/.zarray"] = corpus.ReadAllBytes("sst-temperature-array"),
            [$"{ArrayRoot}/.zattrs"] = corpus.ReadAllBytes("sst-temperature-attributes"),
            [$"{ArrayRoot}/0.0.0"] = chunk,
        };

        return (new InMemoryZarrRangeReader(objects), chunk);
    }

    private static float ReadSingle(byte[] bytes, int index)
        => BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index * sizeof(float), sizeof(float))));
}
