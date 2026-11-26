// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Forms.Packages;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Forms;

public sealed partial class FormPackageValidatorTests
{
    [UnitTest]
    public async Task ValidateForPublish_WithoutExpressionPolicy_RejectsPortableExpressions()
    {
        var result = await CreateValidator().ValidateForPublishAsync(ExpressionPackage());

        result.IsValid.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Code == "expressionLanguageUnsupported");
    }

    [UnitTest]
    public async Task ValidateSubmission_WithoutExpressionPolicy_RejectsPortableExpressions()
    {
        var version = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = ExpressionPackage()
        };

        var result = await CreateValidator().ValidateSubmissionAsync(version,
            CreateSubmissionRequest(FormSubmissionOperations.Create));

        result.IsValid.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Code == "expressionLanguageUnsupported");
    }

    [UnitTheory]
    [InlineData("relevanceExpression")]
    [InlineData("constraintExpression")]
    [InlineData("calculatedExpression")]
    public async Task ValidateWithoutLanguage_DoesNotSilentlyIgnoreFieldExpressions(string propertyName)
    {
        var source = $$"""
            { "fields": [{ "fieldId": "score", "{{propertyName}}": "false" }] }
            """;
        var package = JsonSerializer.Deserialize(source, FormPackageJsonContext.Default.FormPackageDocument)!;
        var version = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = status,
            Package = package
        };

        var publish = await CreateValidator().ValidateForPublishAsync(package);
        var submission = await CreateValidator().ValidateSubmissionAsync(version,
            CreateSubmissionRequest(FormSubmissionOperations.Create));

        publish.Issues.Should().Contain(issue => issue.Code == "expressionLanguageUnsupported");
        submission.Issues.Should().Contain(issue => issue.Code == "expressionLanguageUnsupported");
    }

    [UnitTest]
    public void PackageSerialization_PreservesPortableFieldExpressions()
    {
        const string source = """
            {
              "expressionLanguage": "honua.portable.v1",
              "fields": [{ "fieldId": "score",
                "relevanceExpression": "${enabled} = true",
                "constraintExpression": ". >= 0",
                "constraintMessage": "Score must be nonnegative",
                "calculatedExpression": "${count} * 2" }]
            }
            """;
        var package = JsonSerializer.Deserialize(source, FormPackageJsonContext.Default.FormPackageDocument)!;
        using var serialized = JsonDocument.Parse(
            JsonSerializer.Serialize(package, FormPackageJsonContext.Default.FormPackageDocument));
        using var original = JsonDocument.Parse(source);

        var field = serialized.RootElement.GetProperty("fields")[0];
        foreach (var property in original.RootElement.GetProperty("fields")[0].EnumerateObject())
        {
            field.TryGetProperty(property.Name, out var value).Should().BeTrue(
                "native package storage must retain {0}", property.Name);
            value.GetString().Should().Be(property.Value.GetString());
        }
    }

    [UnitTheory]
    [InlineData("""{"count":0,"name":"capture","category":[{"note":"visit"}]}""")]
    [InlineData("""{"count":0,"category":[{}]}""")]
    public async Task NormalizePublishedXlsform_WhenParentAnswerBecomesHidden_DropsRootAndRepeatAnswers(string answers)
    {
        var package = XlsRelevancePackage();
        var validator = CreateValidator();
        var published = PublishedXlsPackage(package);
        var publishValidation = await validator.ValidateForPublishAsync(package);
        publishValidation.IsValid.Should().BeTrue("{0}", string.Join(", ", publishValidation.Issues));

        var visible = CreateSubmissionRequest(FormSubmissionOperations.Create,
            values: new() { ["count"] = Json("1"), ["name"] = Json(""" "capture" """), ["category"] = Json("""[{"note":"visit"}]""") });
        var visibleResult = await validator.ValidateAndNormalizeSubmissionAsync(published, visible);
        visibleResult.IsValid.Should().BeTrue("{0}", string.Join(", ", visibleResult.Issues));
        visible.Values["category"].GetString().Should().Be("""[{"note":"visit","score":2}]""");

        var hidden = CreateSubmissionRequest(FormSubmissionOperations.Create,
            values: Json(answers).EnumerateObject().ToDictionary(static property => property.Name, static property => property.Value));

        var hiddenResult = await validator.ValidateAndNormalizeSubmissionAsync(published, hidden);
        hiddenResult.IsValid.Should().BeTrue("{0}", string.Join(", ", hiddenResult.Issues));
        hidden.Values.Should().NotContainKey("name");
        hidden.Values["category"].GetString().Should().Be("""[{"score":1}]""");
    }

    [UnitTest]
    public async Task NormalizePublishedXlsForm_UpdateClearsPreviouslyRelevantAttribute()
    {
        var published = PublishedXlsPackage(XlsRelevancePackage());
        var hidden = CreateSubmissionRequest(FormSubmissionOperations.Update, targetFeatureId: 42,
            values: new() { ["count"] = Json("0") }, includeGeometry: false);

        var validation = await CreateValidator().ValidateAndNormalizeSubmissionAsync(published, hidden);

        validation.IsValid.Should().BeTrue("{0}", string.Join(", ", validation.Issues));
        hidden.Values["name"].ValueKind.Should().Be(JsonValueKind.Null);
    }

    [UnitTest]
    public async Task ValidateRecordAsync_ArchivedXlsFormChecksRelevantRequiredRepeatAnswersWithoutMutatingInput()
    {
        var published = PublishedXlsPackage(XlsRelevancePackage(), FormPackageStatus.Archived);
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["count"] = Json("1"), ["name"] = Json("\"capture\""), ["category"] = Json("[{}]")
        };
        var validator = CreateValidator();

        var validation = await validator.ValidateRecordAsync(published, (values, []));

        validation.Issues.Should().Contain(issue => issue.Code == "requiredFieldMissing" && issue.FieldId == "note");
        values["category"].GetRawText().Should().Be("[{}]");
    }

    [UnitTest]
    public async Task ValidateRecordAsync_ComputesDerivedAnswersWithoutRequiringSubmissionGeometry()
    {
        var package = JsonSerializer.Deserialize("""
            {
              "schemaVersion": "honua.form-package.v1",
              "title": "Inspection",
              "expressionLanguage": "xlsform",
              "target": { "serviceId": "test", "layerId": 0 },
              "sections": [{ "sectionId": "main", "label": "Main", "fieldIds": ["count", "name", "gps"] }],
              "fields": [
                { "fieldId": "count", "type": "integer", "targetField": "count", "sectionId": "main",
                  "calculatedExpression": "2" },
                { "fieldId": "name", "type": "text", "targetField": "name", "sectionId": "main",
                  "required": true, "relevanceExpression": "${count} = 2" },
                { "fieldId": "gps", "type": "location", "sectionId": "main", "required": true }
              ]
            }
            """, FormPackageJsonContext.Default.FormPackageDocument)!;
        var published = PublishedXlsPackage(package);
        var validator = CreateValidator();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        var missing = await validator.ValidateRecordAsync(published, (values, []));
        missing.Issues.Should().ContainSingle(issue => issue.Code == "requiredFieldMissing").Which.FieldId.Should().Be("name");

        values["name"] = Json("\"capture\"");
        var complete = await validator.ValidateRecordAsync(published, (values, []));
        complete.IsValid.Should().BeTrue("{0}", string.Join(", ", complete.Issues));
        values.Should().NotContainKey("count");

        var submission = CreateSubmissionRequest(FormSubmissionOperations.Create, values: values, includeGeometry: false);
        var submitted = await validator.ValidateAndNormalizeSubmissionAsync(published, submission);
        submitted.Issues.Should().Contain(issue => issue.Code == "requiredFieldMissing" && issue.FieldId == "gps");
    }

    private static FormPackageVersion PublishedXlsPackage(FormPackageDocument package, string status = FormPackageStatus.Published)
        => new()
        {
            FormId = "inspection",
            Version = 1,
            Status = status,
            Package = package
        };

    private static FormPackageDocument XlsRelevancePackage() =>
        JsonSerializer.Deserialize("""
            {
              "schemaVersion": "honua.form-package.v1",
              "title": "Inspection",
              "expressionLanguage": "xlsform",
              "submitPolicy": { "allowedOperations": ["create", "update"] },
              "target": { "serviceId": "test", "layerId": 0 },
              "sections": [
                { "sectionId": "main", "label": "Main", "fieldIds": ["count", "name"] },
                { "sectionId": "category", "label": "Rows", "repeatable": true, "fieldIds": ["note", "score"] }
              ],
              "fields": [
                { "fieldId": "count", "label": "Count", "type": "integer", "targetField": "count", "sectionId": "main" },
                { "fieldId": "name", "label": "Name", "type": "text", "targetField": "name", "sectionId": "main",
                  "relevanceExpression": "${count} > 0" },
                { "fieldId": "note", "label": "Note", "type": "text", "sectionId": "category",
                  "required": true, "relevanceExpression": "${name} = 'capture'" },
                { "fieldId": "score", "label": "Score", "type": "integer", "sectionId": "category",
                  "calculatedExpression": "if(${name} = 'capture', 2, 1)" }
              ]
            }
            """, FormPackageJsonContext.Default.FormPackageDocument)!;

    private static FormPackageDocument ExpressionPackage()
        => JsonSerializer.Deserialize("""
            {
              "schemaVersion": "honua.form-package.v1",
              "title": "Inspection",
              "expressionLanguage": "honua.portable.v1",
              "target": { "serviceId": "test", "layerId": 0 },
              "sections": [{ "sectionId": "main", "label": "Main", "fieldIds": ["name"] }],
              "fields": [{ "fieldId": "name", "label": "Name", "type": "text",
                "targetField": "name", "sectionId": "main", "constraintExpression": "false" }]
            }
            """, FormPackageJsonContext.Default.FormPackageDocument)!;
}
