// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wmts;

/// <summary>
/// Pure-unit guards for the shared elevation/vertical selection parser (#1792 Shape A) as
/// consumed by the WMTS adapter. WMTS GetCapabilities advertises a discrete <c>elevation</c>
/// dimension (values 100/200/300, default 100) for the CITE Terrain layer; the dimension
/// validator accepts/rejects values against those enumerated values, and this parser then
/// converts the validated token into the recorded numeric selection. These tests exercise
/// that conversion without a database (no Docker).
/// </summary>
[Protocol(TestProtocols.Wmts10)]
public sealed class OgcClassicWmtsVerticalSelectionTests
{
    [UnitTest]
    public void TryParseWmtsElevationValue_WithAdvertisedValue_RecordsSelection()
    {
        // elevation=200 is one of the advertised discrete values; the validator accepts it
        // and this resolver records it as an instant vertical selection.
        var parsed = OgcVerticalSelectionParser.TryParseWmtsElevationValue("200", out var selection);

        parsed.Should().BeTrue();
        selection.Should().NotBeNull();
        selection!.Value.Min.Should().Be(200);
        selection.Value.Max.Should().Be(200);
        selection.Value.IsInstant.Should().BeTrue();
        selection.Value.RawValue.Should().Be("200");
    }

    [UnitTest]
    public void TryParseWmtsElevationValue_WithDefaultValue_RecordsHundred()
    {
        // When the elevation parameter is omitted the adapter resolves the dimension's
        // advertised default (100) before calling the parser.
        var parsed = OgcVerticalSelectionParser.TryParseWmtsElevationValue("100", out var selection);

        parsed.Should().BeTrue();
        selection.Should().NotBeNull();
        selection!.Value.Min.Should().Be(100);
    }

    [UnitTest]
    public void TryParseWmtsElevationValue_WithEmpty_RecordsNothing()
    {
        var parsed = OgcVerticalSelectionParser.TryParseWmtsElevationValue(null, out var selection);

        parsed.Should().BeTrue();
        selection.Should().BeNull();
    }

    [UnitTest]
    public void TryParseWmtsElevationValue_WithNonNumeric_Fails()
    {
        var parsed = OgcVerticalSelectionParser.TryParseWmtsElevationValue("not-a-number", out var selection);

        parsed.Should().BeFalse();
        selection.Should().BeNull();
    }
}
