// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Edit;

public sealed class FeatureAttributeValidatorTests
{
    [UnitTest]
    public void Strict_RejectsUnknownFieldsAndNumericBoolean_WithOrderedErrors()
    {
        var result = FeatureAttributeValidator.Validate(Resource(MetadataV2FieldType.Boolean), new Dictionary<string, object?>
        {
            ["unknown"] = 4, ["value"] = 1,
        });
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Multiple attribute validation errors: Unknown field 'unknown'.; Field 'value' must be a boolean.");
        result.Value.Should().BeNull();
    }

    [UnitTest]
    public void Compatibility_OmitsUnknownFieldsAndNormalizesJsonNumericBoolean()
    {
        var result = FeatureAttributeValidator.Validate(Resource(MetadataV2FieldType.Boolean), new Dictionary<string, object?>
        {
            ["unknown"] = 4, ["VALUE"] = JsonSerializer.SerializeToElement(1),
        }, new(AttributeValidationProfile.GeoServices));
        result.IsValid.Should().BeTrue(result.ErrorMessage);
        result.Value!.Should().HaveCount(1).And.Contain("value", true);
        result.Value["VALUE"].Should().Be(true);
    }

    [UnitTheory]
    [InlineData(AttributeValidationProfile.Strict)]
    [InlineData(AttributeValidationProfile.GeoServices)]
    public void Update_RejectsEditableBoundGlobalId(AttributeValidationProfile profile)
    {
        var resource = Resource(MetadataV2FieldType.Uuid) with
        {
            Editing = new() { GlobalIdField = "value" },
        };
        var result = FeatureAttributeValidator.Validate(resource, new Dictionary<string, object?> { ["VALUE"] = Guid.NewGuid() },
            new(profile, AttributeMutationOperation.Update));
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Field 'value' is not editable.");
    }

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Decimal, "12345678901234567890.1000")]
    [InlineData(MetadataV2FieldType.TimestampOffset, "2026-11-14T08:30:00.5000-08:00")]
    public void ExactValues_PreserveOriginalTextThroughJsonNormalization(MetadataV2FieldType type, string text)
    {
        var result = FeatureAttributeValidator.Validate(Resource(type), new Dictionary<string, object?>
        {
            ["value"] = JsonSerializer.SerializeToElement(text),
        });
        result.IsValid.Should().BeTrue(result.ErrorMessage);
        result.Value!["value"].Should().Be(text);
    }

    [UnitTest]
    public void ReservedAttributes_AcceptOnlyTheExactReservedNames()
    {
        var resource = Resource(MetadataV2FieldType.String);
        var result = FeatureAttributeValidator.Validate(resource, new Dictionary<string, object?>
        {
            [FeatureMutationAttributes.WfsGmlNameAttributeName] = JsonSerializer.SerializeToElement("place"),
        });
        result.IsValid.Should().BeTrue(result.ErrorMessage);
        result.Value![FeatureMutationAttributes.WfsGmlNameAttributeName].Should().Be("place");
        FeatureMutationAttributes.IsReserved("__honua_wfs_gml_name_extra").Should().BeFalse();
        FeatureMutationAttributes.IsReserved("__HONUA_WFS_GML_NAME").Should().BeFalse();
    }

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Date)]
    [InlineData(MetadataV2FieldType.DateTime)]
    public void NumericDates_AreAcceptedOnlyByCompatibilityProfile(MetadataV2FieldType type)
    {
        var attributes = new Dictionary<string, object?> { ["value"] = 1730000000000L };
        FeatureAttributeValidator.Validate(Resource(type), attributes).IsValid.Should().BeFalse();
        var result = FeatureAttributeValidator.Validate(Resource(type), attributes, new(AttributeValidationProfile.GeoServices));
        result.IsValid.Should().BeTrue(result.ErrorMessage);
        result.Value!["value"].Should().Be(1730000000000L);
    }

    private static MetadataV2Object Resource(MetadataV2FieldType type) => new()
    {
        SchemaFields = [new() { Name = "value", Type = type, Editable = true, Nullable = true }],
    };
}
