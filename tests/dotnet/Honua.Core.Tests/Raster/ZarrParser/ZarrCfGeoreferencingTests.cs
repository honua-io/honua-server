// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.TestKit.Formats;
using Xunit;

namespace Honua.Core.Tests.Raster.ZarrParser;

// A CF Zarr v2 store laid out as GDAL's multidimensional Zarr driver writes it
// (see certification/bounded-roster/fixture/apply_fixture.py author_zarr).
public class ZarrCfGeoreferencingTests
{
    private const string Root = "zarr/roster-sst.zarr";
    private const string Crs4326 = "\"_CRS\":{\"url\":\"http://www.opengis.net/def/crs/EPSG/0/4326\"}";
    private const string LatitudeAttrs = "\"standard_name\":\"latitude\",\"units\":\"degrees_north\"";
    private const string LongitudeAttrs = "\"standard_name\":\"longitude\",\"units\":\"degrees_east\"";

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_CfLatitudeLongitudeStore_ReportsGeographicCellEdgeExtent()
    {
        var metadata = await ReadAsync(BuildStore(rootAttrs: "{}", latitude: [37.70, 37.75, 37.80, 37.85]));

        metadata.PrimaryVariable.Should().Be("sea_surface_temperature");
        metadata.Srid.Should().Be(4326);
        metadata.SpatialXDimension.Should().Be("longitude");
        metadata.SpatialYDimension.Should().Be("latitude");
        metadata.YAxisAscending.Should().BeTrue();
        metadata.Extent.XMin.Should().BeApproximately(-122.525, 1e-9);
        metadata.Extent.XMax.Should().BeApproximately(-122.325, 1e-9);
        metadata.Extent.YMin.Should().BeApproximately(37.675, 1e-9);
        metadata.Extent.YMax.Should().BeApproximately(37.875, 1e-9);
        metadata.Extent.Srid.Should().Be(4326);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_CfNorthFirstLatitude_ReportsDescendingRows()
    {
        var metadata = await ReadAsync(BuildStore(rootAttrs: "{}", latitude: [37.85, 37.80, 37.75, 37.70]));

        metadata.YAxisAscending.Should().BeFalse();
        metadata.Extent.YMin.Should().BeApproximately(37.675, 1e-9);
        metadata.Extent.YMax.Should().BeApproximately(37.875, 1e-9);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_RootManifestDeclaresGeoreferencing_KeepsManifest()
    {
        const string manifest = "{\"crs\":\"EPSG:3857\",\"extent\":[0,0,400,400],\"x_dimension\":\"longitude\",\"y_dimension\":\"latitude\"}";

        var metadata = await ReadAsync(BuildStore(rootAttrs: manifest, latitude: [37.70, 37.75, 37.80, 37.85]));

        metadata.Srid.Should().Be(3857);
        metadata.Extent.XMax.Should().Be(400);
        metadata.YAxisAscending.Should().BeFalse();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_IrregularLatitude_LeavesStoreUngeoreferenced()
    {
        var metadata = await ReadAsync(BuildStore(rootAttrs: "{}", latitude: [37.70, 37.71, 37.80, 37.85]));

        metadata.Srid.Should().Be(0);
        metadata.SpatialYDimension.Should().BeNull();
        metadata.Extent.XMax.Should().Be(4, "an ungeoreferenced store keeps the legacy grid-shape extent of its data array");
        metadata.Extent.YMax.Should().Be(4);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_CoordinatesWithoutCfAttributes_LeavesStoreUngeoreferenced()
    {
        var objects = BuildStore(rootAttrs: "{}", latitude: [37.70, 37.75, 37.80, 37.85]);
        objects[$"{Root}/latitude/.zattrs"] = Encoding.UTF8.GetBytes("{\"_ARRAY_DIMENSIONS\":[\"latitude\"]}");

        var metadata = await ReadAsync(objects);

        metadata.Srid.Should().Be(0);
        metadata.SpatialXDimension.Should().BeNull();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void TryResolveCellEdges_Float32RoundedHundredthDegreeAxis_IsRegular()
    {
        // A float32 latitude at 0.01 degree near 50 N carries rounding near 4e-6 per sample.
        var latitude = Enumerable.Range(0, 100).Select(index => (double)(float)(50.0 + (index * 0.01))).ToArray();

        var edges = ZarrCfGeoreferencing.TryResolveCellEdges(latitude);

        edges.Should().NotBeNull();
        edges!.Value.Ascending.Should().BeTrue();
        edges.Value.Low.Should().BeApproximately(49.995, 1e-4);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void TryResolveCellEdges_SingleOrConstantAxis_IsRefused()
    {
        ZarrCfGeoreferencing.TryResolveCellEdges([1.0]).Should().BeNull();
        ZarrCfGeoreferencing.TryResolveCellEdges([1.0, 1.0, 1.0]).Should().BeNull();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadMetadataAsync_OversizeCoordinate_IsNotRead()
    {
        var objects = BuildStore(rootAttrs: "{}", latitude: [37.70, 37.75, 37.80, 37.85]);
        var length = ZarrCfGeoreferencing.MaxCoordinateLength + 1;
        objects[$"{Root}/longitude/.zarray"] = ZArray(length, 1000, "<f8");
        objects[$"{Root}/sea_surface_temperature/.zarray"] = Encoding.UTF8.GetBytes(
            "{\"zarr_format\":2,\"shape\":[2,4," + length + "],\"chunks\":[2,4,4],\"dtype\":\"<f4\",\"compressor\":null," +
            "\"fill_value\":-9999.0,\"order\":\"C\",\"filters\":null}");

        var metadata = await ReadAsync(objects);

        metadata.Srid.Should().Be(0, "a coordinate above the length cap is not downloaded");
    }

    private static Task<ZarrStoreMetadata> ReadAsync(Dictionary<string, byte[]> objects)
        => new ZarrMetadataExtractor().ReadMetadataAsync(new InMemoryZarrRangeReader(objects), "bucket", Root);

    private static Dictionary<string, byte[]> BuildStore(string rootAttrs, double[] latitude)
    {
        var objects = new Dictionary<string, byte[]>
        {
            [$"{Root}/.zgroup"] = Encoding.UTF8.GetBytes("{\"zarr_format\":2}"),
            [$"{Root}/.zattrs"] = Encoding.UTF8.GetBytes(rootAttrs),
            [$"{Root}/.zmetadata"] = Encoding.UTF8.GetBytes(
                "{\"zarr_consolidated_format\":1,\"metadata\":{\"latitude/.zarray\":{},\"longitude/.zarray\":{}," +
                "\"sea_surface_temperature/.zarray\":{},\"time/.zarray\":{}}}"),
        };
        AddCoordinate(objects, ("latitude", LatitudeAttrs), latitude);
        AddCoordinate(objects, ("longitude", LongitudeAttrs), [-122.50, -122.45, -122.40, -122.35]);
        AddCoordinate(objects, ("time", "\"standard_name\":\"time\",\"units\":\"hours since 2024-01-01 00:00:00\""), [0.0, 24.0]);
        objects[$"{Root}/sea_surface_temperature/.zarray"] = Encoding.UTF8.GetBytes(
            "{\"zarr_format\":2,\"shape\":[2,4,4],\"chunks\":[2,4,4],\"dtype\":\"<f4\",\"compressor\":null," +
            "\"fill_value\":-9999.0,\"order\":\"C\",\"filters\":null}");
        objects[$"{Root}/sea_surface_temperature/.zattrs"] = Encoding.UTF8.GetBytes(
            "{\"_ARRAY_DIMENSIONS\":[\"time\",\"latitude\",\"longitude\"],\"units\":\"degC\"," + Crs4326 + "}");
        objects[$"{Root}/sea_surface_temperature/0.0.0"] = Enumerable.Range(0, 32)
            .SelectMany(index => System.BitConverter.GetBytes((float)index)).ToArray();
        return objects;
    }

    private static void AddCoordinate(Dictionary<string, byte[]> objects, (string Name, string Attrs) coordinate, double[] values)
    {
        var (name, attrs) = coordinate;
        objects[$"{Root}/{name}/.zarray"] = ZArray(values.Length, values.Length, "<f8");
        objects[$"{Root}/{name}/.zattrs"] = Encoding.UTF8.GetBytes(
            "{\"_ARRAY_DIMENSIONS\":[\"" + name + "\"]," + attrs + "}");
        objects[$"{Root}/{name}/0"] = values.SelectMany(System.BitConverter.GetBytes).ToArray();
    }

    private static byte[] ZArray(int length, int chunk, string dtype)
        => Encoding.UTF8.GetBytes(
            "{\"zarr_format\":2,\"shape\":[" + length + "],\"chunks\":[" + chunk + "],\"dtype\":\"" + dtype +
            "\",\"compressor\":null,\"fill_value\":null,\"order\":\"C\",\"filters\":null}");
}
