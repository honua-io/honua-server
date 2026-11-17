// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.TestKit.Formats;
using NSubstitute;

namespace Honua.Core.Tests.Raster.ZarrParser;

// A CF datacube exactly as GDAL 3.14's Zarr driver writes the bounded-roster fixture
// (FORMAT=ZARR_V2, consolidated): no root attributes, coordinate variables with CF units, a
// south-first latitude axis and the CRS in GDAL's _CRS attribute (trimmed here to its url).
public sealed class ZarrCfGeoreferencingTests
{
    private const string Root = "zarr/roster-sst.zarr";

    private const string GdalConsolidatedMetadata = """
        {".zgroup":{"zarr_format":2},
         "time/.zarray":{"chunks":[2],"compressor":null,"dtype":"<f8","fill_value":null,"filters":null,"order":"C","shape":[2],"zarr_format":2},
         "latitude/.zarray":{"chunks":[4],"compressor":null,"dtype":"<f8","fill_value":null,"filters":null,"order":"C","shape":[4],"zarr_format":2},
         "longitude/.zarray":{"chunks":[4],"compressor":null,"dtype":"<f8","fill_value":null,"filters":null,"order":"C","shape":[4],"zarr_format":2},
         "latitude/.zattrs":{"standard_name":"latitude","units":"degrees_north","_ARRAY_DIMENSIONS":["latitude"]},
         "longitude/.zattrs":{"standard_name":"longitude","units":"degrees_east","_ARRAY_DIMENSIONS":["longitude"]},
         "sea_surface_temperature/.zarray":{"chunks":[1,4,4],"compressor":null,"dtype":"<f4","fill_value":-9999.0,"filters":null,"order":"C","shape":[2,4,4],"zarr_format":2},
         "sea_surface_temperature/.zattrs":{"_CRS":{"url":"http://www.opengis.net/def/crs/EPSG/0/4326"},"units":"degC","_ARRAY_DIMENSIONS":["time","latitude","longitude"]},
         "time/.zattrs":{"standard_name":"time","units":"hours since 2024-01-01 00:00:00","_ARRAY_DIMENSIONS":["time"]}}
        """;

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_GdalCfDatacube_GeoreferencesFromCoordinateVariables()
    {
        var metadata = await ReadAsync(Store(latitudes: [37.70, 37.75, 37.80, 37.85]));

        metadata.Srid.Should().Be(4326);
        metadata.PrimaryVariable.Should().Be("sea_surface_temperature");
        metadata.SpatialXDimension.Should().Be("longitude");
        metadata.SpatialYDimension.Should().Be("latitude");
        metadata.YAxisAscending.Should().BeTrue("GDAL wrote latitude south-first");
        // Coordinates are cell centres 0.05 degrees apart; the extent is the outer cell edges.
        metadata.Extent.XMin.Should().BeApproximately(-122.525, 1e-9);
        metadata.Extent.XMax.Should().BeApproximately(-122.325, 1e-9);
        metadata.Extent.YMin.Should().BeApproximately(37.675, 1e-9);
        metadata.Extent.YMax.Should().BeApproximately(37.875, 1e-9);
        metadata.Extent.Srid.Should().Be(4326);
        metadata.TemporalDimension.Should().Be("time");
        metadata.Temporal!.Start.Should().Be(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        metadata.Temporal.End.Should().Be(new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero));
        metadata.Temporal.StepCount.Should().Be(2);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_NorthFirstWithoutCrsAttribute_UsesDegreeUnits()
    {
        var objects = Store(latitudes: [37.85, 37.80, 37.75, 37.70]);
        Edit(objects, "sea_surface_temperature/.zattrs", attributes => attributes.Remove("_CRS"));

        var metadata = await ReadAsync(objects);

        metadata.Srid.Should().Be(4326);
        metadata.YAxisAscending.Should().BeFalse();
        metadata.Extent.YMax.Should().BeApproximately(37.875, 1e-9);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_HonuaRootAttributes_StillTakePrecedence()
    {
        var objects = Store(latitudes: [37.70, 37.75, 37.80, 37.85]);
        Put(objects, ".zattrs", """{"crs_wkid":3857,"extent":[0,0,4,4]}""");

        var metadata = await ReadAsync(objects);

        metadata.Srid.Should().Be(3857);
        metadata.Extent.XMax.Should().Be(4);
        metadata.YAxisAscending.Should().BeFalse();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_UndecodableCoordinateCodec_RegistersUngeoreferencedAsBefore()
    {
        var objects = Store(latitudes: [37.70, 37.75, 37.80, 37.85]);
        Edit(objects, "longitude/.zarray", array => array["compressor"] = new JsonObject { ["id"] = "blosc" });

        var metadata = await ReadAsync(objects);

        metadata.Srid.Should().Be(0);
        metadata.Arrays.Should().Contain(array => array.Name == "sea_surface_temperature");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_IrregularLatitude_IsNotTreatedAsARegularGrid()
    {
        var metadata = await ReadAsync(Store(latitudes: [37.70, 37.75, 37.81, 37.85]));

        metadata.Srid.Should().Be(0);
        metadata.PrimaryVariable.Should().Be("sea_surface_temperature");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_BoundsVariableListedFirst_PicksTheGriddedVariable()
    {
        var objects = Store(latitudes: [37.70, 37.75, 37.80, 37.85]);
        var consolidated = JsonNode.Parse(objects[$"{Root}/.zmetadata"])!["metadata"]!.AsObject();
        var reordered = new JsonObject
        {
            ["lat_bnds/.zarray"] = JsonNode.Parse("""{"chunks":[4,2],"compressor":null,"dtype":"<f8","fill_value":null,"filters":null,"order":"C","shape":[4,2],"zarr_format":2}"""),
            ["lat_bnds/.zattrs"] = JsonNode.Parse("""{"_ARRAY_DIMENSIONS":["latitude","bnds"]}"""),
        };
        foreach (var (key, value) in consolidated)
        {
            reordered[key] = value!.DeepClone();
        }
        WriteStore(objects, reordered);

        var metadata = await ReadAsync(objects);

        metadata.PrimaryVariable.Should().Be("sea_surface_temperature");
        metadata.Srid.Should().Be(4326);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(37.70, 100f)] // South-west cell: storage row 0 of a south-first grid.
    [InlineData(37.85, 130f)] // North-west cell: storage row 3.
    public async Task ReadPoint_SouthFirstGrid_ReadsTheRowAtThatLatitude(double latitude, float expected)
    {
        var objects = Store(latitudes: [37.70, 37.75, 37.80, 37.85]);
        objects[$"{Root}/sea_surface_temperature/0.0.0"] = Enumerable.Range(0, 16)
            .SelectMany(cell => BitConverter.GetBytes(100f + (cell / 4 * 10) + (cell % 4))).ToArray();
        var registration = new ZarrRegistration
        {
            Id = 1, LayerId = 5200, Name = "roster zarr", Provider = CloudStorageProvider.AwsS3,
            Bucket = "roster-fixtures", RootPath = Root, CreatedAt = DateTimeOffset.UnixEpoch,
            Metadata = await ReadAsync(objects),
        };
        var store = Substitute.For<IZarrStore>();
        store.ListByLayerAsync(5200, Arg.Any<CancellationToken>()).Returns(Task.FromResult(new[] { registration }));
        var reader = new ZarrPointSliceReader(store, new ZarrSubsetReader(), [new InMemoryZarrRangeReader(objects)]);
        var firstHour = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var result = await reader.ReadAsync(5200, -122.50, latitude, 4326,
            [new ZarrPointSliceSelection("sea_surface_temperature", "time", firstHour)]);

        result.Status.Should().Be(ZarrPointSliceReadStatus.Success, result.Error);
        result.Value.Should().Be(expected);
    }

    private static Task<ZarrStoreMetadata> ReadAsync(Dictionary<string, byte[]> objects)
        => new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "roster-fixtures", Root);

    // Every metadata document is also written as its own object, as GDAL does, plus the chunks.
    private static Dictionary<string, byte[]> Store(double[] latitudes)
    {
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        WriteStore(objects, JsonNode.Parse(GdalConsolidatedMetadata)!.AsObject());
        objects[$"{Root}/time/0"] = Float64([0, 24]);
        objects[$"{Root}/latitude/0"] = Float64(latitudes);
        objects[$"{Root}/longitude/0"] = Float64([-122.50, -122.45, -122.40, -122.35]);
        objects[$"{Root}/sea_surface_temperature/0.0.0"] = new byte[64];
        objects[$"{Root}/sea_surface_temperature/1.0.0"] = new byte[64];
        return objects;
    }

    private static void WriteStore(Dictionary<string, byte[]> objects, JsonObject entries)
    {
        foreach (var (key, value) in entries)
        {
            Put(objects, key, value!.ToJsonString());
        }

        Put(objects, ".zmetadata", new JsonObject
        {
            ["zarr_consolidated_format"] = 1,
            ["metadata"] = entries.DeepClone(),
        }.ToJsonString());
    }

    private static void Edit(Dictionary<string, byte[]> objects, string key, Action<JsonObject> edit)
    {
        var entries = JsonNode.Parse(objects[$"{Root}/.zmetadata"])!["metadata"]!.AsObject();
        edit(entries[key]!.AsObject());
        WriteStore(objects, entries.DeepClone().AsObject());
    }

    private static void Put(Dictionary<string, byte[]> objects, string key, string json)
        => objects[$"{Root}/{key}"] = Encoding.UTF8.GetBytes(json);

    private static byte[] Float64(double[] values)
        => values.SelectMany(BitConverter.GetBytes).ToArray();
}
