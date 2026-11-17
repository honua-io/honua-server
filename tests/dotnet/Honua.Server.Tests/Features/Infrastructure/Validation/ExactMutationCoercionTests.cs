// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Validation;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Validation;

public sealed class ExactMutationCoercionTests
{
    [UnitTheory]
    [InlineData(ValidationExtensions.AttributeValidationMode.GeoServices)]
    [InlineData(ValidationExtensions.AttributeValidationMode.Json)]
    public void ValidateAttributes_JsonNumberForDecimal_StoresItsOwnDigits(ValidationExtensions.AttributeValidationMode mode)
    {
        var result = Resource(MetadataV2FieldType.Decimal).ValidateAttributesV2(
            new Dictionary<string, object?> { ["value"] = JsonDocument.Parse("1234.50").RootElement }, mode);

        result.IsValid.Should().BeTrue(result.ErrorMessage);
        result.Value!["value"].Should().Be("1234.50");
    }

    [UnitTheory]
    [InlineData(0.1d, "0.1")]
    [InlineData(-12d, "-12")]
    [InlineData(1e-7d, "0.0000001")]
    public void ValidateAttributes_GeoServicesDoubleForDecimal_StoresCanonicalText(double number, string expected)
    {
        var result = Resource(MetadataV2FieldType.Decimal).ValidateAttributesV2(
            new Dictionary<string, object?> { ["value"] = number }, ValidationExtensions.AttributeValidationMode.GeoServices);

        result.IsValid.Should().BeTrue(result.ErrorMessage);
        result.Value!["value"].Should().Be(expected);
    }

    [UnitTheory]
    [InlineData(ValidationExtensions.AttributeValidationMode.GeoServices)]
    [InlineData(ValidationExtensions.AttributeValidationMode.Json)]
    public void ValidateAttributes_EpochMillisecondsForTimestampOffset_StoresUtcText(ValidationExtensions.AttributeValidationMode mode)
    {
        var epoch = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var result = Resource(MetadataV2FieldType.TimestampOffset).ValidateAttributesV2(
            new Dictionary<string, object?> { ["value"] = epoch }, mode);

        result.IsValid.Should().BeTrue(result.ErrorMessage);
        result.Value!["value"].Should().Be("2024-01-01T12:00:00.000Z");
    }

    [UnitTest]
    public void ValidateAttributes_JsonExponentBelowDecimalRange_IsRejected()
    {
        var result = Resource(MetadataV2FieldType.Decimal).ValidateAttributesV2(
            new Dictionary<string, object?> { ["value"] = JsonDocument.Parse("1.5e-30").RootElement },
            ValidationExtensions.AttributeValidationMode.Json);

        result.IsValid.Should().BeFalse();
    }

    [UnitTest]
    public void ValidateAttributes_StrictMode_StillRejectsNumbersForExactFields()
    {
        var resource = Resource(MetadataV2FieldType.Decimal);

        resource.ValidateAttributesV2(new Dictionary<string, object?> { ["value"] = 1L }).IsValid.Should().BeFalse();
        Resource(MetadataV2FieldType.TimestampOffset)
            .ValidateAttributesV2(new Dictionary<string, object?> { ["value"] = 0L }).IsValid.Should().BeFalse();
    }

    [UnitTest]
    public void ValidateAttributes_GeoServicesOutOfRangeEpoch_IsRejected()
    {
        var result = Resource(MetadataV2FieldType.TimestampOffset).ValidateAttributesV2(
            new Dictionary<string, object?> { ["value"] = long.MaxValue }, ValidationExtensions.AttributeValidationMode.GeoServices);

        result.IsValid.Should().BeFalse();
    }

    private static MetadataV2Object Resource(MetadataV2FieldType type) => new()
    {
        SchemaFields = [new() { Name = "value", Type = type, Nullable = true }]
    };
}
