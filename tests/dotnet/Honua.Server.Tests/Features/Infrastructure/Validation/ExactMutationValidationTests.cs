// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Validation;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Validation;

public sealed class ExactMutationValidationTests
{
    [UnitTheory]
    [InlineData(MetadataV2FieldType.Decimal, "12345678901234567890.1000")]
    [InlineData(MetadataV2FieldType.Decimal, "-0.00")]
    [InlineData(MetadataV2FieldType.TimestampOffset, "2026-11-14T08:30:00.5000-08:00")]
    public void ValidateAttributes_ExactText_PreservesLexicalValue(MetadataV2FieldType type, string text)
    {
        var resource = Resource(type);
        var result = resource.ValidateAttributesV2(new Dictionary<string, object?> { ["value"] = text });
        result.IsValid.Should().BeTrue(result.ErrorMessage);
        result.Value!["value"].Should().Be(text);
    }

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Decimal, "1e5")]
    [InlineData(MetadataV2FieldType.Decimal, "1.00000000000000000000000000001")]
    [InlineData(MetadataV2FieldType.TimestampOffset, "2026-11-14T08:30:00")]
    [InlineData(MetadataV2FieldType.TimestampOffset, "2026-11-14T08:30:00+14:01")]
    public void ValidateAttributes_InvalidExactText_IsRejected(MetadataV2FieldType type, string text)
    {
        var result = Resource(type).ValidateAttributesV2(new Dictionary<string, object?> { ["value"] = text });
        result.IsValid.Should().BeFalse();
    }

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Decimal)]
    [InlineData(MetadataV2FieldType.TimestampOffset)]
    public void ValidateAttributes_BinaryNumberForExactField_IsRejected(MetadataV2FieldType type)
    {
        var result = Resource(type).ValidateAttributesV2(new Dictionary<string, object?> { ["value"] = 1d });
        result.IsValid.Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("0.1000000000000000000000000000", true)]
    [InlineData("0.1000000000000000000000000001", false)]
    [InlineData("-0.0000000000000000000000000001", false)]
    public void ValidateAttributes_DecimalRange_EnforcesExactBounds(string text, bool accepted)
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields = [new()
            {
                Name = "value", Type = MetadataV2FieldType.Decimal,
                Domain = new MetadataV2FieldDomain
                {
                    Type = "range",
                    Range = [JsonSerializer.SerializeToElement(0m), JsonSerializer.SerializeToElement(0.1m)]
                }
            }]
        };
        var result = resource.ValidateAttributesV2(new Dictionary<string, object?> { ["value"] = text });
        result.IsValid.Should().Be(accepted, result.ErrorMessage);
    }

    private static MetadataV2Resource Resource(MetadataV2FieldType type) => new()
    {
        SchemaFields = [new() { Name = "value", Type = type, Nullable = true }]
    };
}
