// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

// The FeatureServer stamp runs after attribute validation, so it must write each tracking field in
// the stored form of its declared type.
public sealed class ServerMaintainedEditFieldsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 18, 30, 0, TimeSpan.Zero);

    [UnitTest]
    public void Stamp_InsertWithExactTimestampTrackingFields_WritesRfc3339Text()
    {
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        ServerMaintainedEditFields.Stamp(ExactTrackingResource(), attributes, isInsert: true,
            new EditPrincipal("field-user", IsAuthenticated: true, IsAdmin: false), Now, useGlobalIds: false, existingAttributes: null);

        foreach (var field in new[] { "created_on", "edited_on" })
        {
            var text = attributes[field].Should().BeOfType<string>().Subject;
            ExactAttributeValues.ParseTimestampOffset(text).Should().Be(Now);
        }
    }

    [UnitTest]
    public void Stamp_UpdateWithExactTimestampTrackingField_WritesRfc3339Text()
    {
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        ServerMaintainedEditFields.Stamp(ExactTrackingResource(), attributes, isInsert: false,
            new EditPrincipal("field-user", IsAuthenticated: true, IsAdmin: false), Now, useGlobalIds: false, existingAttributes: null);

        ExactAttributeValues.ParseTimestampOffset(attributes["edited_on"].Should().BeOfType<string>().Subject)
            .Should().Be(Now);
    }

    [UnitTest]
    public void OmitClientInsertValues_InsertWithNullObjectId_OmitsObjectIdAndValidatesOtherFields()
    {
        var resource = NonNullableObjectIdResource();
        using var nullDoc = JsonDocument.Parse("null");
        var attributes = new Dictionary<string, object?>
        {
            ["OBJECTID"] = nullDoc.RootElement,
            ["name"] = "qgis-feature"
        };

        var sanitized = ServerMaintainedEditFields.OmitClientInsertValues(resource, attributes);

        sanitized.Should().NotContainKey("OBJECTID");
        sanitized.Should().ContainKey("name");

        var validation = FeatureAttributeValidator.Validate(
            resource,
            sanitized,
            new AttributeValidationOptions(AttributeValidationProfile.GeoServices, AttributeMutationOperation.Insert));
        validation.IsValid.Should().BeTrue(validation.ErrorMessage);
    }

    [UnitTest]
    public void OmitClientInsertValues_InsertWithNullNonNullableBusinessField_KeepsFieldForValidation()
    {
        var resource = NonNullableObjectIdResource();
        var attributes = new Dictionary<string, object?>
        {
            ["objectid"] = null,
            ["name"] = null
        };

        var sanitized = ServerMaintainedEditFields.OmitClientInsertValues(resource, attributes);

        sanitized.Should().NotContainKey("objectid");
        sanitized.Should().ContainKey("name");

        var validation = FeatureAttributeValidator.Validate(
            resource,
            sanitized,
            new AttributeValidationOptions(AttributeValidationProfile.GeoServices, AttributeMutationOperation.Insert));
        validation.IsValid.Should().BeFalse();
        validation.ErrorMessage.Should().Be("Field 'name' cannot be null.");
    }

    [UnitTest]
    public void OmitClientInsertValues_InsertWithCustomObjectIdField_OmitsNullAndRetainsExplicitValue()
    {
        var resource = new MetadataV2Object
        {
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "gid",
                    Type = MetadataV2FieldType.BigInteger,
                    Nullable = false,
                    Editable = true,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true, Editable = true }
            ]
        };

        var withNull = ServerMaintainedEditFields.OmitClientInsertValues(
            resource,
            new Dictionary<string, object?> { ["gid"] = null, ["name"] = "a" });
        withNull.Should().NotContainKey("gid");

        var withValue = ServerMaintainedEditFields.OmitClientInsertValues(
            resource,
            new Dictionary<string, object?> { ["gid"] = 42L, ["name"] = "a" });
        withValue.Should().ContainKey("gid").WhoseValue.Should().Be(42L);
    }

    private static MetadataV2Object NonNullableObjectIdResource()
        => new()
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, Editable = true },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = false, Editable = true }
            ]
        };

    private static MetadataV2Object ExactTrackingResource()
        => new()
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger },
                new MetadataV2Field { Name = "created_on", Type = MetadataV2FieldType.TimestampOffset },
                new MetadataV2Field { Name = "edited_on", Type = MetadataV2FieldType.TimestampOffset }
            ],
            Editing = new MetadataV2ObjectEditing { CreatedAtField = "created_on", UpdatedAtField = "edited_on" }
        };
}
