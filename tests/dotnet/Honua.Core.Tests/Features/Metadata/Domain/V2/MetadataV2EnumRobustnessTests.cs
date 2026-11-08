// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Scene.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

public sealed class MetadataV2EnumRobustnessTests
{
    private static readonly Type[] EnumTypes =
    [
        typeof(MetadataV2ResourceType), typeof(MetadataV2ConnectionType),
        typeof(MetadataV2StorageType), typeof(MetadataV2ServiceType),
        typeof(MetadataV2PublicationType), typeof(MetadataV2StorageBindingCapability),
        typeof(MetadataV2GeometryType), typeof(MetadataV2FieldType),
        typeof(MetadataV2AttributeRuleType), typeof(Symbology3DComparison)
    ];

    public static TheoryData<Type, string> UnknownEnumCases()
    {
        var cases = new TheoryData<Type, string>();
        foreach (var type in EnumTypes)
        {
            foreach (var json in new[] { "\"future-value\"", "\"\"", "987", "-987", "2147483648", "-2147483649", "9223372036854775808", "\"987\"" })
            {
                cases.Add(type, json);
            }
        }
        return cases;
    }

    public static TheoryData<Type, string> MalformedEnumCases()
    {
        var cases = new TheoryData<Type, string>();
        foreach (var type in EnumTypes)
        {
            foreach (var json in new[] { "true", "false", "null", "{}", "[]", "1.5" })
            {
                cases.Add(type, json);
            }
        }
        return cases;
    }

    [UnitTheory]
    [MemberData(nameof(UnknownEnumCases))]
    public void Deserialize_UnknownEnumToken_UsesUnknownSentinel(Type enumType, string json)
    {
        var typeInfo = MetadataV2JsonContext.Default.GetTypeInfo(enumType)!;
        typeInfo.Should().NotBeNull();
        var value = JsonSerializer.Deserialize(json, typeInfo);

        value.Should().NotBeNull();
        value!.ToString().Should().Be("Unknown");
        var serialized = JsonSerializer.Serialize(value, typeInfo);
        var reloaded = JsonSerializer.Deserialize(serialized, typeInfo);
        reloaded.Should().Be(value);
    }

    [UnitTheory]
    [MemberData(nameof(MalformedEnumCases))]
    public void Deserialize_MalformedEnumToken_RejectsValue(Type enumType, string json)
    {
        var typeInfo = MetadataV2JsonContext.Default.GetTypeInfo(enumType)!;
        var deserialize = () => JsonSerializer.Deserialize(json, typeInfo);

        deserialize.Should().Throw<JsonException>();
    }

    [UnitTheory]
    [InlineData(typeof(MetadataV2ResourceType), "\"feature-dataset\"", 0)]
    [InlineData(typeof(MetadataV2ResourceType), "\"etl-pipeline\"", 14)]
    [InlineData(typeof(MetadataV2ConnectionType), "\"database\"", 0)]
    [InlineData(typeof(MetadataV2ConnectionType), "\"managed\"", 5)]
    [InlineData(typeof(MetadataV2StorageType), "\"relational-table\"", 0)]
    [InlineData(typeof(MetadataV2StorageType), "\"stac-asset\"", 15)]
    [InlineData(typeof(MetadataV2ServiceType), "\"ogc-api-features\"", 0)]
    [InlineData(typeof(MetadataV2ServiceType), "\"custom\"", 12)]
    [InlineData(typeof(MetadataV2PublicationType), "\"ogc-collection\"", 0)]
    [InlineData(typeof(MetadataV2PublicationType), "\"custom\"", 13)]
    [InlineData(typeof(MetadataV2StorageBindingCapability), "\"query\"", 0)]
    [InlineData(typeof(MetadataV2StorageBindingCapability), "\"search\"", 9)]
    [InlineData(typeof(MetadataV2GeometryType), "\"none\"", 0)]
    [InlineData(typeof(MetadataV2GeometryType), "\"mixed\"", 8)]
    [InlineData(typeof(MetadataV2FieldType), "\"unknown\"", 0)]
    [InlineData(typeof(MetadataV2FieldType), "\"geography\"", 14)]
    public void Deserialize_KnownWireValue_PreservesNumericAssignment(Type enumType, string json, int expected)
    {
        var typeInfo = MetadataV2JsonContext.Default.GetTypeInfo(enumType)!;
        var value = JsonSerializer.Deserialize(json, typeInfo)!;
        Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture).Should().Be(expected);
        JsonSerializer.Serialize(value, typeInfo).Should().Be(json);
        JsonSerializer.Deserialize(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), typeInfo)
            .Should().Be(value);
    }

    [UnitTheory]
    [InlineData(typeof(MetadataV2ResourceType), "\"FEATURE-DATASET\"", 0)]
    [InlineData(typeof(MetadataV2ConnectionType), "\"DATABASE\"", 0)]
    public void Deserialize_MixedCaseWireValue_PreservesKnownMeaning(Type enumType, string json, int expected)
    {
        var value = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.GetTypeInfo(enumType)!);

        Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture).Should().Be(expected);
    }

    [UnitTheory]
    [InlineData(typeof(MetadataV2AttributeRuleType), "0")]
    [InlineData(typeof(MetadataV2AttributeRuleType), "3")]
    [InlineData(typeof(Symbology3DComparison), "0")]
    [InlineData(typeof(Symbology3DComparison), "5")]
    public void Serialize_KnownNumericEnum_PreservesNumericEncoding(Type enumType, string json)
    {
        var typeInfo = MetadataV2JsonContext.Default.GetTypeInfo(enumType)!;
        var value = JsonSerializer.Deserialize(json, typeInfo);

        JsonSerializer.Serialize(value, typeInfo).Should().Be(json);
    }
}
