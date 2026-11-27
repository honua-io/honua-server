// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features.Services;

public class OgcResponseFormatterTests
{
    [UnitTest]
    public void BuildCsvResponse_FormulaField_PrefixesWithSingleQuote()
    {
        // Arrange
        var features = new[]
        {
            CreateFeatureWithName("=SUM(A1:A2)")
        };

        // Act
        var csv = OgcResponseFormatter.BuildCsvResponse(features, ["name"]);

        // Assert
        var row = GetFirstDataRow(csv);
        Assert.True(row.StartsWith("1,'=SUM(A1:A2),", StringComparison.Ordinal), $"Unexpected CSV row: {row}");
    }

    [UnitTest]
    public void BuildCsvResponse_WhitespacePrefixedFormula_PrefixesWithSingleQuote()
    {
        // Arrange
        var features = new[]
        {
            CreateFeatureWithName("   =1+1")
        };

        // Act
        var csv = OgcResponseFormatter.BuildCsvResponse(features, ["name"]);

        // Assert
        var row = GetFirstDataRow(csv);
        Assert.True(row.StartsWith("1,'   =1+1,", StringComparison.Ordinal), $"Unexpected CSV row: {row}");
    }

    [UnitTest]
    public void BuildCsvResponse_NegativeNumericField_DoesNotPrefixSingleQuote()
    {
        // Arrange
        var features = new[]
        {
            CreateFeatureWithName("-42.5")
        };

        // Act
        var csv = OgcResponseFormatter.BuildCsvResponse(features, ["name"]);

        // Assert
        var row = GetFirstDataRow(csv);
        Assert.True(row.StartsWith("1,-42.5,", StringComparison.Ordinal), $"Unexpected CSV row: {row}");
        Assert.DoesNotContain("'-42.5", row, StringComparison.Ordinal);
    }

    [UnitTest]
    public void BuildCsvResponse_NegativeFormulaLikeField_PrefixesWithSingleQuote()
    {
        // Arrange
        var features = new[]
        {
            CreateFeatureWithName("-SUM(A1:A2)")
        };

        // Act
        var csv = OgcResponseFormatter.BuildCsvResponse(features, ["name"]);

        // Assert
        var row = GetFirstDataRow(csv);
        Assert.True(row.StartsWith("1,'-SUM(A1:A2),", StringComparison.Ordinal), $"Unexpected CSV row: {row}");
    }

    private static string GetFirstDataRow(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.True(lines.Length >= 2, $"Unexpected CSV payload: {csv}");
        Assert.Equal("id,name,geometry", lines[0]);
        return lines[1];
    }

    private static GeoJsonFeature CreateFeatureWithName(string value)
    {
        return new GeoJsonFeature
        {
            Id = 1,
            Properties = new Dictionary<string, object?>
            {
                ["name"] = value
            }
        };
    }

    [UnitTest]
    public void BuildGmlSingleFeature_UnderCommaDecimalCulture_UsesInvariantCultureAndPreserves3DZ()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            var pointFeature = new GeoJsonFeature
            {
                Id = 1,
                Geometry = new SimpleGeoJsonGeometry { Type = "Point", CoordinatesJson = "[-122.4194, 37.7749, 15.5]" },
                Properties = new Dictionary<string, object?>()
            };
            var pointGml = OgcResponseFormatter.BuildGmlSingleFeature(pointFeature);
            pointGml.Should().Contain("<gml:pos srsDimension=\"3\">-122.4194 37.7749 15.5</gml:pos>");
            pointGml.Should().NotContain("-122,4194");

            var lineFeature = new GeoJsonFeature
            {
                Id = 2,
                Geometry = new SimpleGeoJsonGeometry { Type = "LineString", CoordinatesJson = "[[1.5, 2.5, 3.5], [4.5, 5.5, 6.5]]" },
                Properties = new Dictionary<string, object?>()
            };
            var lineGml = OgcResponseFormatter.BuildGmlSingleFeature(lineFeature);
            lineGml.Should().Contain("<gml:posList srsDimension=\"3\">1.5 2.5 3.5 4.5 5.5 6.5</gml:posList>");

            var polygonFeature = new GeoJsonFeature
            {
                Id = 3,
                Geometry = new SimpleGeoJsonGeometry { Type = "Polygon", CoordinatesJson = "[[[0.5, 0.5, 10.25], [1.5, 0.5, 10.25], [1.5, 1.5, 10.25], [0.5, 0.5, 10.25]]]" },
                Properties = new Dictionary<string, object?>()
            };
            var polygonGml = OgcResponseFormatter.BuildGmlSingleFeature(polygonFeature);
            polygonGml.Should().Contain("<gml:posList srsDimension=\"3\">0.5 0.5 10.25 1.5 0.5 10.25 1.5 1.5 10.25 0.5 0.5 10.25</gml:posList>");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [UnitTest]
    public void WriteDirectValue_NonFiniteDoubleAndFloat_WritesJsonNullInsteadOfThrowing()
    {
        var values = new object[]
        {
            double.NaN,
            double.PositiveInfinity,
            double.NegativeInfinity,
            float.NaN,
            float.PositiveInfinity,
            float.NegativeInfinity
        };

        foreach (var value in values)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                OgcFeaturesQueryHandler.WriteDirectValue(writer, value);
            }

            Encoding.UTF8.GetString(buffer.WrittenSpan).Should().Be("null");
        }
    }

    [UnitTest]
    public void WriteRawPointGeometry_NonFiniteCoordinates_WritesJsonNull()
    {
        var invalidPoints = new[]
        {
            RawGeoServicesFeature.Create(1, "{}", double.NaN, 37.7),
            RawGeoServicesFeature.Create(2, "{}", -122.4, double.PositiveInfinity),
            RawGeoServicesFeature.Create(3, "{}", double.NegativeInfinity, 0.0)
        };

        foreach (var feature in invalidPoints)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                OgcFeaturesQueryHandler.WriteRawPointGeometry(writer, feature);
            }

            Encoding.UTF8.GetString(buffer.WrittenSpan).Should().Be("null");
        }
    }

    [UnitTest]
    public void CanUseRawPointGeoJsonFastPath_PointLayerWithHasZ_ReturnsFalseToPreserve3DZ()
    {
        var envelopeFilter = new SpatialFilter
        {
            Geometry = [1, 2, 3],
            SpatialRelationship = SpatialRelationship.Intersects,
            IsSimpleEnvelope = true
        };
        var query = new FeatureQuery { SpatialFilter = envelopeFilter };

        var point3D = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-3d", Name = "points_3d" },
            Spatial = new MetadataV2ObjectSpatial { GeometryType = MetadataV2GeometryType.Point },
            Display = new MetadataV2ObjectDisplay { HasZ = true }
        };
        var point2D = point3D with { Display = new MetadataV2ObjectDisplay { HasZ = false } };

        OgcFeaturesQueryHandler.CanUseRawPointGeoJsonFastPath(point3D, query).Should().BeFalse();
        OgcFeaturesQueryHandler.CanUseRawPointGeoJsonFastPath(point2D, query).Should().BeTrue();
    }
}
