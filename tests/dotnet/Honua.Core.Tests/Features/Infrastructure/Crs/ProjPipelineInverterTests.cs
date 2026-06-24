// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Crs;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Infrastructure.Crs;

/// <summary>
/// Unit tests for <see cref="ProjPipelineInverter"/>. A reverse-direction datum
/// transformation must apply the geodetic shift the opposite way; emitting the forward
/// pipeline corrupts coordinates (~2x the intended NADCON correction, #2069).
/// </summary>
public sealed class ProjPipelineInverterTests
{
    [UnitTest]
    public void Orient_Forward_ReturnsPipelineUnchanged()
    {
        const string pipeline = "+proj=pipeline +step +proj=hgridshift +grids=us_noaa_conus.tif";

        ProjPipelineInverter.Orient(pipeline, forward: true).Should().Be(pipeline);
    }

    [UnitTest]
    public void Invert_MultiStepPipeline_ReversesStepsAndTogglesInv()
    {
        const string forward =
            "+proj=pipeline +step +proj=unitconvert +xy_in=deg +xy_out=rad " +
            "+step +proj=hgridshift +grids=us_noaa_conus.tif " +
            "+step +proj=unitconvert +xy_in=rad +xy_out=deg";

        var inverted = ProjPipelineInverter.Invert(forward);

        inverted.Should().Be(
            "+proj=pipeline " +
            "+step +proj=unitconvert +xy_in=rad +xy_out=deg +inv " +
            "+step +proj=hgridshift +grids=us_noaa_conus.tif +inv " +
            "+step +proj=unitconvert +xy_in=deg +xy_out=rad +inv");
    }

    [UnitTest]
    public void Invert_StepAlreadyInverted_RemovesInv()
    {
        const string pipeline = "+proj=pipeline +step +proj=hgridshift +grids=g.tif +inv";

        ProjPipelineInverter.Invert(pipeline)
            .Should().Be("+proj=pipeline +step +proj=hgridshift +grids=g.tif");
    }

    [UnitTest]
    public void Invert_DoubleInversion_RoundTripsToOriginal()
    {
        const string forward =
            "+proj=pipeline +step +proj=unitconvert +xy_in=deg +xy_out=rad " +
            "+step +proj=hgridshift +grids=us_noaa_conus.tif " +
            "+step +proj=unitconvert +xy_in=rad +xy_out=deg";

        ProjPipelineInverter.Invert(ProjPipelineInverter.Invert(forward)).Should().Be(forward);
    }

    [UnitTest]
    public void Invert_Noop_IsSelfInverse()
    {
        ProjPipelineInverter.Invert("+proj=noop").Should().Be("+proj=noop");
    }
}
