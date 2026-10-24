// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
        enriched.YAxisAscending.Should().BeFalse();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Enrich_PositivePixelHeight_RecordsAscendingYAxis()
    {
        var json = GdalInfoJson.Replace(
            "20.85, 0.0, -0.1", "20.45, 0.0, 0.1", StringComparison.Ordinal);

        var enriched = GdalInfoCoverageEnricher.Enrich(BaseMetadata(), json);

        enriched.YAxisAscending.Should().BeTrue();
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

    [UnitTest]
    public void Enrich_ZarrDimensionMetadata_DecodesFixtureGrid()
    {
        // gdalinfo -json on the derived Zarr (the container path that does not need
        // userfaultfd). Two time bands, cell centers half a pixel inside the extent.
        const string zarrInfo = """
        {
          "size": [4, 4],
          "geoTransform": [-122.525, 0.05, 0, 37.675, 0, 0.05],
          "cornerCoordinates": {
            "upperLeft": [-122.525, 37.675],
            "lowerRight": [-122.325, 37.875]
          },
          "bands": [
            {
              "type": "Float32",
              "noDataValue": -9999,
              "minimum": 11,
              "maximum": 25,
              "mean": 18,
              "stdDev": 4,
              "metadata": { "": { "DIM_time_VALUE": "0", "DIM_time_UNIT": "hours since 2024-01-01 00:00:00" } }
            },
            {
              "type": "Float32",
              "noDataValue": -9999,
              "minimum": 21,
              "maximum": 35,
              "mean": 28,
              "stdDev": 3,
              "metadata": { "": { "DIM_time_VALUE": "24", "DIM_time_UNIT": "hours since 2024-01-01 00:00:00" } }
            }
          ]
        }
        """;

        var enriched = GdalInfoCoverageEnricher.Enrich(BaseMetadata(), zarrInfo);

        var extent = enriched.Extent ?? throw new InvalidOperationException("Coverage extent was not enriched.");
        extent.XMin.Should().BeApproximately(-122.525, 1e-9);
        extent.YMin.Should().BeApproximately(37.675, 1e-9);
        extent.XMax.Should().BeApproximately(-122.325, 1e-9);
        extent.YMax.Should().BeApproximately(37.875, 1e-9);
        enriched.Resolution.X.Should().BeApproximately(0.05, 1e-9);
        enriched.Resolution.Y.Should().BeApproximately(0.05, 1e-9);
        enriched.YAxisAscending.Should().BeTrue();
        enriched.Temporal.Should().NotBeNull();
        enriched.Temporal!.Start.Should().Be(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        enriched.Temporal.End.Should().Be(new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero));
        enriched.Temporal.StepCount.Should().Be(2);
        enriched.Statistics.Should().NotBeNull();
        enriched.Statistics!.Minimum.Should().Be(11);
        enriched.Statistics.Maximum.Should().Be(35);
        enriched.Statistics.Mean.Should().Be(23);
        enriched.Statistics.StandardDeviation.Should().BeNull("two bands do not yield one standard deviation");
    }
}
