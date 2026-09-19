// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation;
using Honua.Infrastructure.Validation;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// QGIS serialises the unset system-maintained object id as an explicit <c>"objectid": null</c>
/// when it digitises a new feature, and cannot omit it. The mutation validator must read that as
/// "not supplied" on insert and still reject it on update.
/// </summary>
/// <remarks>
/// These cases pin the field shape the client-compat fixture actually publishes, which the
/// integration test <c>AddFeatures_WithNullSystemMaintainedObjectId_AssignsOneInstead</c> does not:
/// there the object id is declared <c>Editable=false</c>, so a relaxation keyed on
/// <see cref="MetadataV2Field.Editable"/> alone passed while the seeded fixture — object id
/// <c>Editable</c> left at its default of true, identified only by the <c>id.primary</c> role or
/// the conventional name — still answered error 1006 to the same QGIS insert. The relaxation is
/// keyed on <see cref="MetadataV2SpatialExtensions.IsServerAssignedIdField"/> so every shape that
/// rule recognises is covered, and each shape is a separate case here.
/// </remarks>
[Protocol(TestProtocols.FeatureServer)]
public sealed class NullSystemMaintainedObjectIdValidationTests
{
    private static MetadataV2Field RoledObjectId() => new()
    {
        Name = "objectid",
        Type = MetadataV2FieldType.BigInteger,
        Nullable = false,
        SemanticRoles = ["id.primary"],
        // Editable deliberately left at its default (true): the seeded fixture never sets it.
    };

    private static MetadataV2Field ConventionalObjectId() => new()
    {
        Name = "objectid",
        Type = MetadataV2FieldType.Integer,
        Nullable = false,
        // No role, no Editable flag: the conventional Esri-style OID and nothing else.
    };

    private static MetadataV2Field DeclaredNonEditableObjectId() => new()
    {
        Name = "objectid",
        Type = MetadataV2FieldType.BigInteger,
        Nullable = false,
        Editable = false,
    };

    private static MetadataV2Field Name() => new()
    {
        Name = "name",
        Type = MetadataV2FieldType.String,
        Nullable = true,
    };

    public static TheoryData<string, MetadataV2Field> ServerAssignedObjectIdShapes => new()
    {
        { "id.primary role, Editable defaulted", RoledObjectId() },
        { "conventional integer objectid, no role", ConventionalObjectId() },
        { "declared Editable=false", DeclaredNonEditableObjectId() },
    };

    [Theory]
    [Operation(Operations.ApplyEdits)]
    [MemberData(nameof(ServerAssignedObjectIdShapes))]
    public void Insert_WithExplicitNullObjectId_IsAcceptedAndTheKeyIsDropped(string shape, MetadataV2Field objectId)
    {
        var result = Validate(objectId, isUpdate: false, ("objectid", null), ("name", "digitised"));

        result.IsValid.Should().BeTrue($"[{shape}] a null server-assigned object id on insert means 'assign one': {result.ErrorMessage}");
        result.Value.Should().NotContainKey("objectid",
            $"[{shape}] the store must assign the id exactly as when the member is absent, so the null must not reach it");
        result.Value.Should().ContainKey("name");
    }

    [Theory]
    [Operation(Operations.ApplyEdits)]
    [MemberData(nameof(ServerAssignedObjectIdShapes))]
    public void Update_WithExplicitNullObjectId_IsStillRejected(string shape, MetadataV2Field objectId)
    {
        var result = Validate(objectId, isUpdate: true, ("objectid", null), ("name", "renamed"));

        result.IsValid.Should().BeFalse($"[{shape}] an update carrying a null object id addresses no row");
    }

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public void Insert_WithExplicitNullOnAnOrdinaryNonNullableField_IsStillRejected()
    {
        // The relaxation must not widen into "any null on insert is fine": a
        // non-nullable field the client is responsible for still fails as before.
        var required = new MetadataV2Field { Name = "code", Type = MetadataV2FieldType.String, Nullable = false };

        var result = Validate(required, isUpdate: false, ("code", null));

        result.IsValid.Should().BeFalse("'code' is not server-assigned, so a null is a genuine violation");
        result.ErrorMessage.Should().Contain("code").And.Contain("cannot be null");
    }

    private static ValidationResult<ImmutableDictionary<string, object?>> Validate(
        MetadataV2Field field,
        bool isUpdate,
        params (string Attribute, object? Value)[] attributes)
    {
        var resource = new MetadataV2Resource { SchemaFields = [field, Name()] };
        var validator = new FeatureMutationValidator(Substitute.For<IGeometryValidator>());
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (attribute, value) in attributes)
        {
            payload[attribute] = value;
        }

        return validator.ValidateAttributes(
            resource,
            payload,
            ValidationExtensions.AttributeValidationMode.GeoServices,
            isUpdate);
    }
}
