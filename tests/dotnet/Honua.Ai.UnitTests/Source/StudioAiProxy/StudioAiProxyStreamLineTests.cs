// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Ai.StudioAiProxy.Adapters;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.StudioAiProxy;

[Protocol(TestProtocols.TestQuality)]
public sealed class StudioAiProxyStreamLineTests
{
    [UnitTest]
    public async Task ReadBoundedLineAsync_LinesUnderTheCap_ReadLikeReadLine()
    {
        using var reader = Reader("data: one\ndata: two\r\n\nlast");

        var lines = await ReadAllAsync(reader, maximumChars: 16);

        lines.Should().Equal("data: one", "data: two", string.Empty, string.Empty, "last");
    }

    [UnitTest]
    public async Task ReadBoundedLineAsync_LineAtTheCap_IsReturned()
    {
        using var reader = Reader(new string('x', 8) + "\nnext");

        var line = await StudioAiProxyHttpStreamHelpers.ReadBoundedLineAsync(reader, 8, CancellationToken.None);

        line.Should().Be(new string('x', 8));
    }

    [UnitTest]
    public async Task ReadBoundedLineAsync_LineOverTheCap_ThrowsIOException()
    {
        using var reader = Reader(new string('x', 9) + "\n");

        var act = () => StudioAiProxyHttpStreamHelpers.ReadBoundedLineAsync(reader, 8, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
    }

    [UnitTest]
    public async Task ReadLineSafeAsync_LineOverTheStreamCap_ReturnsReadFailure()
    {
        using var reader = Reader(new string('x', StudioAiProxyHttpStreamHelpers.MaximumStreamLineChars + 1));

        var (line, timedOut, ioError) = await StudioAiProxyHttpStreamHelpers.ReadLineSafeAsync(
            reader, CancellationToken.None, CancellationToken.None);

        line.Should().BeNull();
        timedOut.Should().BeFalse();
        ioError.Should().NotBeNull();
    }

    private static StreamReader Reader(string content)
        => new(new MemoryStream(Encoding.UTF8.GetBytes(content)), Encoding.UTF8);

    private static async Task<List<string>> ReadAllAsync(StreamReader reader, int maximumChars)
    {
        List<string> lines = [];
        while (await StudioAiProxyHttpStreamHelpers.ReadBoundedLineAsync(reader, maximumChars, CancellationToken.None) is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }
}
