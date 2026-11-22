// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Forms;

// Layers published with exact types declare TIMESTAMPTZ as TimestampOffset and NUMERIC as Decimal.
public sealed partial class FormPackageValidatorTests
{
    [UnitTheory]
    [InlineData("datetime", "inspected_at")]
    [InlineData("number", "amount")]
    [InlineData("decimal", "amount")]
    public async Task ValidateForPublish_ExactTargetFields_AcceptMatchingQuestionTypes(string questionType, string targetField)
    {
        var result = await CreateValidator(targetResource: CreateExactTypeResource())
            .ValidateForPublishAsync(CreateExactFieldPackage(questionType, targetField));

        result.IsValid.Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("date", "inspected_at")]
    [InlineData("text", "amount")]
    public async Task ValidateForPublish_ExactTargetFields_RefuseOtherQuestionTypes(string questionType, string targetField)
    {
        var result = await CreateValidator(targetResource: CreateExactTypeResource())
            .ValidateForPublishAsync(CreateExactFieldPackage(questionType, targetField));

        result.Issues.Select(issue => issue.Code).Should().Contain("targetFieldTypeMismatch");
    }

    [UnitTheory]
    [InlineData("datetime", "inspected_at", "\"2026-11-21T08:30:00.000-10:00\"")]
    [InlineData("number", "amount", "12.50")]
    [InlineData("number", "amount", "\"12.50\"")]
    [InlineData("number", "amount", "1.5e3")]
    public async Task ValidateSubmission_ExactTargetFields_AcceptValidExactValues(
        string questionType, string targetField, string submittedJson)
    {
        var codes = await SubmissionIssueCodesAsync(questionType, targetField, submittedJson);

        codes.Should().NotContain("fieldValueTypeMismatch");
    }

    [UnitTheory]
    [InlineData("datetime", "inspected_at", "1795285800000")]
    [InlineData("datetime", "inspected_at", "\"not-a-time\"")]
    [InlineData("datetime", "inspected_at", "\"2026-11-21T08:30:00\"")]
    [InlineData("number", "amount", "true")]
    [InlineData("number", "amount", "\"abc\"")]
    [InlineData("number", "amount", "1e400")]
    public async Task ValidateSubmission_ExactTargetFields_RefuseInvalidExactValues(
        string questionType, string targetField, string submittedJson)
    {
        var codes = await SubmissionIssueCodesAsync(questionType, targetField, submittedJson);

        codes.Should().Contain("fieldValueTypeMismatch");
    }

    private static async Task<IEnumerable<string>> SubmissionIssueCodesAsync(
        string questionType, string targetField, string submittedJson)
    {
        var version = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = CreateExactFieldPackage(questionType, targetField)
        };
        var request = CreateSubmissionRequest(FormSubmissionOperations.Create,
            values: new Dictionary<string, JsonElement> { ["name"] = Json(submittedJson) });

        var result = await CreateValidator(targetResource: CreateExactTypeResource()).ValidateSubmissionAsync(version, request);
        return result.Issues.Select(issue => issue.Code).ToArray();
    }

    private static FormPackageDocument CreateExactFieldPackage(string questionType, string targetField)
    {
        var package = CreatePackage();
        package.Fields[0] = new FormFieldDefinition
        {
            FieldId = "name",
            Label = "Exact value",
            Type = questionType,
            TargetField = targetField,
            SectionId = "main",
            Required = false
        };
        return package;
    }

    private static MetadataV2Object CreateExactTypeResource()
    {
        var resource = CreateResource(MetadataV2SpatialReference.Wgs84);
        return resource with
        {
            SchemaFields =
            [
                .. resource.SchemaFields,
                new MetadataV2Field { Name = "inspected_at", Type = MetadataV2FieldType.TimestampOffset, Nullable = true },
                new MetadataV2Field { Name = "amount", Type = MetadataV2FieldType.Decimal, Nullable = true }
            ]
        };
    }
}
