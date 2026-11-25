// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;

namespace Honua.Worker.Gdal.Tests;

public sealed class BoundedProcessOutputBufferTests
{
    [UnitTest]
    public void AppendLine_WithinLimit_PreservesAllLinesWithoutTruncation()
    {
        var buffer = new BoundedProcessOutputBuffer(maxChars: 128);

        buffer.AppendLine("line-1");
        buffer.AppendLine("line-2");
        buffer.AppendLine(null);

        buffer.Truncated.Should().BeFalse();
        buffer.ToString().Should().Be($"line-1{Environment.NewLine}line-2{Environment.NewLine}");
    }

    [UnitTest]
    public void AppendLine_WhenOutputExceedsLimit_TruncatesWithMarkerAndBoundsLength()
    {
        const int limit = 64;
        var buffer = new BoundedProcessOutputBuffer(maxChars: limit);

        for (var i = 0; i < 1000; i++)
        {
            buffer.AppendLine("0123456789abcdef0123456789abcdef");
        }

        buffer.Truncated.Should().BeTrue();
        var text = buffer.ToString();
        text.Should().EndWith(BoundedProcessOutputBuffer.TruncationMarker);
        text.Length.Should().BeLessThanOrEqualTo(
            limit + Environment.NewLine.Length + BoundedProcessOutputBuffer.TruncationMarker.Length);
    }

    [UnitTest]
    public void AppendLine_WithoutTruncationMarker_OmitsMarkerAndBoundsLength()
    {
        const int limit = 64;
        var buffer = BoundedProcessOutputBuffer.CreateWithoutTruncationMarker(maxChars: limit);

        for (var i = 0; i < 1000; i++)
        {
            buffer.AppendLine("{\"key\":\"0123456789abcdef0123456789abcdef\"}");
        }

        buffer.Truncated.Should().BeTrue();
        var text = buffer.ToString();
        text.Should().NotContain(BoundedProcessOutputBuffer.TruncationMarker);
        text.Length.Should().BeLessThanOrEqualTo(limit);
    }
}
