// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Tiles;

/// <summary>
/// Pure-unit guards for the shared elevation/vertical selection parser (#1792 Shape A) as
/// consumed by the OGC API Tiles adapter. The adapter replaces the previous blanket
/// <c>subset</c> reject with this parser: a vertical axis (<c>Z</c> / <c>elevation</c> /
/// <c>height</c>) is accepted and recorded, while any other axis (or a malformed vertical
/// value) must still be rejected with 400. The latter is the CITE Tiles 16/16
/// unknown-subset guard. These tests run without a database (no Docker).
/// </summary>
[Protocol(TestProtocols.OgcApiTiles)]
public sealed class OgcTilesVerticalSubsetTests
{
    [UnitTest]
    public void TryParseTilesSubset_WithZSingleValue_RecordsInstant()
    {
        var parsed = OgcVerticalSelectionParser.TryParseTilesSubset(
            "Z(100)", out var selection, out var isVerticalAxis, out var error);

        parsed.Should().BeTrue();
        isVerticalAxis.Should().BeTrue();
        error.Should().BeNull();
        selection.Should().NotBeNull();
        selection!.Value.Min.Should().Be(100);
        selection!.Value.Max.Should().Be(100);
        selection!.Value.IsInstant.Should().BeTrue();
    }

    [UnitTest]
    public void TryParseTilesSubset_WithElevationInterval_RecordsRange()
    {
        var parsed = OgcVerticalSelectionParser.TryParseTilesSubset(
            "elevation(100:300)", out var selection, out var isVerticalAxis, out var error);

        parsed.Should().BeTrue();
        isVerticalAxis.Should().BeTrue();
        error.Should().BeNull();
        selection.Should().NotBeNull();
        selection!.Value.Min.Should().Be(100);
        selection!.Value.Max.Should().Be(300);
        selection!.Value.IsInstant.Should().BeFalse();
    }

    [UnitTest]
    public void TryParseTilesSubset_WithInvertedInterval_IsMalformed()
    {
        // A vertical axis whose interval is out of order is malformed: the axis is recognized
        // (isVerticalAxis = true) so the adapter rejects with 400 (not the unknown-axis path).
        var parsed = OgcVerticalSelectionParser.TryParseTilesSubset(
            "Z(300:100)", out var selection, out var isVerticalAxis, out var error);

        parsed.Should().BeFalse();
        isVerticalAxis.Should().BeTrue();
        selection.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public void TryParseTilesSubset_WithMalformedVerticalValue_IsMalformed()
    {
        var parsed = OgcVerticalSelectionParser.TryParseTilesSubset(
            "Z(abc)", out var selection, out var isVerticalAxis, out var error);

        parsed.Should().BeFalse();
        isVerticalAxis.Should().BeTrue();
        selection.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public void TryParseTilesSubset_WithUnknownAxis_IsNotVertical()
    {
        // CITE guard: the easting axis E(...) is not a vertical axis. The adapter must fall
        // through to its blanket unsupported-subset reject (400) — preserving CITE 16/16.
        var parsed = OgcVerticalSelectionParser.TryParseTilesSubset(
            "E(0:1)", out var selection, out var isVerticalAxis, out var error);

        parsed.Should().BeFalse();
        isVerticalAxis.Should().BeFalse();
        selection.Should().BeNull();
        error.Should().BeNull();
    }

    [UnitTest]
    public void TryParseTilesSubset_WithNonAxisForm_IsNotVertical()
    {
        // A subset value that is not in axis(value) form is not a vertical selection; the
        // adapter rejects it via the existing unsupported-subset path.
        var parsed = OgcVerticalSelectionParser.TryParseTilesSubset(
            "garbage", out var selection, out var isVerticalAxis, out var error);

        parsed.Should().BeFalse();
        isVerticalAxis.Should().BeFalse();
        selection.Should().BeNull();
    }
}
