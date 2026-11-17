// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class GeoServicesExactFieldConventionsTests
{
    [UnitTheory]
    [InlineData(MetadataV2FieldType.Decimal, "esriFieldTypeDouble", "sqlTypeNumeric")]
    [InlineData(MetadataV2FieldType.TimestampOffset, "esriFieldTypeDate", "sqlTypeOther")]
    public void ExactTypes_MapToEsriDoubleAndDate(MetadataV2FieldType type, string fieldType, string sqlType)
    {
        GeoServicesFieldConventions.MapFieldType(type).Should().Be(fieldType);
        GeoServicesFieldConventions.MapSqlType(type).Should().Be(sqlType);
    }

    [UnitTest]
    public void TryConvertTemporalValue_DecimalText_BecomesInvariantDouble()
    {
        GeoServicesFieldConventions.TryConvertTemporalValue("-1234.500", MetadataV2FieldType.Decimal, out var converted)
            .Should().BeTrue();
        converted.Should().Be(-1234.5);
        GeoServicesFieldConventions.TryConvertTemporalValue("NaN", MetadataV2FieldType.Decimal, out _)
            .Should().BeFalse();
    }

    [UnitTest]
    public void TryConvertTemporalValue_OffsetTimestamp_BecomesEpochMilliseconds()
    {
        GeoServicesFieldConventions.TryConvertTemporalValue(
                "2024-01-01T14:00:00+02:00", MetadataV2FieldType.TimestampOffset, out var converted)
            .Should().BeTrue();
        converted.Should().Be(new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    [UnitTest]
    public void ResolveTemporalFieldTypes_IncludesExactFields()
    {
        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "exact", Name = "Exact" },
            Type = MetadataV2ObjectType.Table,
            SchemaFields =
            [
                new MetadataV2Field { Name = "amount", Type = MetadataV2FieldType.Decimal },
                new MetadataV2Field { Name = "observed", Type = MetadataV2FieldType.TimestampOffset },
                new MetadataV2Field { Name = "ratio", Type = MetadataV2FieldType.Double }
            ]
        };

        GeoServicesFieldConventions.ResolveTemporalFieldTypes(resource).Keys
            .Should().BeEquivalentTo("amount", "observed");
    }
}
