// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Xunit;

namespace Honua.Core.Tests.Raster.ZarrParser;

public class ZarrMetadataExtractorTests
{
    [Fact]
    public async Task ReadMetadataAsync_SingleArrayUncompressed_DiscoversShapeAndDtype()
    {
        var objects = ZarrFixtureBuilder.BuildSingleVariableUncompressed(
            root: "stores/example",
            rows: 8,
            cols: 8,
            chunkRows: 4,
            chunkCols: 4,
            sample: (r, c) => r * 10 + c);
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

    [Fact]
    public async Task ReadMetadataAsync_InvalidJson_ThrowsInvalidData()
    {
        var objects = ZarrFixtureBuilder.BuildInvalidJson("stores/broken");
        var reader = new InMemoryZarrRangeReader(objects);
        var extractor = new ZarrMetadataExtractor();

        var act = () => extractor.ReadMetadataAsync(reader, "bucket", "stores/broken");

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReadMetadataAsync_UnsupportedV3_Rejected()
    {
        var objects = ZarrFixtureBuilder.BuildUnsupportedV3("stores/v3");
        var reader = new InMemoryZarrRangeReader(objects);
        var extractor = new ZarrMetadataExtractor();

        var act = () => extractor.ReadMetadataAsync(reader, "bucket", "stores/v3");

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*v3*");
    }

    [Fact]
    public async Task ReadMetadataAsync_FortranOrder_Rejected()
    {
        var objects = ZarrFixtureBuilder.BuildFortranOrder("stores/fortran");
        var reader = new InMemoryZarrRangeReader(objects);
        var extractor = new ZarrMetadataExtractor();

        var act = () => extractor.ReadMetadataAsync(reader, "bucket", "stores/fortran");

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*Fortran*");
    }
}
