// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.Worker.Gdal.Tests;

public sealed partial class RasterExecutionProofTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rasterize_BurnAndAttribute_MatchInsideOutsideAndBoundaryCellOracle(bool attribute)
    {
        var inputs = new List<(string, string)>
        {
            ("source", Input("rasterize.geojson")), ("width", "4"), ("height", "4"), ("nodata", "-9999"),
            attribute ? ("attribute", "class") : ("burnValue", "7")
        };
        var output = await ExecuteRaster("conversion.rasterize", inputs.ToArray());
        AssertGrid(output, 4, 4, 4326, [0, 1, 0, 4, 0, -1], 1);
        // L: x<1 or y<1. Upper-right rectangle: 2.5<=x<=4, 2<=y<=4.
        // Centers in column 2 lie exactly on the rectangle's left boundary.
        // Its boundary cells must burn, while the gap in column 1 stays nodata.
        var left = attribute ? 11.0 : 7.0;
        var right = attribute ? 29.0 : 7.0;
        AssertBand(output, 0,
        [
            left, NoData, right, right,
            left, NoData, right, right,
            left, NoData, NoData, NoData,
            left, left, left, left
        ], type: "Float64");
    }
}
