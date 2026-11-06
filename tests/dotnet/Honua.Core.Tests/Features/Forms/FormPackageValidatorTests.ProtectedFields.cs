// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Forms;

public sealed partial class FormPackageValidatorTests
{
    [UnitTheory]
    [InlineData("readonly", false)]
    [InlineData("readonly", true)]
    [InlineData("hidden", false)]
    [InlineData("hidden", true)]
    [InlineData("globalid", false)]
    [InlineData("globalid", true)]
    public async Task ValidateSubmission_CurrentTargetFieldPolicy_RejectsProtectedValues(string policy, bool explicitNull)
    {
        var resource = CreateResource(MetadataV2SpatialReference.Wgs84);
        resource = resource with
        {
            SchemaFields = resource.SchemaFields.Select(field => field.Name == "name"
                ? field with { Editable = policy != "readonly", Hidden = policy == "hidden" }
                : field).ToArray(),
            Editing = resource.Editing! with { GlobalIdField = policy == "globalid" ? "name" : null }
        };
        var package = CreatePackage(allowedOperations: [FormSubmissionOperations.Update]);
        package.Fields[0] = new FormFieldDefinition
        {
            FieldId = "name",
            Label = "Name",
            Type = "text",
            TargetField = "name",
            SectionId = "main",
            Required = false
        };
        (await CreateValidator().ValidateForPublishAsync(package)).IsValid.Should().BeTrue();
        var version = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = package
        };
        var request = CreateSubmissionRequest(FormSubmissionOperations.Update, targetFeatureId: 42,
            values: new Dictionary<string, JsonElement> { ["name"] = Json(explicitNull ? "null" : "\"changed\"") });

        var result = await CreateValidator(targetResource: resource).ValidateSubmissionAsync(version, request);

        result.IsValid.Should().BeFalse();
        result.Issues.Select(issue => issue.Code).Should().Contain("targetFieldNotWritable");
    }

    [UnitTheory]
    [InlineData(FormSubmissionOperations.Create, false)]
    [InlineData(FormSubmissionOperations.Update, false)]
    [InlineData(FormSubmissionOperations.Create, true)]
    public async Task ValidateSubmission_EditableFieldOrInitialGlobalId_AcceptsValue(string operation, bool globalId)
    {
        var resource = CreateResource(MetadataV2SpatialReference.Wgs84);
        resource = resource with { Editing = resource.Editing! with { GlobalIdField = globalId ? "name" : null } };
        var version = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = CreatePackage(allowedOperations: [operation])
        };
        var request = CreateSubmissionRequest(operation, targetFeatureId: operation == FormSubmissionOperations.Update ? 42 : null);

        var result = await CreateValidator(targetResource: resource).ValidateSubmissionAsync(version, request);

        result.IsValid.Should().BeTrue();
    }

}
