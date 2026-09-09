// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Xunit;
using Xunit.Sdk;

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
    //   sliver-over-centre  dn=8 covers x[0.6,2.4] y[2.4,2.9] — contains only (1.5,2.5)
    //   sliver-below-centre dn=9 covers x[0.6,2.4] y[2.6,2.9] — contains NO centre
    // The no-centre sliver is LAST in the fixture on purpose: gdal_rasterize burns in
    // feature order, so it is the last writer over the cells it would touch. An
    // implementation that wrongly burned it therefore leaves dn=9 visible instead of
    // having it overwritten by the sliver drawn after it.
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
        // must burn nothing at all. It is the fixture's LAST feature, so a wrong burn
        // would survive as dn=9 rather than being overwritten; its absence is therefore
        // real evidence, not an artefact of draw order.
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
        // Without an explicit nodata the tool leaves the untouched background at 0 AND
        // declares 0 as the band's nodata value, so background cells read as invalid.
        // Verified against the pinned digest: `gdal_rasterize -of GTiff -burn 5 -tr 1 1`
        // emits a band whose nodata metadata is 0, not absent.
        band.GetProperty("nodata").ValueKind.Should().Be(JsonValueKind.Number,
            "the cellSize path still declares a nodata sentinel");
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

    [Fact]
    public async Task RasterizeOracle_AttributeBurnSubstitutedForTheFixedBurnValue_IsRejected()
    {
        // A plausible wrong-but-well-formed rasterize result: the same extent, grid,
        // CRS, band type and nodata, produced by real gdal_rasterize — but burning
        // the per-feature attribute where the caller asked for one fixed value.
        var wrong = await ExecuteRaster(
            "conversion.rasterize",
            ("source", Input("rasterize-parcels.geojson")),
            ("attribute", "dn"),
            ("width", "5"),
            ("height", "3"),
            ("nodata", RasterizeNoData.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

        AssertGrid(wrong, 5, 3, 4326, [0, 1, 0, 3, 0, -1], 1);

        Action assert = () => AssertBand(wrong, 0, ExpectedBurnCells, type: "Float64", nodata: RasterizeNoData);
        assert.Should().Throw<XunitException>("the burn-value oracle must reject an attribute burn")
            .Which.Message.Should().Contain("cell 0");
    }

    [Fact]
    public async Task RasterizeOracle_AllTouchedInsteadOfCentreContainment_IsRejected()
    {
        // The other plausible substitution: burning every pixel a polygon TOUCHES
        // instead of every pixel whose centre it contains. Real gdal_rasterize -at
        // produces it, so the output is a valid GeoTIFF on the identical grid with
        // the identical nodata sentinel — only the burned set is wrong.
        Directory.CreateDirectory(_scratch);
        File.Copy(Fixture("rasterize-parcels.geojson"), Path.Join(_scratch, "parcels.geojson"), overwrite: true);
        await Run("gdal_rasterize", [
            "-of", "GTiff", "-a_nodata", "-9999", "-a", "dn", "-at", "-ts", "5", "3",
            "parcels.geojson", "all-touched.tif"]);
        var wrong = await Decode(await File.ReadAllBytesAsync(Path.Join(_scratch, "all-touched.tif")));

        AssertGrid(wrong, 5, 3, 4326, [0, 1, 0, 3, 0, -1], 1);
        var cells = wrong.GetProperty("bands")[0].GetProperty("values")
            .EnumerateArray().Select(value => value.GetDouble()).ToArray();
        // All-touched burns 'sliver-below-centre', which contains no pixel centre at
        // all, and it is the fixture's last feature, so its dn = 9 claims the whole top
        // row. Centre containment never writes 9 anywhere.
        cells[0].Should().Be(9, "all-touched burns a polygon that contains no pixel centre");
        cells[1].Should().Be(9, "all-touched burns a polygon that contains no pixel centre");
        cells[2].Should().Be(9, "all-touched burns a polygon that contains no pixel centre");

        Action assert = () => AssertBand(wrong, 0, ExpectedAttributeCells, type: "Float64", nodata: RasterizeNoData);
        assert.Should().Throw<XunitException>("the centre-containment oracle must reject an all-touched burn");
    }

    private static double Cell(double[] cells, int column, int row) => cells[(row * 6) + column];
}
