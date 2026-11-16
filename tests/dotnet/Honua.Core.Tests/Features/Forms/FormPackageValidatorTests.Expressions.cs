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
