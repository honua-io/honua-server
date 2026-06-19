// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Multidimensional.Domain;
using Honua.Core.Features.Raster.Multidimensional.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Raster.Multidimensional;

/// <summary>
/// Verifies the gdalinfo enrichment pass (ADR-0039 Path B) that supplies the
/// spatial extent, resolution, and temporal bounds gdalmdiminfo cannot. The
/// golden document mirrors <c>gdalinfo -json maui_sst.nc</c> (OSGeo gdal
/// ubuntu-full) over the CF-1.8 NetCDF4 cube.
/// </summary>
public sealed class GdalInfoCoverageEnricherTests
{
    private const string GdalInfoJson =
        """
        {
          "size": [5, 4],
          "geoTransform": [-156.55, 0.1, 0.0, 20.85, 0.0, -0.1],
          "cornerCoordinates": {
            "upperLeft": [-156.55, 20.85],
            "lowerRight": [-156.05, 20.45]
          },
          "metadata": {
            "": {
              "NETCDF_DIM_EXTRA": "{time}",
              "time#standard_name": "time",
              "time#units": "hours since 2026-01-01 00:00:00",
              "NETCDF_DIM_time_VALUES": "{0,6,12}"
            }
          }
        }
        """;

    private static MultidimensionalCoverageMetadata BaseMetadata() => new()
    {
        Format = MultidimensionalCoverageFormat.NetCdf4,
        Srid = 4326,
        Extent = null,
        Resolution = (0d, 0d),
        Temporal = null,
        Vertical = null,
        Variables = new[]
        {
            new MultidimensionalCoverageVariable("sst", "Float32", Array.Empty<MultidimensionalCoverageDimension>(), null, "degC", null, null, null),
        },
    };

    [UnitTest]
    public void Enrich_FillsExtentAndResolution()
    {
        var enriched = GdalInfoCoverageEnricher.Enrich(BaseMetadata(), GdalInfoJson);

        enriched.Resolution.X.Should().BeApproximately(0.1, 1e-9);
        enriched.Resolution.Y.Should().BeApproximately(0.1, 1e-9);

        enriched.Extent.Should().NotBeNull();
        var extent = enriched.Extent!.Value;
        extent.XMin.Should().BeApproximately(-156.55, 1e-9);
        extent.YMin.Should().BeApproximately(20.45, 1e-9);
        extent.XMax.Should().BeApproximately(-156.05, 1e-9);
        extent.YMax.Should().BeApproximately(20.85, 1e-9);
        extent.Srid.Should().Be(4326);
    }

    [UnitTest]
    public void Enrich_DecodesCfTemporalExtent()
    {
        var enriched = GdalInfoCoverageEnricher.Enrich(BaseMetadata(), GdalInfoJson);

        enriched.Temporal.Should().NotBeNull();
        enriched.Temporal!.Start.Should().Be(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        enriched.Temporal.End.Should().Be(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        enriched.Temporal.StepCount.Should().Be(3);
        enriched.Vertical.Should().BeNull();
    }

    [UnitTest]
    public void Enrich_PreservesVariablesAndFormat()
    {
        var enriched = GdalInfoCoverageEnricher.Enrich(BaseMetadata(), GdalInfoJson);

        enriched.Format.Should().Be(MultidimensionalCoverageFormat.NetCdf4);
        enriched.Variables.Should().ContainSingle().Which.Name.Should().Be("sst");
    }

    [UnitTest]
    public void Enrich_ToleratesEmptyOrInvalidJson()
    {
        var baseMetadata = BaseMetadata();

        GdalInfoCoverageEnricher.Enrich(baseMetadata, "").Should().BeSameAs(baseMetadata);
        GdalInfoCoverageEnricher.Enrich(baseMetadata, "not json").Should().BeSameAs(baseMetadata);

        var verticalSource = GdalInfoCoverageEnricher.Enrich(baseMetadata, "{}");
        verticalSource.Extent.Should().BeNull();
        verticalSource.Temporal.Should().BeNull();
    }
}
