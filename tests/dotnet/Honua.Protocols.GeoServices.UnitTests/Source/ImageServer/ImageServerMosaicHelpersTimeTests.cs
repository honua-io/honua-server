// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Unit tests for the ImageServer <c>time</c> parser (#4061). Esri clients send epoch-millisecond
/// instants and <c>start,end</c> extents (the forms the service advertises in
/// <c>timeInfo.timeExtent</c>); every ImageServer operation shares this parser.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerMosaicHelpersTimeTests
{
    // 2024-01-01T00:00:00Z is 1704067200000 ms and each day adds 86400000 ms, so
    // 2024-01-10 is 1704844800000 and 2024-01-20 is 1705708800000.
    private const string Jan10Ms = "1704844800000";
    private const string Jan20Ms = "1705708800000";
    private static readonly DateTimeOffset Jan10 = new(2024, 1, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Jan20 = new(2024, 1, 20, 0, 0, 0, TimeSpan.Zero);

    [UnitTest]
    public void TryParseTime_EpochMillisecondInstant_ReturnsInstant()
    {
        ImageServerMosaicHelpers.TryParseTime(Jan20Ms, out var timestamp, out var timeStart, out var error)
            .Should().BeTrue(error ?? string.Empty);

        timestamp.Should().Be(Jan20);
        timeStart.Should().BeNull();
    }

    [UnitTest]
    public void TryParseTime_IsoInstant_ReturnsInstant()
    {
        ImageServerMosaicHelpers.TryParseTime("2024-01-20T00:00:00Z", out var timestamp, out var timeStart, out var error)
            .Should().BeTrue(error ?? string.Empty);

        timestamp.Should().Be(Jan20);
        timeStart.Should().BeNull();
    }

    [UnitTest]
    public void TryParseTime_EpochMillisecondExtent_ReturnsStartAndEnd()
    {
        ImageServerMosaicHelpers.TryParseTime($"{Jan10Ms},{Jan20Ms}", out var timestamp, out var timeStart, out var error)
            .Should().BeTrue(error ?? string.Empty);

        timeStart.Should().Be(Jan10);
        timestamp.Should().Be(Jan20);
    }

    [UnitTest]
    public void TryParseTime_BracketedEpochMillisecondExtent_ReturnsStartAndEnd()
    {
        // The ArcGIS API for Python sends timeInfo.timeExtent as a JSON array (#4782).
        ImageServerMosaicHelpers.TryParseTime($"[{Jan10Ms}, {Jan20Ms}]", out var timestamp, out var timeStart, out var error)
            .Should().BeTrue(error ?? string.Empty);

        timeStart.Should().Be(Jan10);
        timestamp.Should().Be(Jan20);
    }

    [UnitTest]
    public void TryParseTime_OpenEndedExtent_ReturnsStartOnly()
    {
        ImageServerMosaicHelpers.TryParseTime($"{Jan10Ms},null", out var timestamp, out var timeStart, out var error)
            .Should().BeTrue(error ?? string.Empty);

        timeStart.Should().Be(Jan10);
        timestamp.Should().BeNull();
    }

    [UnitTest]
    public void TryParseTime_NoFilterForms_ReturnNoBounds()
    {
        foreach (var value in new[] { null, string.Empty, "null", "NULL", "null,null" })
        {
            ImageServerMosaicHelpers.TryParseTime(value, out var timestamp, out var timeStart, out var error)
                .Should().BeTrue($"'{value}' means no temporal filter");

            timestamp.Should().BeNull();
            timeStart.Should().BeNull();
            error.Should().BeNull();
        }
    }

    [UnitTest]
    public void TryParseTime_ReversedExtent_IsRejected()
    {
        ImageServerMosaicHelpers.TryParseTime($"{Jan20Ms},{Jan10Ms}", out _, out _, out var error)
            .Should().BeFalse();

        error.Should().Contain($"{Jan20Ms},{Jan10Ms}");
    }

    [UnitTest]
    public void TryParseTime_UnparseableValue_IsRejected()
    {
        ImageServerMosaicHelpers.TryParseTime("yesterday", out _, out _, out var error)
            .Should().BeFalse();

        error.Should().Contain("yesterday");
    }
}
