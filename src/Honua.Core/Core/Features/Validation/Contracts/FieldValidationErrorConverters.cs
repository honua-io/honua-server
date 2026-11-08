// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Core.Features.Validation.Contracts;

public static class FieldValidationErrorConverters
{
    public static FieldValidationError ToFieldValidationError(this StudioValidationDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return new FieldValidationError
        {
            Code = diagnostic.Code,
            Severity = MapSeverity(diagnostic.Severity),
            Path = diagnostic.Path,
            Message = diagnostic.Message,
        };
    }

    public static FieldValidationError ToFieldValidationError(this FormValidationIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);

        return new FieldValidationError
        {
            Code = issue.Code,
            Severity = ParseSeverity(issue.Severity),
            Path = issue.Path,
            Message = issue.Message,
            FieldId = issue.FieldId,
        };
    }

    public static FieldValidationError ToFieldValidationError(this WorkflowPackageValidationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new FieldValidationError
        {
            Code = failure.Code,
            Severity = ValidationSeverity.Error,
            Path = failure.FieldPath,
            Message = failure.Message,
        };
    }

    public static FieldValidationResult ToFieldValidationResult(this StudioValidationSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var errors = new List<FieldValidationError>(summary.Diagnostics.Count);
        foreach (var diagnostic in summary.Diagnostics)
        {
            errors.Add(diagnostic.ToFieldValidationError());
        }

        return new FieldValidationResult { Errors = errors };
    }

    public static FieldValidationResult ToFieldValidationResult(this FormPackageValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var errors = new List<FieldValidationError>(result.Issues.Length);
        foreach (var issue in result.Issues)
        {
            errors.Add(issue.ToFieldValidationError());
        }

        return new FieldValidationResult { Errors = errors };
    }

    public static FieldValidationResult ToFieldValidationResult(this WorkflowPackageValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var errors = new List<FieldValidationError>(result.Failures.Count + result.Warnings.Count);
        foreach (var failure in result.Failures)
        {
            errors.Add(failure.ToFieldValidationError());
        }

        foreach (var warning in result.Warnings)
        {
            errors.Add(new FieldValidationError
            {
                Code = "workflow.validation.warning",
                Severity = ValidationSeverity.Warning,
                Message = warning,
            });
        }

        return new FieldValidationResult { Errors = errors };
    }

    private static ValidationSeverity MapSeverity(StudioPackageDiagnosticSeverity severity) => severity switch
    {
        StudioPackageDiagnosticSeverity.Info => ValidationSeverity.Info,
        StudioPackageDiagnosticSeverity.Warning => ValidationSeverity.Warning,
        StudioPackageDiagnosticSeverity.Error => ValidationSeverity.Error,
        StudioPackageDiagnosticSeverity.Blocker => ValidationSeverity.Blocker,
        _ => ValidationSeverity.Error,
    };

    private static ValidationSeverity ParseSeverity(string? severity)
        => severity?.ToLower(CultureInfo.InvariantCulture) switch
        {
            "info" => ValidationSeverity.Info,
            "warning" => ValidationSeverity.Warning,
            "blocker" => ValidationSeverity.Blocker,
            _ => ValidationSeverity.Error,
        };
}
