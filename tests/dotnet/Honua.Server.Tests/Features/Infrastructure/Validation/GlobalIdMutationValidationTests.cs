// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation;
using Honua.Infrastructure.Validation;

namespace Honua.Server.Tests.Features.Infrastructure.Validation;

public sealed class GlobalIdMutationValidationTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(ValidationExtensions.AttributeValidationMode.Strict)]
    [InlineData(ValidationExtensions.AttributeValidationMode.GeoServices)]
    public void BoundGlobalId_AllowsCreationButRejectsUpdatesAcrossMutationAdapters(
        ValidationExtensions.AttributeValidationMode mode)
    {
        var resource = new MetadataV2Resource
        {
            Editing = new() { GlobalIdField = "STABLE_ID" },
            SchemaFields =
            [
                new() { Name = "stable_id", Type = MetadataV2FieldType.Uuid, Editable = true },
                new() { Name = "ordinary_uuid", Type = MetadataV2FieldType.Uuid, Editable = true }
            ]
        };
        var attributes = new Dictionary<string, object?> { ["stable_ID"] = Guid.NewGuid().ToString() };
        resource.ValidateAttributesV2(attributes, mode).IsValid.Should().BeTrue();

        var update = resource.ValidateAttributesV2(attributes, mode, isUpdate: true);
        update.IsValid.Should().BeFalse();
        update.ErrorMessage.Should().Contain("stable_id").And.Contain("not editable");

        resource.ValidateAttributesV2(
            new Dictionary<string, object?> { ["ordinary_uuid"] = Guid.NewGuid().ToString() },
            mode, isUpdate: true).IsValid.Should().BeTrue();
    }
}
