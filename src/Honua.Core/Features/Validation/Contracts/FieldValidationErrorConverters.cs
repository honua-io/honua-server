// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Core.Features.Validation.Contracts;

/// <summary>
/// Normalizes the existing field-addressable validation shapes onto the shared
/// <see cref="FieldValidationError"/> contract without changing what is
/// validated. Covers the three already-structured shapes:
/// Studio package diagnostics (<see cref="StudioValidationDiagnostic"/>),
/// Form package issues (<see cref="FormValidationIssue"/>), and Workflow
/// package failures (<see cref="WorkflowPackageValidationFailure"/>). The flat
/// throwers (Analysis content, Content publication) are intentionally not
/// handled here.
/// </summary>
public static class FieldValidationErrorConverters
{
    /// <summary>
    /// Converts a Studio package diagnostic to the shared contract. The Studio
    /// shape is the canonical template, so this is a structural rename only.
    /// </summary>
    /// <param name="diagnostic">Studio diagnostic.</param>
    /// <returns>The normalized <see cref="FieldValidationError"/>.</returns>
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

    /// <summary>
    /// Converts a Form package validation issue to the shared contract,
    /// preserving the field id alias alongside the path.
    /// </summary>
    /// <param name="issue">Form validation issue.</param>
    /// <returns>The normalized <see cref="FieldValidationError"/>.</returns>
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

    /// <summary>
    /// Converts a Workflow package validation failure to the shared contract.
    /// Failures are always blocking, and the workflow-specific <c>fieldPath</c>
    /// is normalized onto <see cref="FieldValidationError.Path"/>.
    /// </summary>
    /// <param name="failure">Workflow validation failure.</param>
    /// <returns>The normalized <see cref="FieldValidationError"/>.</returns>
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

    /// <summary>
    /// Normalizes a Studio validation summary's diagnostics onto the shared
    /// contract. Non-error diagnostics are preserved with their severity.
    /// </summary>
    /// <param name="summary">Studio validation summary.</param>
    /// <returns>A <see cref="FieldValidationResult"/> mirroring the diagnostics.</returns>
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

    /// <summary>
    /// Normalizes a Form package validation result onto the shared contract.
    /// </summary>
    /// <param name="result">Form validation result.</param>
    /// <returns>A <see cref="FieldValidationResult"/> mirroring the issues.</returns>
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

    /// <summary>
    /// Normalizes a Workflow package validation result onto the shared
    /// contract. Both blocking failures and non-fatal warnings are surfaced;
    /// warnings carry the <c>workflow.validation.warning</c> code.
    /// </summary>
    /// <param name="result">Workflow validation result.</param>
    /// <returns>A <see cref="FieldValidationResult"/> mirroring failures and warnings.</returns>
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
