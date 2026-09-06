// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.Worker.Gdal.Tests;

public sealed partial class RasterExecutionProofTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Rasterize_BurnAndAttribute_MatchInsideOutsideAndBoundaryCellOracle(bool attribute, bool centerInside)
    {
        var inputs = new List<(string, string)>
        {
            ("source", Input(centerInside ? "rasterize.geojson" : "rasterize-outside.geojson")),
            ("width", "4"), ("height", "4"), ("nodata", "-9999"),
            attribute ? ("attribute", "class") : ("burnValue", "7")
        };
        var output = await ExecuteRaster("conversion.rasterize", inputs.ToArray());
        AssertGrid(output, 4, 4, 4326, [0, 1, 0, 4, 0, -1], 1);
        // L: x<1 or y<1. The rectangle's left boundary is x=2.25 or x=2.75.
        // Both cut column-2 pixels, whose x=2.5 centers are respectively inside
        // and outside. Freeze BOTH boundary cases with independent center-rule
        // expectations. GDAL explicitly leaves exact center-on-edge ties
        // unspecified: https://gdal.org/en/stable/programs/gdal_rasterize.html
        var left = attribute ? 11.0 : 7.0;
        var right = attribute ? 29.0 : 7.0;
        var boundary = centerInside ? right : NoData;
        AssertBand(output, 0,
        [
            left, NoData, boundary, right,
            left, NoData, boundary, right,
            left, NoData, NoData, NoData,
            left, left, left, left
        ], type: "Float64");
    }
}
