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
