// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed class HavingConditionTypeTests
{
    private static readonly MetadataV2Resource Layer = new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "layer", Name = "layer" },
        SchemaFields =
        [
            new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger, Nullable = false },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
            new MetadataV2Field { Name = "population", Type = MetadataV2FieldType.Integer },
            new MetadataV2Field { Name = "created", Type = MetadataV2FieldType.DateTime },
            new MetadataV2Field { Name = "observed", Type = MetadataV2FieldType.Date },
            new MetadataV2Field { Name = "starts", Type = MetadataV2FieldType.Time },
            new MetadataV2Field { Name = "active", Type = MetadataV2FieldType.Boolean }
        ]
    };

    [UnitTest]
    public void TryParseHavingConditions_MaxOfStringComparedToNumber_Rejects()
    {
        var parsed = Parse("MAX(name) > 5", Stat(StatisticType.Max, "name", MetadataV2FieldType.String), out _, out var error);

        parsed.Should().BeFalse();
        error.Should().Contain("string field 'name'");
    }

    [UnitTest]
    public void TryParseHavingConditions_MaxOfStringComparedToQuotedString_BindsText()
    {
        var parsed = Parse(
            "MAX(name) > 'O''Brien'",
            Stat(StatisticType.Max, "name", MetadataV2FieldType.String),
            out var conditions,
            out var error);

        parsed.Should().BeTrue(error);
        conditions.Should().ContainSingle();
        conditions[0].TextValue.Should().Be("O'Brien");
        conditions[0].BoundValue().Should().Be("O'Brien");
    }

    [UnitTest]
    public void TryParseHavingConditions_AndInsideQuotedLiteral_DoesNotSplitTheLiteral()
    {
        var parsed = FeatureServerQueryHandler.TryParseHavingConditions(
            "MAX(name) > 'O''Brien AND Research AND Development' AND COUNT(population) >= 2",
            [
                Stat(StatisticType.Max, "name", MetadataV2FieldType.String),
                Stat(StatisticType.Count, "population", MetadataV2FieldType.Integer)
            ],
            Layer,
            out var conditions,
            out var error);

        parsed.Should().BeTrue(error);
        conditions.Should().HaveCount(2);
        conditions[0].TextValue.Should().Be("O'Brien AND Research AND Development");
        conditions[1].BoundValue().Should().Be(2d);
    }

    [UnitTest]
    public void TryParseHavingConditions_CountOfStringComparedToNumber_BindsDouble()
    {
        var parsed = Parse(
            "COUNT(name) > 5",
            Stat(StatisticType.Count, "name", MetadataV2FieldType.String),
            out var conditions,
            out var error);

        parsed.Should().BeTrue(error);
        conditions[0].BoundValue().Should().Be(5d);
    }

    [UnitTest]
    public void TryParseHavingConditions_MaxOfDateTimeEpoch_BindsTimestamp()
    {
        var parsed = Parse(
            "MAX(created) > 1609459200000",
            Stat(StatisticType.Max, "created", MetadataV2FieldType.DateTime),
            out var conditions,
            out var error);

        parsed.Should().BeTrue(error);
        conditions[0].TimestampValue.Should().Be(new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero));
        conditions[0].BoundValue().Should().BeOfType<DateTimeOffset>();
    }

    [UnitTest]
    public void TryParseHavingConditions_MaxOfDateTimeLiteral_BindsTimestamp()
    {
        var parsed = Parse(
            "MAX(created) >= timestamp '2021-01-01T00:00:00Z'",
            Stat(StatisticType.Max, "created", MetadataV2FieldType.DateTime),
            out var conditions,
            out var error);

        parsed.Should().BeTrue(error);
        conditions[0].TimestampValue.Should().Be(new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [UnitTest]
    public void TryParseHavingConditions_MaxOfDateEpoch_BindsDate()
    {
        var parsed = Parse(
            "MIN(observed) < 1609459200000",
            Stat(StatisticType.Min, "observed", MetadataV2FieldType.Date),
            out var conditions,
            out var error);

        parsed.Should().BeTrue(error);
        conditions[0].DateValue.Should().Be(new DateOnly(2021, 1, 1));
        conditions[0].BoundValue().Should().BeOfType<DateOnly>();
    }

    [UnitTest]
    public void TryParseHavingConditions_MaxOfTimeLiteral_BindsTime()
    {
        var parsed = Parse(
            "MAX(starts) <= time '14:30:00'",
            Stat(StatisticType.Max, "starts", MetadataV2FieldType.Time),
            out var conditions,
            out var error);

        parsed.Should().BeTrue(error);
        conditions[0].TimeValue.Should().Be(new TimeOnly(14, 30, 0));
    }

    [UnitTest]
    public void TryParseHavingConditions_MaxOfTimeNumber_Rejects()
    {
        var parsed = Parse("MAX(starts) > 5", Stat(StatisticType.Max, "starts", MetadataV2FieldType.Time), out _, out var error);

        parsed.Should().BeFalse();
        error.Should().Contain("time field 'starts'");
    }

    [UnitTest]
    public void TryParseHavingConditions_MaxOfBoolean_Rejects()
    {
        var parsed = Parse("MAX(active) > 5", Stat(StatisticType.Max, "active", MetadataV2FieldType.Boolean), out _, out var error);

        parsed.Should().BeFalse();
        error.Should().Contain("not supported");
    }

    [UnitTest]
    public void TryParseHavingConditions_MaxOfInteger_BindsDouble()
    {
        var parsed = Parse(
            "MAX(population) >= 1000.5",
            Stat(StatisticType.Max, "population", MetadataV2FieldType.Integer),
            out var conditions,
            out var error);

        parsed.Should().BeTrue(error);
        conditions[0].BoundValue().Should().Be(1000.5d);
    }

    private static bool Parse(
        string having,
        StatisticDefinition statistic,
        out ImmutableArray<HavingCondition> conditions,
        out string? error)
        => FeatureServerQueryHandler.TryParseHavingConditions(
            having,
            [statistic],
            Layer,
            out conditions,
            out error);

    private static StatisticDefinition Stat(StatisticType type, string field, MetadataV2FieldType fieldType)
        => new()
        {
            StatisticType = type,
            OnStatisticField = field,
            OutStatisticFieldName = "stat",
            FieldType = fieldType
        };
}
