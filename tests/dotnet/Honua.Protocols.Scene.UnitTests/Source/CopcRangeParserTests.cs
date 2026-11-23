// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.Scene;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Scene;

[Protocol(TestProtocols.Scene)]
public sealed class CopcRangeParserTests
{
    [UnitTest]
    public void TryParseSingleByteRange_ValidExplicitRange_ReturnsExpectedBounds()
    {
        CopcRangeParser.TryParseSingleByteRange("bytes=0-374", 1000, out var start, out var end).Should().BeTrue();
        start.Should().Be(0);
        end.Should().Be(374);
    }

    [UnitTest]
    public void TryParseSingleByteRange_OpenEndedAndSuffixRanges_ClampCorrectly()
    {
        CopcRangeParser.TryParseSingleByteRange("bytes=500-", 1000, out var start, out var end).Should().BeTrue();
        start.Should().Be(500);
        end.Should().Be(999);

        CopcRangeParser.TryParseSingleByteRange("bytes=-128", 1000, out start, out end).Should().BeTrue();
        start.Should().Be(872);
        end.Should().Be(999);

        CopcRangeParser.TryParseSingleByteRange("bytes=900-5000", 1000, out start, out end).Should().BeTrue();
        start.Should().Be(900);
        end.Should().Be(999);
    }

    [UnitTest]
    public void TryParseSingleByteRange_InvalidOrMultiRangeHeaders_ReturnFalse()
    {
        CopcRangeParser.TryParseSingleByteRange("bytes=1000-1100", 1000, out _, out _).Should().BeFalse();
        CopcRangeParser.TryParseSingleByteRange("bytes=200-100", 1000, out _, out _).Should().BeFalse();
        CopcRangeParser.TryParseSingleByteRange("bytes=0-10, 20-30", 1000, out _, out _).Should().BeFalse();
        CopcRangeParser.TryParseSingleByteRange("items=0-10", 1000, out _, out _).Should().BeFalse();
        CopcRangeParser.TryParseSingleByteRange("bytes=-0", 1000, out _, out _).Should().BeFalse();
        CopcRangeParser.TryParseSingleByteRange("bytes=0-10", 0, out _, out _).Should().BeFalse();
    }
}
