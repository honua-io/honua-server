// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Edit;

/// <summary>
/// Unit coverage for <see cref="EditProcessor"/> create-operation validation,
/// specifically the per-field required-attribute check introduced by BH2-014.
/// </summary>
public sealed class EditProcessorCreateValidationTests
{
    private static EditProcessor CreateProcessor() => new(NullLogger<EditProcessor>.Instance);

    // Resource with one required non-nullable string field ("name") and one nullable field ("notes").
    private static MetadataV2Resource CreateResourceWithRequiredField() => new()
    {
        SchemaFields =
        [
            new MetadataV2Field
            {
                Name = "objectid",
                Type = MetadataV2FieldType.BigInteger,
                Nullable = false,
                SemanticRoles = ["id.primary"]
            },
            new MetadataV2Field
            {
                Name = "name",
                Type = MetadataV2FieldType.String,
                Nullable = false,
                SemanticRoles = []
            },
            new MetadataV2Field
            {
                Name = "notes",
                Type = MetadataV2FieldType.String,
                Nullable = true,
                SemanticRoles = []
            }
        ]
    };

    // BH2-014 regression: a feature that supplies SOME attributes (e.g. only the nullable "notes"
    // field) but omits the required "name" field must be rejected with a named error, not passed
    // through to the DB to fail with an opaque NOT NULL constraint violation.
    [UnitTest]
    public void ValidateEdit_CreateWithMissingRequiredField_ReturnsNamedFailure()
    {
        var processor = CreateProcessor();
        var resource = CreateResourceWithRequiredField();

        // Supply only the nullable field — "name" is absent.
        var attributes = ImmutableDictionary.Create<string, object?>(StringComparer.OrdinalIgnoreCase)
            .Add("notes", "some note");

        var request = UnifiedEditRequest.WithCreates(
            ImmutableArray.Create(EditFeature.ForCreate(geometry: null, attributes)));

        var result = processor.ValidateEdit(request, resource);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage.Should().Contain("name",
            "the error message must name the missing required field");
        result.ErrorMessage.Should().Contain("Required attribute",
            "the error should identify the category of validation failure");
    }

    [UnitTest]
    public void ValidateEdit_CreateWithAllRequiredFields_Succeeds()
    {
        var processor = CreateProcessor();
        var resource = CreateResourceWithRequiredField();

        var attributes = ImmutableDictionary.Create<string, object?>(StringComparer.OrdinalIgnoreCase)
            .Add("name", "Honua")
            .Add("notes", "optional");

        var request = UnifiedEditRequest.WithCreates(
            ImmutableArray.Create(EditFeature.ForCreate(geometry: null, attributes)));

        var result = processor.ValidateEdit(request, resource);

        // Validation passes — the "name" field is present; geometry is not required
        // because this resource has no Spatial configuration.
        result.IsValid.Should().BeTrue();
    }

    [UnitTest]
    public void ValidateEdit_CreateWithEmptyAttributesAndRequiredField_ReturnsGenericFailure()
    {
        var processor = CreateProcessor();
        var resource = CreateResourceWithRequiredField();

        // Completely empty attribute dictionary: the original "no attributes at all" check
        // (not the BH2-014 per-field check) should fire first with the legacy message.
        var attributes = ImmutableDictionary<string, object?>.Empty;

        var request = UnifiedEditRequest.WithCreates(
            ImmutableArray.Create(EditFeature.ForCreate(geometry: null, attributes)));

        var result = processor.ValidateEdit(request, resource);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Attributes required",
            "the empty-attributes guard fires before the per-field check");
    }

    [UnitTest]
    public void ValidateEdit_CreateWithCaseInsensitiveFieldName_Succeeds()
    {
        var processor = CreateProcessor();
        var resource = CreateResourceWithRequiredField();

        // Supply "NAME" (uppercase) — should match the schema field "name" case-insensitively.
        var attributes = ImmutableDictionary.Create<string, object?>(StringComparer.OrdinalIgnoreCase)
            .Add("NAME", "CaseTest");

        var request = UnifiedEditRequest.WithCreates(
            ImmutableArray.Create(EditFeature.ForCreate(geometry: null, attributes)));

        var result = processor.ValidateEdit(request, resource);

        result.IsValid.Should().BeTrue(
            "field presence check must be case-insensitive so 'NAME' satisfies the 'name' schema field");
    }
}
