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
            Status = FormPackageStatus.Published,
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
