// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.TestKit.Formats;

namespace Honua.Core.Tests.Raster.ZarrParser;

public class ZarrFillValueIntegrityTests
{
    private const string Root = "datasets/fill";

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ReadSubsetAsync_MissingBooleanChunk_PreservesDeclaredFill(bool v3, bool fill)
    {
        var subset = await ReadMissingChunk(v3, v3 ? "bool" : "|b1", fill ? "true" : "false");
        subset.Data.Should().Equal(Enumerable.Repeat(fill ? (byte)1 : (byte)0, 4));
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("0x7fc00000", 0x7fc00000U)]
    [InlineData("0x7f800001", 0x7f800001U)]
    [InlineData("0xffc01234", 0xffc01234U)]
    [InlineData("0x7f800000", 0x7f800000U)]
    [InlineData("0xff800000", 0xff800000U)]
    [InlineData("0x3f800000", 0x3f800000U)]
    public async Task ReadSubsetAsync_MissingFloat32Chunk_PreservesHexadecimalFillBits(string fill, uint bits)
    {
        var subset = await ReadMissingChunk(true, "float32", "\"" + fill + "\"");
        for (var offset = 0; offset < subset.Data.Length; offset += 4)
        {
            BinaryPrimitives.ReadUInt32LittleEndian(subset.Data.AsSpan(offset, 4)).Should().Be(bits);
        }
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(false, "9007199254740993", 9007199254740993L)]
    [InlineData(false, "9223372036854775807", long.MaxValue)]
    [InlineData(false, "-9223372036854775808", long.MinValue)]
    [InlineData(true, "9007199254740993", 9007199254740993L)]
    [InlineData(true, "9223372036854775807", long.MaxValue)]
    [InlineData(true, "-9223372036854775808", long.MinValue)]
    public async Task ReadSubsetAsync_MissingInt64Chunk_PreservesExactFill(bool v3, string fill, long expected)
    {
        var subset = await ReadMissingChunk(v3, v3 ? "int64" : "<i8", fill);
        for (var offset = 0; offset < subset.Data.Length; offset += 8)
        {
            BinaryPrimitives.ReadInt64LittleEndian(subset.Data.AsSpan(offset, 8)).Should().Be(expected);
        }
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(false, "9007199254740993", 9007199254740993UL)]
    [InlineData(false, "18446744073709551615", ulong.MaxValue)]
    [InlineData(true, "9007199254740993", 9007199254740993UL)]
    [InlineData(true, "18446744073709551615", ulong.MaxValue)]
    public async Task ReadSubsetAsync_MissingUInt64Chunk_PreservesExactFill(bool v3, string fill, ulong expected)
    {
        var subset = await ReadMissingChunk(v3, v3 ? "uint64" : "<u8", fill);
        for (var offset = 0; offset < subset.Data.Length; offset += 8)
        {
            BinaryPrimitives.ReadUInt64LittleEndian(subset.Data.AsSpan(offset, 8)).Should().Be(expected);
        }
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("bool", "1")]
    [InlineData("int8", "128")]
    [InlineData("uint8", "-1")]
    [InlineData("int16", "1.5")]
    [InlineData("int16", "1e0")]
    [InlineData("int64", "9223372036854775808")]
    [InlineData("uint64", "18446744073709551616")]
    [InlineData("float32", "true")]
    [InlineData("float64", "null")]
    [InlineData("float32", "\"inf\"")]
    public async Task ReadSubsetAsync_V3InvalidFill_RejectsInsteadOfSynthesizingSamples(string dtype, string fill)
    {
        var act = () => ReadMissingChunk(true, dtype, fill);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("float32", "0000C07F")]
    [InlineData("float64", "000000000000F87F")]
    public async Task ReadSubsetAsync_V3CanonicalNaNFill_PreservesRequiredPositiveQuietBits(string dtype, string expectedHex)
    {
        var subset = await ReadMissingChunk(true, dtype, "\"NaN\"");
        var scalar = Convert.FromHexString(expectedHex);
        for (var offset = 0; offset < subset.Data.Length; offset += scalar.Length)
        {
            subset.Data.AsSpan(offset, scalar.Length).ToArray().Should().Equal(scalar);
        }
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void TryGetFiniteFillValue_TypedFills_KeepExactMetadataAndOmitLossySentinels()
    {
        ZarrValueDecoder.TryGetFiniteFillValue(true, out var boolean).Should().BeTrue();
        boolean.Should().Be(1);
        ZarrValueDecoder.TryGetFiniteFillValue(1.5f, out var single).Should().BeTrue();
        single.Should().Be(1.5);
        ZarrValueDecoder.TryGetFiniteFillValue(-9999L, out var signed).Should().BeTrue();
        signed.Should().Be(-9999);
        ZarrValueDecoder.TryGetFiniteFillValue(42UL, out var unsigned).Should().BeTrue();
        unsigned.Should().Be(42);
        ZarrValueDecoder.TryGetFiniteFillValue(9007199254740993L, out _).Should().BeFalse();
        ZarrValueDecoder.TryGetFiniteFillValue(ulong.MaxValue, out _).Should().BeFalse();
        ZarrValueDecoder.TryGetFiniteFillValue(float.NaN, out _).Should().BeFalse();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("float32", "1e39", "0000807F")]
    [InlineData("float32", "1e999", "0000807F")]
    [InlineData("float64", "1e309", "000000000000F07F")]
    [InlineData("float32", "-1e-999", "00000080")]
    [InlineData("float32", "1.000000059604644775390625", "0000803F")]
    [InlineData("float32", "1.000000178813934326171875", "0200803F")]
    public async Task ReadSubsetAsync_V3NumericFill_RoundsToNearestIeeeValue(string dtype, string fill, string expectedHex)
    {
        // Zarr v3 data-types / permitted-fill-values: JSON numbers round to the
        // nearest representable value using round-half-to-even, including IEEE overflow.
        var subset = await ReadMissingChunk(true, dtype, fill);
        var scalar = Convert.FromHexString(expectedHex);
        for (var offset = 0; offset < subset.Data.Length; offset += scalar.Length)
        {
            subset.Data.AsSpan(offset, scalar.Length).ToArray().Should().Equal(scalar);
        }
    }

    private static async Task<ZarrSubsetResult> ReadMissingChunk(bool v3, string dtype, string fill)
    {
        var objects = v3
            ? ZarrFixtureBuilder.BuildV3SingleArray(Root, 2, 2, 2, 2, (r, c) => 0, gzip: false)
            : ZarrFixtureBuilder.BuildSingleVariableUncompressed(Root, 2, 2, 2, 2, (r, c) => 0);
        var key = Root + (v3 ? "/zarr.json" : "/.zarray");
        var document = JsonNode.Parse(objects[key])!;
        document[v3 ? "data_type" : "dtype"] = dtype;
        document["fill_value"] = JsonNode.Parse(fill);
        objects[key] = Encoding.UTF8.GetBytes(document.ToJsonString());
        objects.Remove(Root + (v3 ? "/c/0/0" : "/0.0"));
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", Root);
        return await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", Root, metadata,
            new ZarrSubsetRequest { Variable = "fill", Start = [0, 0], Stop = [2, 2] });
    }
}
