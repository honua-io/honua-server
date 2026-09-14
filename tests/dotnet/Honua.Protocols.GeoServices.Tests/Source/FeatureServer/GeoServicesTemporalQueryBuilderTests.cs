// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Unit tests for <see cref="GeoServicesTemporalQueryBuilder"/>. Per honua's documented
/// temporal-animation contract (docs/gis/temporal-animation-api.md), a non-empty
/// <c>time</c> parameter supplied against a layer that is not time-enabled is REJECTED
/// with an <see cref="ArgumentException"/> (HTTP 400) — an intentional, documented
/// divergence from Esri's lenient "ignore time" behavior (issue #1444).
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
public sealed class GeoServicesTemporalQueryBuilderTests
{
    [UnitTest]
    [Operation(Operations.Query)]
    public void BuildTemporalExpression_NonTimeEnabledLayer_RejectsTimeWithArgumentException()
    {
        // Per honua's documented temporal-animation contract (#1444), a non-empty time=
        // filter against a non-time-enabled layer is rejected with an ArgumentException
        // (mapped to HTTP 400) rather than silently ignored.
        var resource = BuildNonTemporalResource();
        var time = ((DateTimeOffset)new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc))
            .ToUnixTimeMilliseconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        var act = () => GeoServicesTemporalQueryBuilder.BuildTemporalExpression(
            time, timeRelation: null, resource);

        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void BuildTemporalExpression_NonTimeEnabledLayer_TimeRange_RejectsWithArgumentException()
    {
        var resource = BuildNonTemporalResource();

        var act = () => GeoServicesTemporalQueryBuilder.BuildTemporalExpression(
            "0,86400000", timeRelation: "esriTimeRelationOverlaps", resource);

        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void BuildTemporalExpression_TimeEnabledLayer_ProducesPredicate()
    {
        // Control: a time-enabled layer still produces a real temporal predicate.
        var resource = BuildTemporalResource();

        var expression = GeoServicesTemporalQueryBuilder.BuildTemporalExpression(
            "0,86400000", timeRelation: null, resource);

        expression.Should().NotBeNull();
    }

    [Theory]
    [InlineData("esriTimeRelationAfterStartTime")]
    [InlineData("esriTimeRelationBeforeStartTime")]
    [InlineData("esriTimeRelationAfterEndTime")]
    [InlineData("esriTimeRelationBeforeEndTime")]
    [InlineData("esriTimeRelationOverlaps")]
    [InlineData("esriTimeRelationOverlapsStartWithinEnd")]
    [Operation(Operations.Query)]
    public void BuildTemporalExpression_StandardEsriTimeRelations_ProduceAPredicate(string timeRelation)
    {
        // All six standard Esri start/end-relative timeRelation spellings must map onto the
        // interval engine and produce a real predicate (not throw ArgumentException -> 400).
        var resource = BuildTemporalResource();

        var expression = GeoServicesTemporalQueryBuilder.BuildTemporalExpression(
            "0,86400000", timeRelation, resource);

        expression.Should().NotBeNull();
    }

    [Theory]
    [InlineData("afterStartTime")]
    [InlineData("beforeStartTime")]
    [InlineData("afterEndTime")]
    [InlineData("beforeEndTime")]
    [Operation(Operations.Query)]
    public void BuildTemporalExpression_StartEndRelativeShortSpellings_AreAccepted(string timeRelation)
    {
        // The short (non-"esriTimeRelation"-prefixed) spellings are also accepted, mirroring
        // the existing case-insensitive vocabulary; none should throw ArgumentException.
        var resource = BuildTemporalResource();

        var act = () => GeoServicesTemporalQueryBuilder.BuildTemporalExpression(
            "0,86400000", timeRelation, resource);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("[1672527600000, 1728950400000]", "1672527600000,1728950400000", true, true)]
    [InlineData("[1672527600000,1728950400000]", "1672527600000,1728950400000", true, true)]
    [InlineData(" [ 1672527600000 , 1728950400000 ] ", "1672527600000,1728950400000", true, true)]
    [InlineData("[null, 1728950400000]", "null,1728950400000", false, true)]
    [InlineData("[1672527600000, null]", "1672527600000,null", true, false)]
    [InlineData("[\"2022-12-31T23:00:00Z\", \"2024-10-15T00:00:00Z\"]", "2022-12-31T23:00:00Z,2024-10-15T00:00:00Z", true, true)]
    [Operation(Operations.Query)]
    public void TryParseTimeParameter_BracketedExtent_ParsesToSameBoundsAsPlainExtent(
        string bracketed,
        string plain,
        bool hasStart,
        bool hasEnd)
    {
        // The ArcGIS API for Python sends a layer's timeInfo.timeExtent as a JSON array (#4782).
        // 1672527600000 ms is 2022-12-31T23:00:00Z (2023-01-01T00:00:00Z = 1672531200000, less
        // one hour) and 1728950400000 ms is 2024-10-15T00:00:00Z (2024-01-01 = 1704067200000,
        // plus 288 days).
        var expectedStart = hasStart ? new DateTimeOffset(2022, 12, 31, 23, 0, 0, TimeSpan.Zero) : (DateTimeOffset?)null;
        var expectedEnd = hasEnd ? new DateTimeOffset(2024, 10, 15, 0, 0, 0, TimeSpan.Zero) : (DateTimeOffset?)null;

        GeoServicesTemporalQueryBuilder.TryParseTimeParameter(bracketed, out var bracketedStart, out var bracketedEnd)
            .Should().BeTrue();
        GeoServicesTemporalQueryBuilder.TryParseTimeParameter(plain, out var plainStart, out var plainEnd)
            .Should().BeTrue();

        bracketedStart.Should().Be(expectedStart);
        bracketedEnd.Should().Be(expectedEnd);
        plainStart.Should().Be(expectedStart);
        plainEnd.Should().Be(expectedEnd);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseTimeParameter_BracketedNullExtent_IsTheNoFilterForm()
    {
        GeoServicesTemporalQueryBuilder.TryParseTimeParameter("[null, null]", out var start, out var end)
            .Should().BeTrue();

        start.Should().BeNull();
        end.Should().BeNull();
    }

    [Theory]
    [InlineData("[1672527600000, 1728950400000")]
    [InlineData("1672527600000, 1728950400000]")]
    [InlineData("[]")]
    [InlineData("[1672527600000]")]
    [InlineData("[1672527600000 1728950400000]")]
    [InlineData("[1672527600000, 1700000000000, 1728950400000]")]
    [InlineData("[[1672527600000, 1728950400000]]")]
    [InlineData("[1728950400000, 1672527600000]")]
    [InlineData("[yesterday, 1728950400000]")]
    [InlineData("[\"2022-12-31T23:00:00Z, 1728950400000]")]
    [InlineData("[,]")]
    [InlineData("[null,]")]
    [InlineData("[, 1728950400000]")]
    [InlineData("[\"\", 1728950400000]")]
    [Operation(Operations.Query)]
    public void TryParseTimeParameter_MalformedBracketedExtent_IsRejected(string time)
    {
        GeoServicesTemporalQueryBuilder.TryParseTimeParameter(time, out _, out _)
            .Should().BeFalse();

        var act = () => GeoServicesTemporalQueryBuilder.BuildTemporalExpression(
            time, timeRelation: null, BuildTemporalResource());

        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void BuildTemporalExpression_BracketedExtent_BuildsSamePredicateAsPlainExtent()
    {
        var resource = BuildTemporalResource();

        var bracketed = GeoServicesTemporalQueryBuilder.BuildTemporalExpression(
            "[1672527600000, 1728950400000]", timeRelation: null, resource);
        var plain = GeoServicesTemporalQueryBuilder.BuildTemporalExpression(
            "1672527600000,1728950400000", timeRelation: null, resource);

        bracketed.Should().NotBeNull();
        bracketed.Should().BeEquivalentTo(plain, options => options.RespectingRuntimeTypes());
    }

    private static MetadataV2Resource BuildNonTemporalResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-non-temporal", Name = "plain_layer" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields =
            [
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
            ],
            // No Temporal configuration: the layer is not time-enabled.
        };

    private static MetadataV2Resource BuildTemporalResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-temporal", Name = "time_layer" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields =
            [
                new MetadataV2Field { Name = "start_time", Type = MetadataV2FieldType.DateTime },
            ],
            Temporal = new MetadataV2ResourceTemporal
            {
                StartTimeField = "start_time",
            },
        };
}
