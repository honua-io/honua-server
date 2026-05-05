// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Helpers;

namespace Honua.Server.Tests.Features.Infrastructure.Helpers;

/// <summary>
/// Unit tests for the OGC temporal capability formatter introduced for ticket #379.
/// The formatter must preserve sub-second precision from the layer extent so the
/// timestamp advertised in WMS / WMTS capabilities (and resolved by
/// <c>time=default</c> / <c>time=current</c>) round-trips through the inclusive
/// Postgres comparison without dropping the row at the layer's actual maximum.
/// </summary>
public sealed class TemporalExtentHelpersTests
{
    [Fact]
    public void FormatOgcTemporalValue_WholeSecond_UsesSecondPrecision()
    {
        var value = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

        var formatted = TemporalExtentHelpers.FormatOgcTemporalValue(value);

        formatted.Should().Be("2024-06-15T12:00:00Z");
    }

    [Fact]
    public void FormatOgcTemporalValue_WithMilliseconds_PreservesFractionalSeconds()
    {
        // .NET DateTime ticks are 100ns; the canonical OGC format mirrors
        // OgcFeaturesUtilities.FormatTemporalValue and uses 7-digit precision
        // when sub-second ticks are present.
        var value = new DateTimeOffset(2024, 6, 15, 12, 0, 0, 123, TimeSpan.Zero);

        var formatted = TemporalExtentHelpers.FormatOgcTemporalValue(value);

        formatted.Should().Be("2024-06-15T12:00:00.1230000Z");
    }

    [Fact]
    public void FormatOgcTemporalValue_NonUtcOffset_NormalizesToUtc()
    {
        // Layer extents come from Postgres in DateTimeOffset; capabilities and
        // default/current resolution must always render as 'Z' so OGC clients
        // get a consistent canonical form regardless of source offset.
        var value = new DateTimeOffset(2024, 6, 15, 14, 30, 0, TimeSpan.FromHours(2));

        var formatted = TemporalExtentHelpers.FormatOgcTemporalValue(value);

        formatted.Should().Be("2024-06-15T12:30:00Z");
    }

    [Fact]
    public void FormatOgcTemporalValue_SubSecondTicks_PreservesPrecision()
    {
        // Direct tick construction emulates a Postgres microsecond timestamp
        // round-tripped through DateTimeOffset; sub-second precision must
        // survive the format step so a sub-second max is not silently
        // truncated to the prior whole second.
        var value = new DateTimeOffset(new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567));

        var formatted = TemporalExtentHelpers.FormatOgcTemporalValue(value);

        formatted.Should().Be("2024-06-15T12:00:00.1234567Z");
    }
}
