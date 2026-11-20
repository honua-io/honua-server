// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
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

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Decimal, "-1234.500")]
    [InlineData(MetadataV2FieldType.DateTime, "2024-01-01T14:00:00+02:00")]
    [InlineData(MetadataV2FieldType.TimestampOffset, 1_704_110_400_000L)]
    [InlineData(MetadataV2FieldType.Date, "2024-06-15T23:30:00Z")]
    [InlineData(MetadataV2FieldType.Date, "not a date")]
    public void TryWriteTemporalValue_WritesTheSerializedConvertedValue(MetadataV2FieldType type, object value)
    {
        WriteProperty(writer =>
            {
                if (GeoServicesFieldConventions.TryWriteTemporalValue(writer, new TemporalAttribute("value", value, type)))
                {
                    return;
                }
                writer.WriteNull("value");
            })
            .Should().Be(WriteProperty(writer =>
            {
                writer.WritePropertyName("value");
                var converted = GeoServicesFieldConventions.TryConvertTemporalValue(value, type, out var result) ? result : null;
                JsonSerializer.Serialize(writer, converted, FeatureServerJsonContext.Default.Object);
            }));
    }

    private static string WriteProperty(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
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

    [UnitTest]
    public void StatisticsAndClassBreaks_TreatDecimalAsNumeric()
    {
        var resource = new MetadataV2Object
        {
            SchemaFields = [new MetadataV2Field { Name = "amount", Type = MetadataV2FieldType.Decimal }]
        };
        object?[] arguments = [resource, "amount", null];

        Invoke("IsNumericFieldType", MetadataV2FieldType.Decimal).Should().Be(true);
        Invoke("TryResolveNumericField", arguments).Should().Be(true);
        arguments[2].Should().Be(MetadataV2FieldType.Decimal);
    }

    private static object? Invoke(string method, params object?[] arguments)
        => typeof(FeatureServerQueryHandler)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, arguments);
}
