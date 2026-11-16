// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wmts;

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
        var selectionValue = selection!.Value;
        selectionValue.Min.Should().Be(200);
        selectionValue.Max.Should().Be(200);
        selectionValue.IsInstant.Should().BeTrue();
        selectionValue.RawValue.Should().Be("200");
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
