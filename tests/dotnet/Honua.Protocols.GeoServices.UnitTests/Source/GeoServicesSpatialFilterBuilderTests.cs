// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

public sealed class GeoServicesSpatialFilterBuilderTests
{
    [UnitTheory]
    [InlineData(4326, 190d, 200d, -170d, -160d)]
    [InlineData(4326, -200d, -190d, 160d, 170d)]
    [InlineData(4326, 0d, 360d, -180d, 180d)]
    [InlineData(4326, -10d, 10d, -10d, 10d)]
    [InlineData(3857, 190d, 200d, 190d, 200d)]
    public void BuildSpatialFilter_SimpleEnvelope_FastBoundsMatchGeometry(
        int srid, double west, double east, double expectedWest, double expectedEast)
    {
        var filter = GeoServicesSpatialFilterBuilder.BuildSpatialFilter(
            new QueryParameters { SpatialRel = "esriSpatialRelEnvelopeIntersects" },
            new GeoServicesGeometry { Xmin = west, Ymin = -10d, Xmax = east, Ymax = 10d },
            srid);

        filter.IsSimpleEnvelope.Should().BeTrue();
        filter.AllowEnvelopeOnly.Should().BeTrue();
        filter.AntimeridianSplit.Should().BeFalse();
        filter.EnvelopeMinX.Should().Be(expectedWest);
        filter.EnvelopeMaxX.Should().Be(expectedEast);

        var bounds = new WKBReader().Read(filter.Geometry).EnvelopeInternal;
        filter.EnvelopeMinX.Should().Be(bounds.MinX);
        filter.EnvelopeMinY.Should().Be(bounds.MinY);
        filter.EnvelopeMaxX.Should().Be(bounds.MaxX);
        filter.EnvelopeMaxY.Should().Be(bounds.MaxY);
    }

    [UnitTest]
    public void BuildSpatialFilter_OmittedUnits_UsesLinearUnitOfQuerySpatialReference()
    {
        var filter = GeoServicesSpatialFilterBuilder.BuildSpatialFilter(
            new QueryParameters
            {
                SpatialRel = "esriSpatialRelIntersects",
                Distance = 500
            },
            new GeoServicesGeometry { X = 0, Y = 0 },
            2263,
            linearMetersPerUnit: CrsLinearUnitFactor.UsSurveyFootMeters);

        filter.Distance.Should().BeApproximately(500d * CrsLinearUnitFactor.UsSurveyFootMeters, 1e-9);
        filter.DistanceUnit.Should().Be(DistanceUnit.Meters);
    }

    [UnitTest]
    public void BuildSpatialFilter_ExplicitMeters_IgnoresLinearUnitFactor()
    {
        var filter = GeoServicesSpatialFilterBuilder.BuildSpatialFilter(
            new QueryParameters
            {
                SpatialRel = "esriSpatialRelIntersects",
                Distance = 500,
                Units = "esriSRUnit_Meter"
            },
            new GeoServicesGeometry { X = 0, Y = 0 },
            2263,
            linearMetersPerUnit: CrsLinearUnitFactor.UsSurveyFootMeters);

        filter.Distance.Should().Be(500);
        filter.DistanceUnit.Should().Be(DistanceUnit.Meters);
    }

    [UnitTest]
    public void BuildSpatialFilter_OmittedUnitsWithoutLinearFactor_StaysInMetres()
    {
        var filter = GeoServicesSpatialFilterBuilder.BuildSpatialFilter(
            new QueryParameters
            {
                SpatialRel = "esriSpatialRelIntersects",
                Distance = 500
            },
            new GeoServicesGeometry { X = 0, Y = 0 },
            4326);

        filter.Distance.Should().Be(500);
        filter.DistanceUnit.Should().Be(DistanceUnit.Meters);
    }

    [UnitTest]
    public void BuildSpatialFilter_WithDatelineCrossingEnvelope_ReturnsMultiPolygon()
    {
        var filter = GeoServicesSpatialFilterBuilder.BuildSpatialFilter(
            new QueryParameters(),
            new GeoServicesGeometry
            {
                Xmin = 170,
                Ymin = -10,
                Xmax = -170,
                Ymax = 10,
                SpatialReference = new GeoServicesSpatialReference { Wkid = 4326 }
            },
            4326);

        filter.Srid.Should().Be(4326);
        filter.AntimeridianSplit.Should().BeTrue();
        filter.IsSimpleEnvelope.Should().BeFalse();

        var geometry = new WKBReader().Read(filter.Geometry);
        geometry.Should().BeOfType<MultiPolygon>();

        var multiPolygon = (MultiPolygon)geometry;
        multiPolygon.NumGeometries.Should().Be(2);
    }

    [UnitTest]
    public void BuildSpatialFilter_WithUnwrappedPacificEnvelope_FoldsAcrossTheAntimeridian()
    {
        var filter = GeoServicesSpatialFilterBuilder.BuildSpatialFilter(
            new QueryParameters(),
            new GeoServicesGeometry
            {
                Xmin = 170,
                Ymin = -10,
                Xmax = 190,
                Ymax = 10
            },
            4326);

        filter.AntimeridianSplit.Should().BeTrue();

        var multiPolygon = new WKBReader().Read(filter.Geometry).Should().BeOfType<MultiPolygon>().Subject;
        multiPolygon.NumGeometries.Should().Be(2);
        multiPolygon.Coordinates.Should().OnlyContain(coordinate => coordinate.X >= -180d && coordinate.X <= 180d);
    }
}
