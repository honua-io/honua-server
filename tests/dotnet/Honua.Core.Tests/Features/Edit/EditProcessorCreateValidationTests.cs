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

    // Resource whose non-nullable "objectid" field carries NO semantic roles — mirrors the many
    // seeded/published layers (e.g. WebAppFixtureMetadataV2Mixin, ServiceRbacTestFixture) that
    // declare objectid as Nullable=false without the "id.primary" role. BH2-014 (#2456) keyed its
    // primary-id skip exclusively on the role, so every create omitting objectid was rejected.
    private static MetadataV2Resource CreateResourceWithUnroledObjectId() => new()
    {
        SchemaFields =
        [
            new MetadataV2Field
            {
                Name = "objectid",
                Type = MetadataV2FieldType.Integer,
                Nullable = false,
                SemanticRoles = []
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

    // Regression (#2456): a create that supplies every required NON-id field but omits the
    // server-assigned "objectid" must validate, even when objectid lacks the "id.primary" role.
    // Before the fix this failed with "Required attribute(s) missing for create operation: objectid".
    [UnitTest]
    public void ValidateEdit_CreateOmittingUnroledObjectId_Succeeds()
    {
        var processor = CreateProcessor();
        var resource = CreateResourceWithUnroledObjectId();

        var attributes = ImmutableDictionary.Create<string, object?>(StringComparer.OrdinalIgnoreCase)
            .Add("name", "Honua");

        var request = UnifiedEditRequest.WithCreates(
            ImmutableArray.Create(EditFeature.ForCreate(geometry: null, attributes)));

        var result = processor.ValidateEdit(request, resource);

        result.IsValid.Should().BeTrue(
            "the server-assigned object-id field must never be required from the client, "
            + "even when it does not carry the id.primary semantic role");
    }

    // The fix must not weaken BH2-014: a genuinely-missing required NON-id field is still rejected
    // even on a layer whose objectid lacks the id.primary role.
    [UnitTest]
    public void ValidateEdit_CreateMissingRequiredFieldOnUnroledObjectIdLayer_StillFails()
    {
        var processor = CreateProcessor();
        var resource = CreateResourceWithUnroledObjectId();

        // Supplies only the nullable "notes" field — the required "name" field is absent.
        var attributes = ImmutableDictionary.Create<string, object?>(StringComparer.OrdinalIgnoreCase)
            .Add("notes", "some note");

        var request = UnifiedEditRequest.WithCreates(
            ImmutableArray.Create(EditFeature.ForCreate(geometry: null, attributes)));

        var result = processor.ValidateEdit(request, resource);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("name",
            "a genuinely-missing required non-id field must still be reported");
        result.ErrorMessage.Should().NotContain("objectid",
            "the server-assigned object-id field must never appear as a missing required attribute");
    }

    // The update path must likewise not require the object-id field as a client attribute: the
    // caller identifies the row via EditFeature.ObjectId, not via an "objectid" attribute entry.
    [UnitTest]
    public void ValidateEdit_UpdateOmittingUnroledObjectIdAttribute_Succeeds()
    {
        var processor = CreateProcessor();
        var resource = CreateResourceWithUnroledObjectId();

        var attributes = ImmutableDictionary.Create<string, object?>(StringComparer.OrdinalIgnoreCase)
            .Add("name", "Updated");

        var request = UnifiedEditRequest.WithUpdates(
            ImmutableArray.Create(EditFeature.ForUpdate(objectId: 42, geometry: null, attributes)));

        var result = processor.ValidateEdit(request, resource);

        result.IsValid.Should().BeTrue(
            "an update identifies the row via EditFeature.ObjectId, so the object-id field is not "
            + "required as a client attribute");
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

    // Resource that carries BOTH a distinct public primary id ("id", string, id.primary role) AND a
    // separate server-assigned integer object-id ("objectid", no role) — the shape of the OGC API
    // Features String-ID layers (OgcFeaturesStringIdentifierEndpointTests, StringIdLayerId=99).
    // FindPrimaryIdField resolves to the roled public "id", so before this fix the conventional
    // integer "objectid" fell through the primary-id skip and a create omitting it (the normal case,
    // since the server auto-assigns it) was rejected — surfacing as an OGC HTTP 500.
    private static MetadataV2Resource CreateResourceWithDistinctPublicIdAndObjectId() => new()
    {
        SchemaFields =
        [
            new MetadataV2Field
            {
                Name = "id",
                Type = MetadataV2FieldType.String,
                Nullable = false,
                Length = 64,
                SemanticRoles = ["id.primary"]
            },
            new MetadataV2Field
            {
                Name = "objectid",
                Type = MetadataV2FieldType.Integer,
                Nullable = false,
                SemanticRoles = []
            },
            new MetadataV2Field
            {
                Name = "name",
                Type = MetadataV2FieldType.String,
                Nullable = false,
                SemanticRoles = []
            }
        ]
    };

    // Regression (OGC create-with-top-level-string-feature-id): a create that supplies the public
    // "id" plus the required "name" but omits the server-assigned integer "objectid" must validate,
    // even though "objectid" is non-nullable and does not carry the id.primary role (a separate
    // "id" field does). Before the fix this failed with
    // "Required attribute(s) missing for create operation: objectid" → OGC HTTP 500.
    [UnitTest]
    public void ValidateEdit_CreateOmittingServerAssignedObjectIdBesidePublicId_Succeeds()
    {
        var processor = CreateProcessor();
        var resource = CreateResourceWithDistinctPublicIdAndObjectId();

        var attributes = ImmutableDictionary.Create<string, object?>(StringComparer.OrdinalIgnoreCase)
            .Add("id", "top-level-created")
            .Add("name", "Top Level ID Created");

        var request = UnifiedEditRequest.WithCreates(
            ImmutableArray.Create(EditFeature.ForCreate(geometry: null, attributes)));

        var result = processor.ValidateEdit(request, resource);

        result.IsValid.Should().BeTrue(
            "the conventional server-assigned integer object-id must never be required from the "
            + "client, even when a distinct id.primary field is the resource's resolved primary id");
    }

    // The fix must not weaken validation: on the same two-id-field layer, a genuinely-missing
    // required NON-id field ("name") is still rejected, and the server-assigned "objectid" must not
    // appear in the missing-required list.
    [UnitTest]
    public void ValidateEdit_CreateMissingRequiredFieldBesidePublicIdAndObjectId_StillFails()
    {
        var processor = CreateProcessor();
        var resource = CreateResourceWithDistinctPublicIdAndObjectId();

        // Supplies only the public "id"; the required "name" field is absent.
        var attributes = ImmutableDictionary.Create<string, object?>(StringComparer.OrdinalIgnoreCase)
            .Add("id", "top-level-created");

        var request = UnifiedEditRequest.WithCreates(
            ImmutableArray.Create(EditFeature.ForCreate(geometry: null, attributes)));

        var result = processor.ValidateEdit(request, resource);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("name",
            "a genuinely-missing required non-id field must still be reported");
        result.ErrorMessage.Should().NotContain("objectid",
            "the server-assigned object-id field must never appear as a missing required attribute");
    }
}
