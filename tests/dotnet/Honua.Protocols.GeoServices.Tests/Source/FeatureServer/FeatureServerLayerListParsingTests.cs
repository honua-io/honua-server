// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// The GeoServices <c>layers</c> parameter (createReplica / extractChanges) is a JSON
/// array per the Esri spec. The ArcGIS API for Python
/// (<c>FeatureLayerCollection.extract_changes</c> / <c>create_replica</c>) sends the
/// bracketed JSON-array form (<c>[0]</c> / <c>[0,1]</c>), while the comma-separated
/// form (<c>0</c> / <c>0,1</c>) is also widely used. Both forms must parse identically.
/// </summary>
public sealed class FeatureServerLayerListParsingTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("[0]")]
    [InlineData(" [0] ")]
    [InlineData("[ 0 ]")]
    public void TryParseLayerIdList_SingleLayer_AcceptsCommaAndJsonArrayForms(string raw)
    {
        var ok = FeatureServerEndpoints.TryParseLayerIdList(raw, out var ids, out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        ids.Should().Equal(0);
    }

    [Theory]
    [InlineData("0,1")]
    [InlineData("[0,1]")]
    [InlineData("[0, 1]")]
    [InlineData(" [ 0 , 1 ] ")]
    public void TryParseLayerIdList_MultipleLayers_AcceptsCommaAndJsonArrayForms(string raw)
    {
        var ok = FeatureServerEndpoints.TryParseLayerIdList(raw, out var ids, out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        ids.Should().BeEquivalentTo([0, 1]);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("[a]")]
    [InlineData("[0,a]")]
    [InlineData("[0,]")]
    [InlineData("[]")]
    public void TryParseLayerIdList_NonNumericOrEmpty_ReturnsError(string raw)
    {
        var ok = FeatureServerEndpoints.TryParseLayerIdList(raw, out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("[0]", "0")]
    [InlineData("[0,1]", "0,1")]
    [InlineData(" [ 0 , 1 ] ", " 0 , 1 ")]
    public void StripLayerListBrackets_RemovesOuterBrackets(string raw, string expected)
    {
        FeatureServerEndpoints.StripLayerListBrackets(raw).Should().Be(expected);
    }
}
