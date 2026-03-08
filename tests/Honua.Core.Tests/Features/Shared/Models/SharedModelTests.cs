// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Tests.Features.SharedModels;

/// <summary>
/// Tests for shared model components and their conversions
/// </summary>
public class SharedModelTests
{
    private static readonly double[] _expectedBbox = [-180.0, -90.0, 180.0, 90.0];
    private static readonly double[] _expectedBbox2dLower = [-180.0, -90.0];
    private static readonly double[] _expectedBbox2dUpper = [180.0, 90.0];
    [Fact]
    public void SpatialReference_Create_WithWkidOnly_SetsCorrectValues()
    {
        // Arrange & Act
        var spatialRef = SpatialReference.Create(4326);

        // Assert
        spatialRef.Wkid.Should().Be(4326);
        spatialRef.LatestWkid.Should().BeNull();
        spatialRef.VcsWkid.Should().BeNull();
        spatialRef.LatestVcsWkid.Should().BeNull();
        spatialRef.Wkt.Should().BeNull();
    }

    [Fact]
    public void SpatialReference_Create_WithAllParameters_SetsCorrectValues()
    {
        // Arrange & Act
        var spatialRef = SpatialReference.Create(4326, 4979, 5773, 5774, "GEOGCS[\"WGS 84\"]");

        // Assert
        spatialRef.Wkid.Should().Be(4326);
        spatialRef.LatestWkid.Should().Be(4979);
        spatialRef.VcsWkid.Should().Be(5773);
        spatialRef.LatestVcsWkid.Should().Be(5774);
        spatialRef.Wkt.Should().Be("GEOGCS[\"WGS 84\"]");
    }

    [Fact]
    public void ServiceError_Create_WithCodeAndMessage_SetsCorrectValues()
    {
        // Arrange & Act
        var error = ServiceError.Create("400", "Bad request");

        // Assert
        error.Code.Should().Be("400");
        error.Message.Should().Be("Bad request");
        error.Target.Should().BeNull();
        error.Details.Should().BeNull();
    }

    [Fact]
    public void ServiceError_GetNumericCode_ReturnsCorrectValue()
    {
        // Arrange
        var numericError = ServiceError.Create(404, "Not found");
        var stringError = ServiceError.Create("validation", "Invalid input");

        // Act & Assert
        numericError.GetNumericCode().Should().Be(404);
        stringError.GetNumericCode().Should().BeNull();
    }

    [Fact]
    public void GeoJsonFeatureBase_Create_WithoutGeometry_SetsCorrectValues()
    {
        // Arrange
        var properties = new Dictionary<string, object?>
        {
            ["name"] = "Test Feature",
            ["value"] = 42
        }.AsReadOnly();

        // Act
        var feature = GeoJsonFeatureBase.Create(1, properties);

        // Assert
        feature.Id.Should().Be(1);
        feature.Properties.Should().BeEquivalentTo(properties);
        feature.HasGeometry.Should().BeFalse();
    }

    [Fact]
    public void PagedResponseBase_Create_WithAllParameters_SetsCorrectValues()
    {
        // Arrange & Act
        var response = PagedResponseBase.Create(10, 100, true);

        // Assert
        response.ReturnedCount.Should().Be(10);
        response.TotalCount.Should().Be(100);
        response.ExceededTransferLimit.Should().BeTrue();
    }

    [Fact]
    public void ExtentExtensions_ToBoundingBox_ReturnsCorrectArray()
    {
        // Arrange
        var extent = FeatureExtent.Create(-180.0, -90.0, 180.0, 90.0, 4326);

        // Act
        var bbox = ExtentExtensions.ToBoundingBox(extent);

        // Assert
        bbox.Should().BeEquivalentTo(_expectedBbox);
    }

    [Fact]
    public void ExtentExtensions_ToBoundingBox2D_ReturnsCorrectArray()
    {
        // Arrange
        var extent = FeatureExtent.Create(-180.0, -90.0, 180.0, 90.0, 4326);

        // Act
        var bbox2d = extent.ToBoundingBox2D();

        // Assert
        bbox2d.Should().HaveCount(2);
        bbox2d[0].Should().BeEquivalentTo(_expectedBbox2dLower);
        bbox2d[1].Should().BeEquivalentTo(_expectedBbox2dUpper);
    }

    [Fact]
    public void ExtentExtensions_ToFeatureExtent_FromBoundingBox_ReturnsCorrectExtent()
    {
        // Arrange
        var bbox = _expectedBbox;

        // Act
        var extent = bbox.ToFeatureExtent(4326);

        // Assert
        extent.MinX.Should().Be(-180.0);
        extent.MinY.Should().Be(-90.0);
        extent.MaxX.Should().Be(180.0);
        extent.MaxY.Should().Be(90.0);
        extent.SpatialReference.Should().Be(4326);
    }

    [Fact]
    public void BoundingBox_WithAntimeridianCrossing_IsValidAndContainsRanges()
    {
        var crossing = BoundingBox.Create(170, -10, -170, 10, 4326);
        var east = BoundingBox.Create(175, -5, 179, 5, 4326);
        var west = BoundingBox.Create(-179, -5, -175, 5, 4326);
        var middle = BoundingBox.Create(-50, -5, -40, 5, 4326);

        crossing.IsAntimeridianCrossing.Should().BeTrue();
        crossing.IsValid.Should().BeTrue();
        crossing.Width.Should().BeApproximately(20, 1e-9);
        crossing.Contains(east).Should().BeTrue();
        crossing.Contains(west).Should().BeTrue();
        crossing.Intersects(middle).Should().BeFalse();
    }

    [Fact]
    public void BoundingBox_Union_DisjointRanges_UsesShortestLongitudeSpan()
    {
        var left = BoundingBox.Create(-10, -5, 10, 5, 4326);
        var right = BoundingBox.Create(30, -5, 40, 5, 4326);

        var union = left.Union(right);

        union.IsAntimeridianCrossing.Should().BeFalse();
        union.MinX.Should().Be(-10);
        union.MaxX.Should().Be(40);
    }

    [Fact]
    public void BoundingBox_Union_AcrossDateline_PreservesCrossing()
    {
        var east = BoundingBox.Create(170, -10, 179, 10, 4326);
        var west = BoundingBox.Create(-179, -10, -170, 10, 4326);

        var union = east.Union(west);

        union.IsAntimeridianCrossing.Should().BeTrue();
        union.MinX.Should().Be(170);
        union.MaxX.Should().Be(-170);
    }

    [Fact]
    public void BoundingBox_Intersection_PreservesAntimeridianCrossing()
    {
        var crossing = BoundingBox.Create(170, -10, -170, 10, 4326);
        var tighter = BoundingBox.Create(175, -5, -175, 5, 4326);

        var intersection = crossing.Intersection(tighter);

        intersection.Should().NotBeNull();
        intersection!.Value.IsAntimeridianCrossing.Should().BeTrue();
        intersection.Value.MinX.Should().Be(175);
        intersection.Value.MaxX.Should().Be(-175);
    }

    [Fact]
    public void ExtentExtensions_ExtractSridFromCrs_WithEpsgUri_ReturnsCorrectSrid()
    {
        // Arrange
        var crsUri = "http://www.opengis.net/def/crs/EPSG/0/4326";

        // Act
        var srid = ExtentExtensions.ExtractSridFromCrs(crsUri);

        // Assert
        srid.Should().Be(4326);
    }

    [Fact]
    public void ExtentExtensions_ExtractSridFromCrs_WithEpsgPrefix_ReturnsCorrectSrid()
    {
        // Arrange
        var crsString = "EPSG:3857";

        // Act
        var srid = ExtentExtensions.ExtractSridFromCrs(crsString);

        // Assert
        srid.Should().Be(3857);
    }

    [Fact]
    public void ExtentExtensions_ExtractSridFromCrs_WithInvalidCrs_ReturnsDefault()
    {
        // Arrange
        var invalidCrs = "invalid-crs";

        // Act
        var srid = ExtentExtensions.ExtractSridFromCrs(invalidCrs);

        // Assert
        srid.Should().Be(4326); // Default WGS84
    }

    [Fact]
    public void ExtentExtensions_ToOgcCrsUri_ReturnsCorrectFormat()
    {
        // Act
        var crsUri = ExtentExtensions.ToOgcCrsUri(3857);

        // Assert
        crsUri.Should().Be("http://www.opengis.net/def/crs/EPSG/0/3857");
    }

    [Fact]
    public void ModelConversions_ToGeoJsonBase_FromFeature_ReturnsCorrectBase()
    {
        // Arrange
        var attributes = new Dictionary<string, object?>
        {
            ["name"] = "Test",
            ["value"] = 123
        }.ToImmutableDictionary();
        var geometry = new byte[] { 1, 2, 3, 4 }; // Mock WKB
        var feature = Feature.Create(1, geometry, attributes);

        // Act
        var geoJsonBase = feature.ToGeoJsonBase();

        // Assert
        geoJsonBase.Id.Should().Be(1L);
        geoJsonBase.Properties.Should().BeEquivalentTo(attributes);
        geoJsonBase.HasGeometry.Should().BeTrue();
    }

    [Fact]
    public void ModelConversions_ToSpatialReference_FromSrid_ReturnsCorrectSpatialRef()
    {
        // Act
        var spatialRef = 4326.ToSpatialReference();

        // Assert
        spatialRef.Wkid.Should().Be(4326);
        spatialRef.LatestWkid.Should().BeNull();
    }

    [Fact]
    public void ModelConversions_CreateValidationError_ReturnsCorrectError()
    {
        // Act
        var error = ModelConversions.CreateValidationError("Invalid field", "fieldName");

        // Assert
        error.Code.Should().Be("400");
        error.Message.Should().Be("Invalid field");
        error.Target.Should().Be("fieldName");
    }

    [Fact]
    public void ModelConversions_CreateNotFoundError_ReturnsCorrectError()
    {
        // Act
        var error = ModelConversions.CreateNotFoundError("Layer");

        // Assert
        error.Code.Should().Be("404");
        error.Message.Should().Be("Layer not found");
    }

    [Fact]
    public void GeoJsonGeometryBase_Create_WithType_SetsCorrectValues()
    {
        // Act
        var pointGeometry = GeoJsonGeometryBase.Create("Point");
        var collectionGeometry = GeoJsonGeometryBase.Create("GeometryCollection");

        // Assert
        pointGeometry.Type.Should().Be("Point");
        pointGeometry.HasCoordinates.Should().BeTrue();
        pointGeometry.IsGeometryCollection.Should().BeFalse();

        collectionGeometry.Type.Should().Be("GeometryCollection");
        collectionGeometry.HasCoordinates.Should().BeFalse();
        collectionGeometry.IsGeometryCollection.Should().BeTrue();
    }
}
