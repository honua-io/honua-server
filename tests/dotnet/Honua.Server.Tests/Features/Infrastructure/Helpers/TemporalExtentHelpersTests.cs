// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Helpers;
using NSubstitute;

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

    [Fact]
    public async Task TryResolveTemporalRangeAsync_EndExtentNull_FallsBackToStartExtentEnd()
    {
        // Regression for #379: when EndTimeField is configured but every row
        // has a null end, the Postgres reader returns null for that field's
        // extent. Without the fallback, max collapses to null even though the
        // start field has valid latest values, so temporalExtent /
        // capabilities <Default> would lose the layer's actual maximum.
        var layer = BuildLayerWithIntervalTimeInfo();
        var startMax = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero);
        var startMin = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var reader = Substitute.For<IFeatureReader>();
        reader.GetTemporalExtentAsync(layer.Id, "start_time", FieldType.DateTime, Arg.Any<CancellationToken>())
            .Returns(TemporalExtentResult.Create(startMin, startMax));
        reader.GetTemporalExtentAsync(layer.Id, "end_time", FieldType.DateTime, Arg.Any<CancellationToken>())
            .Returns((TemporalExtentResult?)null);

        var range = await TemporalExtentHelpers.TryResolveTemporalRangeAsync(
            layer, reader, CancellationToken.None);

        range.Should().NotBeNull();
        range!.Value.Min.Should().Be(startMin);
        range.Value.Max.Should().Be(startMax);
        range.Value.HasExtent.Should().BeTrue();
    }

    [Fact]
    public async Task TryResolveTemporalRangeAsync_EndExtentPopulated_PrefersEndExtentEnd()
    {
        // Sanity check: when both extents resolve, the canonical max is the
        // configured end column's max (interval-style layer where end > start).
        var layer = BuildLayerWithIntervalTimeInfo();
        var startMin = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var startMax = new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var endMax = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero);

        var reader = Substitute.For<IFeatureReader>();
        reader.GetTemporalExtentAsync(layer.Id, "start_time", FieldType.DateTime, Arg.Any<CancellationToken>())
            .Returns(TemporalExtentResult.Create(startMin, startMax));
        reader.GetTemporalExtentAsync(layer.Id, "end_time", FieldType.DateTime, Arg.Any<CancellationToken>())
            .Returns(TemporalExtentResult.Create(startMin, endMax));

        var range = await TemporalExtentHelpers.TryResolveTemporalRangeAsync(
            layer, reader, CancellationToken.None);

        range.Should().NotBeNull();
        range!.Value.Min.Should().Be(startMin);
        range.Value.Max.Should().Be(endMax);
    }

    [Fact]
    public async Task TryResolveTemporalRangeAsync_ProviderThrowsNotSupported_ReturnsNull()
    {
        // Read-only providers (MySQL/MariaDB, SQL Server) throw
        // NotSupportedException from GetTemporalExtentAsync. The shared
        // helper must catch that and return null so capabilities and the
        // temporalExtent endpoint fall back to their non-time-aware
        // contract (omit time dimension, return 404) instead of escaping
        // a 500 to the public surface (#379).
        var layer = BuildLayerWithIntervalTimeInfo();
        var reader = Substitute.For<IFeatureReader>();
        reader.GetTemporalExtentAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<FieldType>(),
                Arg.Any<CancellationToken>())
            .Returns<TemporalExtentResult?>(_ => throw new NotSupportedException(
                "Temporal extent queries are not supported by the test provider."));

        var range = await TemporalExtentHelpers.TryResolveTemporalRangeAsync(
            layer, reader, CancellationToken.None);

        range.Should().BeNull();
    }

    [Fact]
    public async Task TryResolveTemporalRangeAsync_EndExtentThrowsNotSupported_ReturnsNull()
    {
        // Same contract on the second extent fetch: even when start
        // succeeds, an end-field NotSupportedException must be swallowed
        // and surfaced as "no extent available" so the layer falls back
        // to non-time-aware behavior consistently.
        var layer = BuildLayerWithIntervalTimeInfo();
        var startMin = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var startMax = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero);

        var reader = Substitute.For<IFeatureReader>();
        reader.GetTemporalExtentAsync(layer.Id, "start_time", FieldType.DateTime, Arg.Any<CancellationToken>())
            .Returns(TemporalExtentResult.Create(startMin, startMax));
        reader.GetTemporalExtentAsync(layer.Id, "end_time", FieldType.DateTime, Arg.Any<CancellationToken>())
            .Returns<TemporalExtentResult?>(_ => throw new NotSupportedException("end-field extent unsupported"));

        var range = await TemporalExtentHelpers.TryResolveTemporalRangeAsync(
            layer, reader, CancellationToken.None);

        range.Should().BeNull();
    }

    private static LayerDefinition BuildLayerWithIntervalTimeInfo()
    {
        var fields = new[]
        {
            new FieldDefinition("start_time", FieldType.DateTime),
            new FieldDefinition("end_time", FieldType.DateTime)
        };

        return new LayerDefinition(
            Id: 0,
            Name: "interval_layer",
            Description: null,
            GeometryType: GeometryType.None,
            SpatialReference: SpatialReference.WGS84,
            Fields: fields,
            Metadata: new CatalogMetadata
            {
                TimeInfo = new LayerTimeInfo
                {
                    StartTimeField = "start_time",
                    EndTimeField = "end_time"
                }
            });
    }
}
