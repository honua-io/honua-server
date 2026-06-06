// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Unit tests for <see cref="GeoServicesGeometryParser"/> geometryType validation (#1462).
/// </summary>
public sealed class GeoServicesGeometryParserTests
{
    [UnitTest]
    public void TryParse_WithEnvelopeCoordinates_ParsesEnvelope()
    {
        var parsed = GeoServicesGeometryParser.TryParseGeoServicesGeometry(
            "-122.6,37.4,-122.3,37.9",
            "esriGeometryEnvelope",
            out var geometry,
            out var error);

        parsed.Should().BeTrue();
        error.Should().BeNull();
        geometry.Should().NotBeNull();
        geometry!.Xmin.Should().Be(-122.6);
        geometry.Ymax.Should().Be(37.9);
    }

    [UnitTest]
    public void TryParse_WithPointCoordinates_ParsesPoint()
    {
        var parsed = GeoServicesGeometryParser.TryParseGeoServicesGeometry(
            "-122.4,37.7",
            "esriGeometryPoint",
            out var geometry,
            out var error);

        parsed.Should().BeTrue();
        error.Should().BeNull();
        geometry.Should().NotBeNull();
        geometry!.X.Should().Be(-122.4);
        geometry.Y.Should().Be(37.7);
    }

    [UnitTest]
    public void TryParse_WithNoGeometryType_InfersFromCoordinateCount()
    {
        var parsed = GeoServicesGeometryParser.TryParseGeoServicesGeometry(
            "-122.6,37.4,-122.3,37.9",
            geometryType: null,
            out var geometry,
            out var error);

        parsed.Should().BeTrue();
        error.Should().BeNull();
        geometry!.Xmax.Should().Be(-122.3);
    }

    // BUG C: an unknown geometryType must be rejected, not silently coerced to an envelope
    // based on the coordinate count.
    [UnitTest]
    public void TryParse_WithUnknownGeometryType_Fails()
    {
        var parsed = GeoServicesGeometryParser.TryParseGeoServicesGeometry(
            "-122.6,37.4,-122.3,37.9",
            "esriGeometryBogus",
            out var geometry,
            out var error);

        parsed.Should().BeFalse();
        geometry.Should().BeNull();
        error.Should().Contain("geometryType").And.Contain("esriGeometryBogus");
    }

    [Theory]
    [InlineData("esriGeometryPoint")]
    [InlineData("esriGeometryMultipoint")]
    [InlineData("esriGeometryPolyline")]
    [InlineData("esriGeometryPolygon")]
    [InlineData("esriGeometryEnvelope")]
    [InlineData("ESRIGEOMETRYENVELOPE")]
    public void TryParse_WithKnownGeometryTypeAndNoGeometry_Succeeds(string geometryType)
    {
        var parsed = GeoServicesGeometryParser.TryParseGeoServicesGeometry(
            geometryText: null,
            geometryType,
            out _,
            out var error);

        parsed.Should().BeTrue();
        error.Should().BeNull();
    }
}
