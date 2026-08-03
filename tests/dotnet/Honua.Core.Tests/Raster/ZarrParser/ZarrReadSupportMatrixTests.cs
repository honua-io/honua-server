// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;

namespace Honua.Core.Tests.Raster.ZarrParser;

public sealed class ZarrReadSupportMatrixTests
{
    public static TheoryData<ZarrFormatVersion, string, string, int, string> SupportedDataTypes => new()
    {
        { ZarrFormatVersion.V2, "|b1", "|b1", 1, "01" },
        { ZarrFormatVersion.V2, "|i1", "|i1", 1, "FE" },
        { ZarrFormatVersion.V2, "|u1", "|u1", 1, "FE" },
        { ZarrFormatVersion.V2, "<i2", "<i2", 2, "FEFF" },
        { ZarrFormatVersion.V2, "<u2", "<u2", 2, "FEFF" },
        { ZarrFormatVersion.V2, "<i4", "<i4", 4, "FEFFFFFF" },
        { ZarrFormatVersion.V2, "<u4", "<u4", 4, "FEFFFFFF" },
        { ZarrFormatVersion.V2, "<i8", "<i8", 8, "FEFFFFFFFFFFFFFF" },
        { ZarrFormatVersion.V2, "<u8", "<u8", 8, "FEFFFFFFFFFFFFFF" },
        { ZarrFormatVersion.V2, "<f4", "<f4", 4, "0000C03F" },
        { ZarrFormatVersion.V2, "<f8", "<f8", 8, "000000000000F83F" },
        { ZarrFormatVersion.V3, "bool", "|b1", 1, "01" },
        { ZarrFormatVersion.V3, "int8", "|i1", 1, "FE" },
        { ZarrFormatVersion.V3, "uint8", "|u1", 1, "FE" },
        { ZarrFormatVersion.V3, "int16", "<i2", 2, "FEFF" },
        { ZarrFormatVersion.V3, "uint16", "<u2", 2, "FEFF" },
        { ZarrFormatVersion.V3, "int32", "<i4", 4, "FEFFFFFF" },
        { ZarrFormatVersion.V3, "uint32", "<u4", 4, "FEFFFFFF" },
        { ZarrFormatVersion.V3, "int64", "<i8", 8, "FEFFFFFFFFFFFFFF" },
        { ZarrFormatVersion.V3, "uint64", "<u8", 8, "FEFFFFFFFFFFFFFF" },
        { ZarrFormatVersion.V3, "float32", "<f4", 4, "0000C03F" },
        { ZarrFormatVersion.V3, "float64", "<f8", 8, "000000000000F83F" },
    };

    [Fact]
    public void Entries_CoverRequiredAxesForBothVersions()
    {
        var requiredAxes = new[] { "format", "codec", "dtype", "endian", "order", "dimensions", "metadata", "chunk-key" };

        foreach (var version in new[] { ZarrFormatVersion.V2, ZarrFormatVersion.V3 })
        {
            var rows = ZarrReadSupportMatrix.Entries.Where(entry => entry.Version == version).ToArray();
            rows.Select(entry => entry.Axis).Distinct().Should().Contain(requiredAxes);
            rows.Should().Contain(entry => entry.Supported);
            rows.Should().Contain(entry => !entry.Supported);
        }
    }

    [Theory]
    [MemberData(nameof(SupportedDataTypes))]
    public async Task AdvertisedDataType_IsFixtureBackedAndReadable(
        ZarrFormatVersion version,
        string advertisedDataType,
        string normalizedDataType,
        int elementSize,
        string payloadHex)
    {
        const string root = "matrix/data";
        var payload = Convert.FromHexString(payloadHex);
        var objects = BuildSingleCellFixture(root, version, advertisedDataType, payload);
        var reader = new InMemoryZarrRangeReader(objects);

        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", root);
        var result = await new ZarrSubsetReader().ReadSubsetAsync(
            reader,
            "bucket",
            root,
            metadata,
            new ZarrSubsetRequest
            {
                Variable = "data",
                Start = [0],
                Stop = [1]
            });

        metadata.ZarrFormat.Should().Be(version);
        metadata.Arrays[0].DataType.Should().Be(normalizedDataType);
        ZarrSubsetReader.ResolveElementSize(normalizedDataType).Should().Be(elementSize);
        result.Data.Should().Equal(payload);
    }

    [Theory]
    [InlineData(">i2")]
    [InlineData("=i4")]
    [InlineData("<f2")]
    [InlineData("<f16")]
    [InlineData("<c8")]
    [InlineData("|S4")]
    public async Task V2DataTypeOutsideMatrix_IsRejectedAtMetadataAdmission(string dataType)
    {
        var objects = BuildSingleCellFixture("matrix/rejected-v2", ZarrFormatVersion.V2, dataType, [0]);
        var reader = new InMemoryZarrRangeReader(objects);

        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "matrix/rejected-v2");

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*support matrix*");
    }

    [Theory]
    [InlineData("float16")]
    [InlineData("complex64")]
    [InlineData("string")]
    public async Task V3DataTypeOutsideMatrix_IsRejectedAtMetadataAdmission(string dataType)
    {
        var objects = BuildSingleCellFixture("matrix/rejected-v3", ZarrFormatVersion.V3, dataType, [0]);
        var reader = new InMemoryZarrRangeReader(objects);

        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "matrix/rejected-v3");

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*unsupported data_type*");
    }

    [Fact]
    public async Task V2UnknownCompressor_IsRejectedDuringMetadataAdmission()
    {
        var objects = BuildSingleCellFixture(
            "matrix/v2-blosc",
            ZarrFormatVersion.V2,
            "<f4",
            [0, 0, 0, 0],
            compressorJson: "{\"id\":\"blosc\"}");
        var reader = new InMemoryZarrRangeReader(objects);

        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "matrix/v2-blosc");

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*blosc*support matrix*");
    }

    [Theory]
    [InlineData("[{\"name\":\"gzip\",\"configuration\":{}}]")]
    [InlineData("[{\"name\":\"bytes\",\"configuration\":{}}]")]
    [InlineData("[{\"name\":\"bytes\",\"configuration\":{\"endian\":\"big\"}}]")]
    [InlineData("[{\"name\":\"gzip\",\"configuration\":{}},{\"name\":\"bytes\",\"configuration\":{\"endian\":\"little\"}}]")]
    [InlineData("[{\"name\":\"bytes\",\"configuration\":{\"endian\":\"little\"}},{\"name\":\"gzip\",\"configuration\":{}},{\"name\":\"gzip\",\"configuration\":{}}]")]
    public async Task V3CodecPipelineOutsideMatrix_IsRejected(string codecsJson)
    {
        var objects = BuildV3Fixture("matrix/v3-codec", "float32", [0, 0, 0, 0], codecsJson, "default", "/");
        var reader = new InMemoryZarrRangeReader(objects);

        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "matrix/v3-codec");

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData(ZarrFormatVersion.V2, "v2", "/", "0")]
    [InlineData(ZarrFormatVersion.V3, "default", ".", "c.0")]
    [InlineData(ZarrFormatVersion.V3, "v2", "/", "0")]
    public async Task AdvertisedChunkKeyLayouts_AreFixtureBacked(
        ZarrFormatVersion version,
        string encoding,
        string separator,
        string chunkKey)
    {
        const string root = "matrix/chunk-key";
        var payload = Convert.FromHexString("0000C03F");
        Dictionary<string, byte[]> objects;
        if (version == ZarrFormatVersion.V2)
        {
            objects = BuildSingleCellFixture(root, version, "<f4", payload, dimensionSeparator: separator);
        }
        else
        {
            objects = BuildV3Fixture(
                root,
                "float32",
                payload,
                "[{\"name\":\"bytes\",\"configuration\":{\"endian\":\"little\"}}]",
                encoding,
                separator);
        }

        objects.Should().ContainKey($"{root}/{chunkKey}");
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", root);
        var result = await new ZarrSubsetReader().ReadSubsetAsync(
            reader,
            "bucket",
            root,
            metadata,
            new ZarrSubsetRequest { Variable = metadata.Arrays[0].Name, Start = [0], Stop = [1] });

        result.Data.Should().Equal(payload);
    }

    [Fact]
    public async Task V2SlashDimensionSeparator_UsesHierarchicalChunkKey()
    {
        const string root = "matrix/v2-slash";
        var payload = Convert.FromHexString("0000C03F");
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"{root}/.zarray"] = Encoding.UTF8.GetBytes(
                "{\"chunks\":[1,1],\"compressor\":null,\"dimension_separator\":\"/\"," +
                "\"dtype\":\"<f4\",\"fill_value\":0,\"filters\":null,\"order\":\"C\"," +
                "\"shape\":[1,1],\"zarr_format\":2}"),
            [$"{root}/0/0"] = payload,
        };
        var reader = new InMemoryZarrRangeReader(objects);

        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", root);
        var result = await new ZarrSubsetReader().ReadSubsetAsync(
            reader,
            "bucket",
            root,
            metadata,
            new ZarrSubsetRequest
            {
                Variable = metadata.Arrays[0].Name,
                Start = [0, 0],
                Stop = [1, 1]
            });

        result.Data.Should().Equal(payload);
    }

    [Theory]
    [InlineData(ZarrFormatVersion.V2, "|", null)]
    [InlineData(ZarrFormatVersion.V3, "default", "|")]
    public async Task ChunkSeparatorOutsideMatrix_IsRejected(
        ZarrFormatVersion version,
        string encodingOrSeparator,
        string? separator = null)
    {
        var actualSeparator = separator ?? encodingOrSeparator;
        var objects = version == ZarrFormatVersion.V2
            ? BuildSingleCellFixture("matrix/bad-separator", version, "<f4", [0, 0, 0, 0], dimensionSeparator: actualSeparator)
            : BuildV3Fixture(
                "matrix/bad-separator",
                "float32",
                [0, 0, 0, 0],
                "[{\"name\":\"bytes\",\"configuration\":{\"endian\":\"little\"}}]",
                encodingOrSeparator,
                actualSeparator);
        var reader = new InMemoryZarrRangeReader(objects);

        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "matrix/bad-separator");

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*separator*");
    }

    private static Dictionary<string, byte[]> BuildSingleCellFixture(
        string root,
        ZarrFormatVersion version,
        string dataType,
        byte[] payload,
        string compressorJson = "null",
        string dimensionSeparator = ".")
    {
        if (version == ZarrFormatVersion.V3)
        {
            return BuildV3Fixture(
                root,
                dataType,
                payload,
                "[{\"name\":\"bytes\",\"configuration\":{\"endian\":\"little\"}}]",
                "default",
                "/");
        }

        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"{root}/.zarray"] = Encoding.UTF8.GetBytes(
                "{\"chunks\":[1],\"compressor\":" + compressorJson +
                ",\"dimension_separator\":\"" + dimensionSeparator +
                "\",\"dtype\":\"" + dataType +
                "\",\"fill_value\":0,\"filters\":null,\"order\":\"C\",\"shape\":[1],\"zarr_format\":2}"),
            [$"{root}/0"] = payload,
        };
    }

    private static Dictionary<string, byte[]> BuildV3Fixture(
        string root,
        string dataType,
        byte[] payload,
        string codecsJson,
        string chunkKeyEncoding,
        string separator)
    {
        var chunkKey = chunkKeyEncoding == "default" ? $"c{separator}0" : "0";
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"{root}/zarr.json"] = Encoding.UTF8.GetBytes(
                "{\"zarr_format\":3,\"node_type\":\"array\",\"shape\":[1],\"data_type\":\"" + dataType +
                "\",\"chunk_grid\":{\"name\":\"regular\",\"configuration\":{\"chunk_shape\":[1]}}," +
                "\"chunk_key_encoding\":{\"name\":\"" + chunkKeyEncoding + "\",\"configuration\":{\"separator\":\"" + separator +
                "\"}},\"fill_value\":0,\"codecs\":" + codecsJson + ",\"dimension_names\":[\"x\"]}"),
            [$"{root}/{chunkKey}"] = payload,
        };
    }
}
