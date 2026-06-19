// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Multidimensional.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Domain;
using Honua.Core.Features.Raster.Multidimensional.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Raster.Multidimensional;

/// <summary>
/// Verifies the Path B (ADR-0039) GDAL <c>gdalmdiminfo</c> JSON mapper. The
/// golden document below is the verbatim output of
/// <c>gdalmdiminfo maui_sst.nc</c> (OSGeo GDAL <c>ubuntu-full</c> image) over a
/// CF-1.8 NetCDF4 cube: data variable <c>sst(time,lat,lon)</c> with deflate
/// chunking, plus <c>time</c>/<c>lat</c>/<c>lon</c> coordinate variables.
/// </summary>
public sealed class GdalMultidimensionalMetadataMapperTests
{
    // gdalmdiminfo maui_sst.nc  (NETCDF4, CF-1.8)
    private const string GoldenMdimInfo = """
    {
      "type": "group",
      "driver": "netCDF",
      "name": "/",
      "attributes": { "title": "Maui SST test cube", "Conventions": "CF-1.8" },
      "dimensions": [
        {
          "name": "time", "full_name": "/time", "size": 3, "type": "TEMPORAL",
          "indexing_variable": {
            "time": {
              "full_name": "/time", "datatype": "Float64",
              "dimensions": [ "/time" ], "dimension_size": [ 3 ],
              "attributes": { "standard_name": "time" },
              "unit": "hours since 2026-01-01 00:00:00"
            }
          }
        },
        {
          "name": "lat", "full_name": "/lat", "size": 4, "type": "HORIZONTAL_Y", "direction": "NORTH",
          "indexing_variable": {
            "lat": {
              "full_name": "/lat", "datatype": "Float64",
              "dimensions": [ "/lat" ], "dimension_size": [ 4 ],
              "attributes": { "standard_name": "latitude" },
              "unit": "degrees_north"
            }
          }
        },
        {
          "name": "lon", "full_name": "/lon", "size": 5, "type": "HORIZONTAL_X", "direction": "EAST",
          "indexing_variable": {
            "lon": {
              "full_name": "/lon", "datatype": "Float64",
              "dimensions": [ "/lon" ], "dimension_size": [ 5 ],
              "attributes": { "standard_name": "longitude" },
              "unit": "degrees_east"
            }
          }
        }
      ],
      "arrays": {
        "time": {
          "full_name": "/time", "datatype": "Float64",
          "dimensions": [ "/time" ], "dimension_size": [ 3 ],
          "attributes": { "standard_name": "time" },
          "unit": "hours since 2026-01-01 00:00:00"
        },
        "lat": {
          "full_name": "/lat", "datatype": "Float64",
          "dimensions": [ "/lat" ], "dimension_size": [ 4 ],
          "attributes": { "standard_name": "latitude" }, "unit": "degrees_north"
        },
        "lon": {
          "full_name": "/lon", "datatype": "Float64",
          "dimensions": [ "/lon" ], "dimension_size": [ 5 ],
          "attributes": { "standard_name": "longitude" }, "unit": "degrees_east"
        },
        "sst": {
          "full_name": "/sst", "datatype": "Float32",
          "dimensions": [ "/time", "/lat", "/lon" ],
          "dimension_size": [ 3, 4, 5 ],
          "block_size": [ 1, 4, 5 ],
          "attributes": {
            "standard_name": "sea_surface_temperature",
            "long_name": "Sea Surface Temperature"
          },
          "unit": "degC",
          "nodata_value": -9999,
          "structural_info": { "COMPRESS": "DEFLATE" }
        }
      },
      "structural_info": { "NC_FORMAT": "NETCDF4" }
    }
    """;

    [UnitTest]
    public void Map_AutoDiscovery_ExposesOnlyDataVariables()
    {
        var metadata = GdalMultidimensionalMetadataMapper.Map(
            GoldenMdimInfo,
            MultidimensionalCoverageFormat.NetCdf4,
            Array.Empty<string>());

        metadata.Format.Should().Be(MultidimensionalCoverageFormat.NetCdf4);
        metadata.Variables.Should().ContainSingle()
            .Which.Name.Should().Be("sst");
    }

    [UnitTest]
    public void Map_DataVariable_CapturesStructure()
    {
        var metadata = GdalMultidimensionalMetadataMapper.Map(
            GoldenMdimInfo,
            MultidimensionalCoverageFormat.NetCdf4,
            Array.Empty<string>());

        var sst = metadata.Variables.Single();
        sst.DataType.Should().Be("Float32");
        sst.Units.Should().Be("degC");
        sst.StandardName.Should().Be("sea_surface_temperature");
        sst.LongName.Should().Be("Sea Surface Temperature");
        sst.NoData.Should().Be(-9999d);

        sst.Dimensions.Should().HaveCount(3);
        sst.Dimensions[0].Should().Be(new MultidimensionalCoverageDimension("time", 3));
        sst.Dimensions[1].Should().Be(new MultidimensionalCoverageDimension("lat", 4));
        sst.Dimensions[2].Should().Be(new MultidimensionalCoverageDimension("lon", 5));
    }

    [UnitTest]
    public void Map_DataVariable_CapturesChunkLayout()
    {
        var metadata = GdalMultidimensionalMetadataMapper.Map(
            GoldenMdimInfo,
            MultidimensionalCoverageFormat.NetCdf4,
            Array.Empty<string>());

        var chunk = metadata.Variables.Single().ChunkLayout;
        chunk.Should().NotBeNull();
        chunk!.ChunkShape.Should().Equal(1L, 4L, 5L);
        chunk.Compression.Should().Be("deflate");
        chunk.ShuffleFilter.Should().BeFalse();
    }

    [UnitTest]
    public void Map_GeographicAxes_InfersWgs84()
    {
        var metadata = GdalMultidimensionalMetadataMapper.Map(
            GoldenMdimInfo,
            MultidimensionalCoverageFormat.NetCdf4,
            Array.Empty<string>());

        metadata.Srid.Should().Be(4326);
    }

    [UnitTest]
    public void Map_ExtentAndRanges_DeferredToEnrichmentPass()
    {
        // gdalmdiminfo does not emit coordinate values, so spatial extent,
        // resolution, and temporal/vertical bounds are populated by the
        // convert-time enrichment pass — not by this structural mapper.
        var metadata = GdalMultidimensionalMetadataMapper.Map(
            GoldenMdimInfo,
            MultidimensionalCoverageFormat.NetCdf4,
            Array.Empty<string>());

        metadata.Extent.Should().BeNull();
        metadata.Resolution.Should().Be((0d, 0d));
        metadata.Temporal.Should().BeNull();
        metadata.Vertical.Should().BeNull();
    }

    [UnitTest]
    public void Map_DeclaredVariables_FiltersToSelection()
    {
        var metadata = GdalMultidimensionalMetadataMapper.Map(
            GoldenMdimInfo,
            MultidimensionalCoverageFormat.NetCdf4,
            new[] { "sst" });

        metadata.Variables.Should().ContainSingle().Which.Name.Should().Be("sst");
    }

    [UnitTest]
    public void Map_DeclaredVariables_NoMatch_Throws()
    {
        var act = () => GdalMultidimensionalMetadataMapper.Map(
            GoldenMdimInfo,
            MultidimensionalCoverageFormat.NetCdf4,
            new[] { "does_not_exist" });

        act.Should().Throw<MultidimensionalCoverageUnsupportedLayoutException>()
            .Which.Message.Should().Contain("declared variables");
    }

    [UnitTest]
    public void Map_InvalidJson_Throws()
    {
        var act = () => GdalMultidimensionalMetadataMapper.Map(
            "not json at all",
            MultidimensionalCoverageFormat.NetCdf4,
            Array.Empty<string>());

        act.Should().Throw<MultidimensionalCoverageUnsupportedLayoutException>();
    }
}
