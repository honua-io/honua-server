// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Validation;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Validation;

/// <summary>
/// GeoServices wire integers must not change the canonical Boolean schema type.
/// Strict adapters must continue rejecting those numeric Boolean representations.
/// </summary>
public sealed class BooleanMutationValidationTests
{
    [UnitTheory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("0.0", false)]
    [InlineData("1.0", true)]
    public void ValidateAttributes_GeoServicesNumericBoolean_PreservesDeclaredTypes(string json, bool expected)
    {
        var resource = Resource();
        using var document = JsonDocument.Parse(json);
        var attributes = new Dictionary<string, object?>
        {
            ["active"] = document.RootElement.Clone(),
            ["rank"] = 1L
        };
        var result = resource.ValidateAttributesV2(attributes, ValidationExtensions.AttributeValidationMode.GeoServices);
        result.IsValid.Should().BeTrue(result.ErrorMessage);
        result.Value!["active"].Should().BeOfType<bool>().Which.Should().Be(expected);
        result.Value["rank"].Should().BeOfType<long>().Which.Should().Be(1);
    }

    [UnitTheory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("0.0")]
    [InlineData("1.0")]
    public void ValidateAttributes_StrictNumericBoolean_RemainsRejected(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = Resource().ValidateAttributesV2(
            new Dictionary<string, object?> { ["active"] = document.RootElement.Clone() });
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("must be a boolean");
    }

    [UnitTheory]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("0.5")]
    [InlineData("\"0\"")]
    public void ValidateAttributes_GeoServicesInvalidBoolean_RemainsRejected(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = Resource().ValidateAttributesV2(
            new Dictionary<string, object?> { ["active"] = document.RootElement.Clone() },
            ValidationExtensions.AttributeValidationMode.GeoServices);
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("must be a boolean");
    }

    [UnitTheory]
    [InlineData("true", true, ValidationExtensions.AttributeValidationMode.Strict)]
    [InlineData("false", false, ValidationExtensions.AttributeValidationMode.Strict)]
    [InlineData("null", null, ValidationExtensions.AttributeValidationMode.Strict)]
    [InlineData("true", true, ValidationExtensions.AttributeValidationMode.GeoServices)]
    [InlineData("false", false, ValidationExtensions.AttributeValidationMode.GeoServices)]
    [InlineData("null", null, ValidationExtensions.AttributeValidationMode.GeoServices)]
    public void ValidateAttributes_TypedBooleanAndNull_PreserveValues(
        string json, bool? expected, ValidationExtensions.AttributeValidationMode mode)
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields = [new() { Name = "active", Type = MetadataV2FieldType.Boolean, Nullable = true }]
        };
        using var document = JsonDocument.Parse(json);
        var result = resource.ValidateAttributesV2(
            new Dictionary<string, object?> { ["active"] = document.RootElement.Clone() }, mode);
        result.IsValid.Should().BeTrue(result.ErrorMessage);
        if (expected is { } value)
        {
            result.Value!["active"].Should().BeOfType<bool>().Which.Should().Be(value);
        }
        else
        {
            result.Value!["active"].Should().BeNull();
        }
    }

    private static MetadataV2Resource Resource() => new()
    {
        SchemaFields =
        [
            new() { Name = "active", Type = MetadataV2FieldType.Boolean, Nullable = false },
            new() { Name = "rank", Type = MetadataV2FieldType.Integer }
        ]
    };
}
