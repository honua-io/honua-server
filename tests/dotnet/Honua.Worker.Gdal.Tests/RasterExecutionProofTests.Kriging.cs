// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

public sealed partial class RasterExecutionProofTests
{
    [Fact]
    public async Task Kriging_LinearVariogram_MatchesClosedFormGridAndWithheldPointErrors()
    {
        var output = await ExecuteRaster("raster.interpolate-kriging", ("points", Input("kriging.geojson")),
            ("zField", "elevation"), ("variogram", "linear"), ("width", "4"), ("height", "4"));
        AssertGrid(output, 4, 4, 4326, [0, 1, 0, 4, 0, -1], 1);
        output.GetProperty("metadata").GetProperty("HONUA_KRIGING_MODEL").GetString()
            .Should().Be("ordinary-linear-zero-nugget-v1");
        // Independent two-point algebra, not the production matrix solver:
        // w0+w1=1 and D*w1+mu=d0, D*w0+mu=d1 => w1=(1+(d0-d1)/D)/2.
        // gamma(h)=h; Z0=0, Z1=8. Every off-diagonal prediction distinguishes
        // this ordinary-kriging model from IDW and planar linear interpolation.
        var expected = new double[16];
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                var x = column + 0.5;
                var y = 3.5 - row;
                var d0 = Math.Sqrt(x * x + y * y);
                var d1 = Math.Sqrt((4 - x) * (4 - x) + (4 - y) * (4 - y));
                expected[row * 4 + column] = 4 * (1 + (d0 - d1) / Math.Sqrt(32));
            }
        }
        AssertBand(output, 0, expected, "Float64", double.NaN, 1e-10);
        // Withheld locations on the diagonal have independently known truth z=x+y.
        // None of these four points are supplied to the solver.
        var values = output.GetProperty("bands")[0].GetProperty("values");
        double squaredError = 0;
        for (var i = 0; i < 4; i++)
        {
            var residual = values[(3 - i) * 4 + i].GetDouble() - (2 * i + 1);
            Math.Abs(residual).Should().BeLessThan(1e-10);
            squaredError += residual * residual;
        }
        Math.Sqrt(squaredError / 4).Should().BeLessThan(1e-10, "withheld-point RMSE must meet the frozen tolerance");
    }
}
