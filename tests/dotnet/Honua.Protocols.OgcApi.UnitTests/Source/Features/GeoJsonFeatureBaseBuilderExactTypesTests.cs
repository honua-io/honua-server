// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.GeoJson;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Trait("Category", "Unit")]
[Trait("Tier", "Fast")]
public sealed class GeoJsonFeatureBaseBuilderExactTypesTests
{
    private static readonly MetadataV2Object Resource = new()
    {
        SchemaFields =
        [
            new() { Name = "objectid", Type = MetadataV2FieldType.Integer, SemanticRoles = ["id.primary"] },
            new() { Name = "amount", Type = MetadataV2FieldType.Decimal },
            new() { Name = "observed", Type = MetadataV2FieldType.TimestampOffset },
        ]
    };

    [UnitTest]
    public void Create_DecimalText_BecomesRawJsonNumberWithEveryDigit()
    {
        var properties = Build("12345678901234567890123456789.50", "2024-01-01T14:00:00+02:00");

        var amount = properties["amount"].Should().BeOfType<JsonElement>().Subject;
        amount.ValueKind.Should().Be(JsonValueKind.Number);
        amount.GetRawText().Should().Be("12345678901234567890123456789.50");
    }

    [UnitTest]
    public void Create_OffsetTimestamp_BecomesUtcDateTime()
    {
        var properties = Build("1.5", "2024-01-01T14:00:00+02:00");

        properties["observed"].Should().Be("2024-01-01T12:00:00Z");
    }

    [UnitTest]
    public void Create_NonCanonicalDecimalText_FallsBackToDouble()
    {
        var properties = Build("1e3", null);

        properties["amount"].Should().Be(1000d);
        properties["observed"].Should().BeNull();
    }

    private static IReadOnlyDictionary<string, object?> Build(string amount, string? observed)
    {
        var attributes = new Dictionary<string, object?>
        {
            ["amount"] = amount,
            ["observed"] = observed
        }.ToImmutableDictionary();

        return GeoJsonFeatureBaseBuilder.Create(Feature.Create(1, null, attributes), Resource).Properties;
    }
}
