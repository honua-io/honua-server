// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Burned-pixel proofs for <c>conversion.rasterize</c> (#3938).
///
/// The prior evidence ran the executor against a fake CLI runner and asserted the
/// generated flags, so nothing checked what gdal_rasterize actually burns. These
/// cases run the production executor against the pinned production GDAL image over
/// a committed GeoJSON fixture and decode the emitted GeoTIFF.
///
/// The oracle is gdal_rasterize's documented rule — a pixel is burned when its
/// CENTRE falls inside the polygon — applied by hand to the fixture ordinates.
/// The fixture is deliberately laid out so that on the width/height grid no pixel
/// centre lies on a polygon edge, which makes every expected cell unambiguous
/// rather than dependent on a scanline tie-break.
///
/// Grid derivation for the width+height (<c>-ts</c>) path: the extent is the layer
/// envelope x[0,5] y[0,3], so 5x3 pixels give 1x1 cells with origin (0,3) and
/// centres at x 0.5/1.5/2.5/3.5/4.5 and y 2.5/1.5/0.5.
/// </summary>
public sealed partial class RasterExecutionProofTests
{
    private const double RasterizeNoData = -9999;

    // Fixture ordinates (see rasterize-parcels.geojson):
    //   west  dn=7 covers x[0,3] y[0,3]
    //   east  dn=3 covers x[3,5] y[0,2]
    //   sliver-below-centre dn=9 covers x[0.6,2.4] y[2.6,2.9] — contains NO centre
    //   sliver-over-centre  dn=8 covers x[0.6,2.4] y[2.4,2.9] — contains only (1.5,2.5)
    // Row-major from the top row (y centre 2.5) down.
    private static readonly double[] ExpectedAttributeCells =
    [
        7, 8, 7, RasterizeNoData, RasterizeNoData,
        7, 7, 7, 3, 3,
        7, 7, 7, 3, 3,
    ];

    private static readonly double[] ExpectedBurnCells =
    [
        5, 5, 5, RasterizeNoData, RasterizeNoData,
        5, 5, 5, 5, 5,
        5, 5, 5, 5, 5,
    ];

    [Fact]
    public async Task Rasterize_AttributeMode_BurnsPerFeatureValuesAtPixelCentres()
    {
        var output = await ExecuteRaster(
            "conversion.rasterize",
            ("source", Input("rasterize-parcels.geojson")),
            ("attribute", "dn"),
            ("width", "5"),
            ("height", "3"),
            ("nodata", RasterizeNoData.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

        AssertGrid(output, 5, 3, 4326, [0, 1, 0, 3, 0, -1], 1);
        AssertBand(output, 0, ExpectedAttributeCells, type: "Float64", nodata: RasterizeNoData);
    }

    [Fact]
    public async Task Rasterize_BurnValueMode_BurnsTheFixedValueOverEveryCoveredCentre()
    {
        var output = await ExecuteRaster(
            "conversion.rasterize",
            ("source", Input("rasterize-parcels.geojson")),
            ("burnValue", "5"),
            ("width", "5"),
            ("height", "3"),
            ("nodata", RasterizeNoData.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

        AssertGrid(output, 5, 3, 4326, [0, 1, 0, 3, 0, -1], 1);
        // Same covered centres as the attribute run, but one fixed value: an
        // implementation that leaked the 'dn' attribute through would fail here.
        AssertBand(output, 0, ExpectedBurnCells, type: "Float64", nodata: RasterizeNoData);
    }

    [Fact]
    public async Task Rasterize_SubPixelEdges_DecideBurnByCentreContainment()
    {
        var output = await ExecuteRaster(
            "conversion.rasterize",
            ("source", Input("rasterize-parcels.geojson")),
            ("attribute", "dn"),
            ("width", "5"),
            ("height", "3"),
            ("nodata", RasterizeNoData.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

        var cells = output.GetProperty("bands")[0].GetProperty("values")
            .EnumerateArray().Select(value => value.GetDouble()).ToArray();

        // 'sliver-over-centre' spans y[2.4,2.9] and so contains the centre (1.5,2.5):
        // its dn=8 replaces west's 7 in exactly that one cell.
        cells[1].Should().Be(8, "the sliver crossing the centre must win that pixel");

        // 'sliver-below-centre' spans y[2.6,2.9] — 0.1 above the same centre — and
        // must burn nothing at all, so dn=9 appears nowhere in the raster.
        cells.Should().NotContain(9, "a polygon that contains no pixel centre burns nothing");

        // Both slivers span x[0.6,2.4], which excludes the centres at 0.5 and 2.5:
        // those cells keep west's value rather than the sliver's.
        cells[0].Should().Be(7, "centre 0.5 is 0.1 outside the sliver's left edge");
        cells[2].Should().Be(7, "centre 2.5 is 0.1 outside the sliver's right edge");
    }

    [Fact]
    public async Task Rasterize_CellSizeMode_CentresTheGridOnTheLayerEnvelope()
    {
        var output = await ExecuteRaster(
            "conversion.rasterize",
            ("source", Input("rasterize-parcels.geojson")),
            ("burnValue", "5"),
            ("cellSize", "1"));

        // The -tr path places pixel CENTRES on the envelope bounds, so the 5x3
        // envelope yields 6x4 cells with the origin half a cell outside it.
        AssertGrid(output, 6, 4, 4326, [-0.5, 1, 0, 3.5, 0, -1], 1);

        var band = output.GetProperty("bands")[0];
        band.GetProperty("type").GetString().Should().Be("Float64");
        // Without an explicit nodata the tool leaves the untouched background at 0
        // and reports 0 as the nodata value, so background cells read as invalid.
        band.GetProperty("nodata").GetDouble().Should().Be(0);

        var cells = band.GetProperty("values").EnumerateArray().Select(value => value.GetDouble()).ToArray();
        cells.Should().HaveCount(24);
        cells.Should().OnlyContain(value => value == 0 || value == 5,
            "burnValue mode may only write the fixed value or leave the background");

        // Centres, row-major from the top row: x 0..5, y 3..0. Assert the cells
        // that are strictly inside or strictly outside every polygon; centres that
        // land exactly on an edge are left to the tool's scanline tie-break.
        Cell(cells, column: 1, row: 1).Should().Be(5, "(1,2) is strictly inside west");
        Cell(cells, column: 2, row: 1).Should().Be(5, "(2,2) is strictly inside west");
        Cell(cells, column: 1, row: 2).Should().Be(5, "(1,1) is strictly inside west");
        Cell(cells, column: 2, row: 2).Should().Be(5, "(2,1) is strictly inside west");
        Cell(cells, column: 4, row: 2).Should().Be(5, "(4,1) is strictly inside east");
        Cell(cells, column: 4, row: 0).Should().Be(0, "(4,3) is outside west and above east");
        Cell(cells, column: 5, row: 0).Should().Be(0, "(5,3) is outside west and above east");
    }

    private static double Cell(double[] cells, int column, int row) => cells[(row * 6) + column];
}
