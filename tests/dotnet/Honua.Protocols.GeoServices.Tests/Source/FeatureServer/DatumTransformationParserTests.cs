// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Unit tests for <see cref="DatumTransformationParser"/> and
/// <see cref="VerticalCoordinateSystemHelpers"/> (issue #1274). Validates parsing of the
/// Esri <c>datumTransformation</c> parameter (bare WKID + composite geoTransforms) and
/// detection of vertical-CS requests.
/// </summary>
public sealed class DatumTransformationParserTests
{
    [UnitTest]
    public void TryParse_NullOrEmpty_ReturnsTrueWithNoRequest()
    {
        DatumTransformationParser.TryParse(null, out var request, out var error).Should().BeTrue();
        request.Should().BeNull();
        error.Should().BeNull();

        DatumTransformationParser.TryParse("   ", out request, out error).Should().BeTrue();
        request.Should().BeNull();
    }

    [UnitTest]
    public void TryParse_BareWkid_ParsesForward()
    {
        DatumTransformationParser.TryParse("1241", out var request, out var error).Should().BeTrue();
        error.Should().BeNull();
        request.Should().NotBeNull();
        request!.Value.Wkid.Should().Be(1241);
        request.Value.TransformForward.Should().BeTrue();
    }

    [UnitTest]
    public void TryParse_CompositeGeoTransforms_ParsesWkidAndDirection()
    {
        const string json = """{"geoTransforms":[{"wkid":1241,"transformForward":false}]}""";

        DatumTransformationParser.TryParse(json, out var request, out var error).Should().BeTrue();
        error.Should().BeNull();
        request!.Value.Wkid.Should().Be(1241);
        request.Value.TransformForward.Should().BeFalse();
    }

    [UnitTest]
    public void TryParse_CompositeDefaultsTransformForwardTrue()
    {
        const string json = """{"geoTransforms":[{"wkid":108001}]}""";

        DatumTransformationParser.TryParse(json, out var request, out _).Should().BeTrue();
        request!.Value.TransformForward.Should().BeTrue();
    }

    [UnitTest]
    public void TryParse_CompositeEmptyArray_Fails()
    {
        DatumTransformationParser.TryParse("""{"geoTransforms":[]}""", out var request, out var error).Should().BeFalse();
        request.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public void TryParse_MalformedJson_Fails()
    {
        DatumTransformationParser.TryParse("{not json", out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public void TryParse_NonNumericNonJson_Fails()
    {
        DatumTransformationParser.TryParse("abc", out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public void RequestsVerticalTransform_VcsWkidPresent_ReturnsTrue()
    {
        VerticalCoordinateSystemHelpers
            .RequestsVerticalTransform("""{"wkid":4326,"vcsWkid":5703}""")
            .Should().BeTrue();
    }

    [UnitTest]
    public void RequestsVerticalTransform_NoVcs_ReturnsFalse()
    {
        VerticalCoordinateSystemHelpers.RequestsVerticalTransform("4326").Should().BeFalse();
        VerticalCoordinateSystemHelpers.RequestsVerticalTransform("""{"wkid":4326}""").Should().BeFalse();
        VerticalCoordinateSystemHelpers.RequestsVerticalTransform(null).Should().BeFalse();
    }
}
