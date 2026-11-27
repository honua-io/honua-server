// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.TestKit.Formats;
using NSubstitute;
using Xunit;

namespace Honua.Core.Tests.Raster.ZarrParser;

public class ZarrMetadataExtractorTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_ConsolidatedGroupWithoutVariablesManifest_DiscoversArrays()
    {
        var objects = ZarrFixtureBuilder.BuildGroupedZlib(
            root: "stores/gdal",
            rows: 4,
            cols: 4,
            chunkRows: 4,
            chunkCols: 4,
            sample: (r, c) => r + c,
            srid: 4326,
            xMin: -180,
            yMin: -90,
            xMax: 180,
            yMax: 90);
        objects["stores/gdal/.zattrs"] = "{}"u8.ToArray();
        objects["stores/gdal/.zmetadata"] = Encoding.UTF8.GetBytes(
            "{\"zarr_consolidated_format\":1,\"metadata\":{\"temperature/.zarray\":{}}}");

        var metadata = await new ZarrMetadataExtractor()
            .ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", "stores/gdal");

        metadata.Arrays.Should().ContainSingle().Which.Name.Should().Be("temperature");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_LargeConsolidatedMetadata_IsNotTruncated()
    {
        var objects = ZarrFixtureBuilder.BuildGroupedZlib(
            root: "stores/large-metadata", rows: 4, cols: 4, chunkRows: 4, chunkCols: 4,
            sample: (r, c) => r + c, srid: 4326, xMin: -180, yMin: -90, xMax: 180, yMax: 90);
        objects["stores/large-metadata/.zattrs"] = Encoding.UTF8.GetBytes("{}");
        objects["stores/large-metadata/.zmetadata"] = Encoding.UTF8.GetBytes(
            "{\"zarr_consolidated_format\":1,\"metadata\":{\"temperature/.zarray\":{},\"padding\":\""
            + new string('x', 70_000) + "\"}}");

        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(
            new InMemoryZarrRangeReader(objects), "bucket", "stores/large-metadata");

        metadata.Arrays.Should().ContainSingle().Which.Name.Should().Be("temperature");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_SingleArrayUncompressed_DiscoversShapeAndDtype()
    {
        var objects = ZarrFixtureBuilder.BuildSingleVariableUncompressed(
            root: "stores/example",
            rows: 8,
            cols: 8,
            chunkRows: 4,
            chunkCols: 4,
            sample: (r, c) => (float)r * 10 + c);
        var reader = new InMemoryZarrRangeReader(objects);
        var extractor = new ZarrMetadataExtractor();

        var metadata = await extractor.ReadMetadataAsync(reader, "bucket", "stores/example");

        metadata.Arrays.Should().HaveCount(1);
        var array = metadata.Arrays[0];
        array.Name.Should().Be("example");
        array.RelativePath.Should().BeEmpty();
        array.Shape.Should().Equal(8, 8);
        array.Chunks.Should().Equal(4, 4);
        array.DataType.Should().Be("<f4");
        array.Compressor.Should().BeNull();
        metadata.ZarrFormat.Should().Be(ZarrFormatVersion.V2);
        metadata.PrimaryVariable.Should().Be("example");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_GroupWithCrsAndExtent_PopulatesGeoreferencing()
    {
        var objects = ZarrFixtureBuilder.BuildGroupedZlib(
            root: "stores/grouped",
            rows: 8,
            cols: 8,
            chunkRows: 4,
            chunkCols: 4,
            sample: (r, c) => r + c,
            srid: 4326,
            xMin: -180,
            yMin: -90,
            xMax: 180,
            yMax: 90);
        var reader = new InMemoryZarrRangeReader(objects);
        var extractor = new ZarrMetadataExtractor();

        var metadata = await extractor.ReadMetadataAsync(reader, "bucket", "stores/grouped");

        metadata.Srid.Should().Be(4326);
        metadata.Extent.XMin.Should().Be(-180);
        metadata.Extent.YMax.Should().Be(90);
        metadata.PrimaryVariable.Should().Be("temperature");
        metadata.Arrays.Should().HaveCount(1);
        metadata.Arrays[0].RelativePath.Should().Be("temperature");
        metadata.Arrays[0].Compressor.Should().Be("zlib");
        metadata.Arrays[0].DimensionNames.Should().Equal("y", "x");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_TemporalAttrsPresent_PopulatesTemporalExtent()
    {
        var objects = ZarrFixtureBuilder.BuildGroupedWithTime(
            root: "stores/timeseries",
            timeSteps: 5,
            rows: 8,
            cols: 8,
            includeTemporalAttrs: true);
        var reader = new InMemoryZarrRangeReader(objects);
        var extractor = new ZarrMetadataExtractor();

        var metadata = await extractor.ReadMetadataAsync(reader, "bucket", "stores/timeseries");

        metadata.TemporalDimension.Should().Be("time");
        metadata.Temporal.Should().NotBeNull();
        metadata.Temporal!.Start.Should().Be(new System.DateTimeOffset(2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero));
        metadata.Temporal.End.Should().Be(new System.DateTimeOffset(2026, 1, 5, 0, 0, 0, System.TimeSpan.Zero));
        metadata.Temporal.StepCount.Should().Be(5);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_TemporalAttrsAbsent_TemporalExtentNull()
    {
        var objects = ZarrFixtureBuilder.BuildGroupedWithTime(
            root: "stores/timeseries-notime",
            timeSteps: 5,
            rows: 8,
            cols: 8,
            includeTemporalAttrs: false);
        var reader = new InMemoryZarrRangeReader(objects);
        var extractor = new ZarrMetadataExtractor();

        var metadata = await extractor.ReadMetadataAsync(reader, "bucket", "stores/timeseries-notime");

        metadata.TemporalDimension.Should().Be("time");
        metadata.Temporal.Should().BeNull();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_InvalidJson_ThrowsInvalidData()
    {
        var objects = ZarrFixtureBuilder.BuildInvalidJson("stores/broken");
        var reader = new InMemoryZarrRangeReader(objects);
        var extractor = new ZarrMetadataExtractor();

        var act = () => extractor.ReadMetadataAsync(reader, "bucket", "stores/broken");

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_V3ArrayWithoutZarrJson_Rejected()
    {
        // A `.zarray` (v2 layout) that declares zarr_format:3 is malformed: real v3
        // stores carry metadata in zarr.json. The reader must reject it.
        var objects = ZarrFixtureBuilder.BuildUnsupportedV3("stores/v3-bad");
        var reader = new InMemoryZarrRangeReader(objects);
        var extractor = new ZarrMetadataExtractor();

        var act = () => extractor.ReadMetadataAsync(reader, "bucket", "stores/v3-bad");

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*v3*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_V3SingleArray_DiscoversShapeAndNormalizesDtype()
    {
        var objects = ZarrFixtureBuilder.BuildV3SingleArray(
            root: "stores/v3-single",
            rows: 8,
            cols: 8,
            chunkRows: 4,
            chunkCols: 4,
            sample: (r, c) => (float)r * 10 + c,
            gzip: false);
        var reader = new InMemoryZarrRangeReader(objects);
        var extractor = new ZarrMetadataExtractor();

        var metadata = await extractor.ReadMetadataAsync(reader, "bucket", "stores/v3-single");

        metadata.ZarrFormat.Should().Be(ZarrFormatVersion.V3);
        metadata.Arrays.Should().HaveCount(1);
        var array = metadata.Arrays[0];
        array.Name.Should().Be("v3-single");
        array.Shape.Should().Equal(8, 8);
        array.Chunks.Should().Equal(4, 4);
        array.DataType.Should().Be("<f4");
        array.Compressor.Should().BeNull();
        array.DimensionNames.Should().Equal("y", "x");
        array.ChunkKeyEncoding.Prefix.Should().Be("c");
        array.ChunkKeyEncoding.Separator.Should().Be("/");
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"name\":\"gzip\",\"configuration\":{\"level\":5}}]")]
    [InlineData("[{\"name\":\"gzip\",\"configuration\":{\"level\":5}},{\"name\":\"bytes\",\"configuration\":{\"endian\":\"little\"}}]")]
    [InlineData("[{\"name\":\"bytes\",\"configuration\":{\"endian\":\"little\"}},{\"name\":\"bytes\",\"configuration\":{\"endian\":\"little\"}}]")]
    [InlineData("[{\"name\":\"bytes\",\"configuration\":{\"endian\":\"little\"}},{\"name\":\"gzip\",\"configuration\":{\"level\":5}},{\"name\":\"gzip\",\"configuration\":{\"level\":5}}]")]
    [InlineData("[{\"name\":\"bytes\",\"configuration\":{\"endian\":\"invalid\"}}]")]
    [InlineData("[{\"name\":\"bytes\"}]")]
    [InlineData("[{\"name\":\"bytes\",\"configuration\":{\"endian\":3}}]")]
    [InlineData("[{\"name\":\"bytes\",\"configuration\":{\"endian\":\"big\"}}]")]
    [InlineData("[{\"name\":\"bytes\",\"configuration\":{\"endian\":\"little\"}},{\"name\":\"gzip\",\"configuration\":{\"level\":10}}]")]
    [InlineData("[{\"name\":\"bytes\",\"configuration\":{\"endian\":\"little\"}},{\"name\":\"gzip\"}]")]
    public async Task ReadMetadataAsync_V3UnsupportedPipeline_RejectedBeforeChunkRead(string codecs)
    {
        var objects = ZarrFixtureBuilder.BuildV3SingleArray("stores/pipeline", 2, 2, 2, 2, (r, c) => r + c, gzip: true);
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(objects["stores/pipeline/zarr.json"])!;
        metadata["codecs"] = System.Text.Json.Nodes.JsonNode.Parse(codecs);
        objects["stores/pipeline/zarr.json"] = System.Text.Encoding.UTF8.GetBytes(metadata.ToJsonString());
        // Only metadata is available: unsupported pipelines must fail before any sample access.
        objects.Remove("stores/pipeline/c/0/0");
        var reader = new InMemoryZarrRangeReader(objects);
        var act = () => new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "stores/pipeline");

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_V3Group_PopulatesGeoreferencing()
    {
        var objects = ZarrFixtureBuilder.BuildV3Group(
            root: "stores/v3-group",
            rows: 8,
            cols: 8,
            chunkRows: 4,
            chunkCols: 4,
            sample: (r, c) => r + c,
            srid: 4326,
            xMin: -180,
            yMin: -90,
            xMax: 180,
            yMax: 90);
        var reader = new InMemoryZarrRangeReader(objects);
        var extractor = new ZarrMetadataExtractor();

        var metadata = await extractor.ReadMetadataAsync(reader, "bucket", "stores/v3-group");

        metadata.ZarrFormat.Should().Be(ZarrFormatVersion.V3);
        metadata.Srid.Should().Be(4326);
        metadata.Extent.XMin.Should().Be(-180);
        metadata.Extent.YMax.Should().Be(90);
        metadata.PrimaryVariable.Should().Be("temperature");
        metadata.Arrays.Should().HaveCount(1);
        metadata.Arrays[0].RelativePath.Should().Be("temperature");
        metadata.Arrays[0].DimensionNames.Should().Equal("y", "x");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_V3UnsupportedCodec_Rejected()
    {
        var objects = ZarrFixtureBuilder.BuildV3UnsupportedCodec("stores/v3-blosc");
        var reader = new InMemoryZarrRangeReader(objects);
        var extractor = new ZarrMetadataExtractor();

        var act = () => extractor.ReadMetadataAsync(reader, "bucket", "stores/v3-blosc");

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*blosc*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_FortranOrder_Rejected()
    {
        var objects = ZarrFixtureBuilder.BuildFortranOrder("stores/fortran");
        var reader = new InMemoryZarrRangeReader(objects);
        var extractor = new ZarrMetadataExtractor();

        var act = () => extractor.ReadMetadataAsync(reader, "bucket", "stores/fortran");

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*Fortran*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_NonNullFilters_Rejected()
    {
        var objects = ZarrFixtureBuilder.BuildFilteredArray("stores/filtered");
        var reader = new InMemoryZarrRangeReader(objects);
        var extractor = new ZarrMetadataExtractor();

        var act = () => extractor.ReadMetadataAsync(reader, "bucket", "stores/filtered");

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*filters*not supported*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_EvenlySpacedVerticalAxis_IsDiscovered()
    {
        var objects = ZarrFixtureBuilder.BuildGroupedWithVerticalAxis(
            root: "stores/vertical", levels: 4, rows: 4, cols: 4, explicitCoordinates: false);
        var reader = new InMemoryZarrRangeReader(objects);
        var extractor = new ZarrMetadataExtractor();

        var metadata = await extractor.ReadMetadataAsync(reader, "bucket", "stores/vertical");

        metadata.Axes.Should().HaveCount(1);
        var axis = metadata.Axes[0];
        axis.Name.Should().Be("elevation");
        axis.Count.Should().Be(4);
        axis.Coordinates.Should().BeNull();
        axis.Start.Should().Be(0);
        axis.End.Should().Be(1000);
        axis.Unit.Should().Be("m");
        axis.Positive.Should().Be("up");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_ExplicitVerticalAxis_CarriesCoordinates()
    {
        var objects = ZarrFixtureBuilder.BuildGroupedWithVerticalAxis(
            root: "stores/vertical-explicit", levels: 4, rows: 4, cols: 4, explicitCoordinates: true);
        var reader = new InMemoryZarrRangeReader(objects);
        var extractor = new ZarrMetadataExtractor();

        var metadata = await extractor.ReadMetadataAsync(reader, "bucket", "stores/vertical-explicit");

        metadata.Axes.Should().HaveCount(1);
        metadata.Axes[0].Coordinates.Should().Equal(0d, 10d, 100d, 1000d);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_NoAxesManifest_YieldsEmptyAxes()
    {
        var objects = ZarrFixtureBuilder.BuildGroupedZlib(
            root: "stores/noaxes", rows: 8, cols: 8, chunkRows: 4, chunkCols: 4,
            sample: (r, c) => r + c, srid: 4326, xMin: -180, yMin: -90, xMax: 180, yMax: 90);
        var reader = new InMemoryZarrRangeReader(objects);
        var extractor = new ZarrMetadataExtractor();

        var metadata = await extractor.ReadMetadataAsync(reader, "bucket", "stores/noaxes");

        metadata.Axes.Should().BeEmpty();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_V2ConsolidatedMetadata_ParsesVariablesWithoutPerVariableReads()
    {
        const string root = "stores/v2-consolidated";
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [root + "/.zmetadata"] = Encoding.UTF8.GetBytes("""
            {
              "zarr_consolidated_format": 1,
              "metadata": {
                ".zgroup": { "zarr_format": 2 },
                ".zattrs": { "crs_wkid": 4326, "extent": [0, 0, 8, 8], "primary_variable": "temp", "x_dimension": "x", "y_dimension": "y", "variables": ["temp", "salinity"] },
                "temp/.zarray": { "zarr_format": 2, "shape": [8, 8], "chunks": [4, 4], "dtype": "<f4", "compressor": null, "fill_value": "NaN", "order": "C", "filters": null },
                "temp/.zattrs": { "_ARRAY_DIMENSIONS": ["y", "x"] },
                "salinity/.zarray": { "zarr_format": 2, "shape": [8, 8], "chunks": [4, 4], "dtype": "<f4", "compressor": null, "fill_value": 0, "order": "C", "filters": null },
                "salinity/.zattrs": { "_ARRAY_DIMENSIONS": ["y", "x"] }
              }
            }
            """)
        };
        var requestedKeys = new List<string>();
        var tracking = CreateTrackingRangeReader(new InMemoryZarrRangeReader(objects), requestedKeys);

        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(tracking, "bucket", root);

        metadata.Arrays.Select(a => a.Name).Should().Equal("temp", "salinity");
        metadata.Arrays[0].DimensionNames.Should().Equal("y", "x");
        metadata.Srid.Should().Be(4326);
        requestedKeys.Should().NotContain(k =>
            k.Contains("/temp/", StringComparison.Ordinal) || k.Contains("/salinity/", StringComparison.Ordinal));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_V3ConsolidatedMetadata_ParsesVariablesWithoutPerVariableReads()
    {
        const string root = "stores/v3-consolidated";
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [root + "/zarr.json"] = Encoding.UTF8.GetBytes("""
            {
              "zarr_format": 3,
              "node_type": "group",
              "attributes": { "crs_wkid": 4326, "extent": [0, 0, 8, 8], "primary_variable": "temp", "variables": ["temp"] },
              "consolidated_metadata": {
                "kind": "inline",
                "must_understand": false,
                "metadata": {
                  "temp": {
                    "zarr_format": 3,
                    "node_type": "array",
                    "shape": [8, 8],
                    "data_type": "float32",
                    "chunk_grid": { "name": "regular", "configuration": { "chunk_shape": [4, 4] } },
                    "chunk_key_encoding": { "name": "default", "configuration": { "separator": "/" } },
                    "fill_value": "NaN",
                    "codecs": [{ "name": "bytes", "configuration": { "endian": "little" } }],
                    "dimension_names": ["y", "x"]
                  }
                }
              }
            }
            """)
        };
        var requestedKeys = new List<string>();
        var tracking = CreateTrackingRangeReader(new InMemoryZarrRangeReader(objects), requestedKeys);

        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(tracking, "bucket", root);

        metadata.ZarrFormat.Should().Be(ZarrFormatVersion.V3);
        metadata.Arrays.Should().ContainSingle().Which.Name.Should().Be("temp");
        requestedKeys.Should().NotContain(k => k.Contains("/temp/zarr.json", StringComparison.Ordinal));
    }

    private static ICloudRangeReader CreateTrackingRangeReader(InMemoryZarrRangeReader inner, List<string> requestedKeys)
    {
        var reader = Substitute.For<ICloudRangeReader>();
        reader.Provider.Returns(inner.Provider);
        reader.ReadRangeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                requestedKeys.Add(call.ArgAt<string>(1));
                return inner.ReadRangeAsync(call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<long>(2), call.ArgAt<int>(3), call.ArgAt<CancellationToken>(4));
            });
        reader.GetObjectSizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                requestedKeys.Add(call.ArgAt<string>(1));
                return inner.GetObjectSizeAsync(call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<CancellationToken>(2));
            });
        return reader;
    }
}
