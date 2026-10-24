// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Raster;

public sealed class RasterImportRequestOverviewTests
{
    [UnitTest]
    public void DefaultRequest_DoesNotAskForOverviewPyramids()
    {
        // Production import paths (HTTP, geoprocessing, OGC coverage) omit OverviewFactors.
        // The default must stay empty: tile reads do not consume persisted overviews (#5856).
        var request = new RasterImportRequest
        {
            LayerId = 1,
            Name = "incoming",
            FilePath = "/tmp/incoming.tif",
            FileName = "incoming.tif",
            Format = SupportedRasterFormat.GeoTiff,
        };

        request.OverviewFactors.Should().BeEmpty();
    }
}
