// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Publishing.Reports;

namespace Honua.Ai.ReportGeneration;

/// <summary>
/// Generation gate over <see cref="ReportDocumentValidator"/>. Unlike form generation — whose publish
/// validator resolves a live layer target and so must defer runtime-binding issues — the report document
/// has NO DB-backed publish validator: <see cref="ReportDocumentValidator"/> is purely structural and
/// requires no live metadata. There is therefore nothing to defer here. The gate simply classifies every
/// error-severity issue as a structural failure the model must fix in the bounded repair loop; warnings
/// never block. This keeps the gate shape identical to <c>FormGenerationValidationGate</c> so the two
/// generation services stay symmetric.
/// </summary>
internal static class ReportGenerationValidationGate
{
    /// <summary>
    /// Splits a structural-validation result into the failures that gate generation and the warnings that
    /// are surfaced but never block. (Report validation is structural-only, so nothing is deferred.)
    /// </summary>
    public static ReportGenerationGateResult Evaluate(ReportValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);

        var structural = new List<ReportValidationIssue>();
        var deferred = new List<ReportValidationIssue>();
        foreach (var issue in validation.Issues ?? [])
        {
            // Only errors gate; warnings never block.
            var isError = string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase);
            if (isError)
            {
                structural.Add(issue);
            }
            else
            {
                deferred.Add(issue);
            }
        }

        return new ReportGenerationGateResult(structural.Count == 0, structural, deferred);
    }
}

/// <summary>Outcome of the report generation gate.</summary>
/// <param name="Passed">True when there are no structural failures.</param>
/// <param name="StructuralFailures">Structural issues the model must fix in the repair loop.</param>
/// <param name="DeferredWarnings">Warning-severity issues surfaced but not blocking.</param>
internal sealed record ReportGenerationGateResult(
    bool Passed,
    IReadOnlyList<ReportValidationIssue> StructuralFailures,
    IReadOnlyList<ReportValidationIssue> DeferredWarnings);
