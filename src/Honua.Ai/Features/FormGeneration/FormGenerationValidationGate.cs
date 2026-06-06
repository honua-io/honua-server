// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Forms.Packages;

namespace Honua.Ai.FormGeneration;

/// <summary>
/// Generation-lenient gate over <see cref="FormPackageValidator"/>. The full publish validator
/// resolves the target service/layer against live metadata and checks each field against the real
/// layer schema — data that is unknowable from a natural-language prompt and is bound when the form
/// is published, not when it is generated. So, exactly like the workflow generation gate (which runs
/// the structural <c>WorkflowPackageGraphValidator</c>, not the deep <c>ProcessPlanValidator</c>),
/// form generation must gate + repair only on STRUCTURAL issues and treat the runtime-binding issues
/// as non-blocking warnings.
///
/// This classifies <see cref="FormPackageValidationResult"/> issues by code: the runtime-binding
/// codes below are tolerated during generation; everything else is a structural failure the model
/// must fix in the bounded repair loop.
/// </summary>
internal static class FormGenerationValidationGate
{
    /// <summary>
    /// Issue codes that depend on the real target layer (resolved at publish time), not on the
    /// structural correctness of the generated form. Tolerated during generation.
    /// </summary>
    private static readonly HashSet<string> RuntimeBindingCodes = new(StringComparer.Ordinal)
    {
        "serviceNotFound",
        "layerNotFound",
        "targetFieldNotFound",
        "targetFieldNotWritable",
        "targetNotResolved",
        "targetMissing",
    };

    /// <summary>
    /// Splits a publish-validation result into the structural failures that gate generation and the
    /// runtime-binding warnings that are deferred to publish.
    /// </summary>
    public static FormGenerationGateResult Evaluate(FormPackageValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);

        var structural = new List<FormValidationIssue>();
        var deferred = new List<FormValidationIssue>();
        foreach (var issue in validation.Issues ?? [])
        {
            // Only errors gate; warnings never block.
            var isError = string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase);
            if (isError && !RuntimeBindingCodes.Contains(issue.Code))
            {
                structural.Add(issue);
            }
            else
            {
                deferred.Add(issue);
            }
        }

        return new FormGenerationGateResult(structural.Count == 0, structural, deferred);
    }
}

/// <summary>
/// Target resolver used for GENERATION only: returns an unresolved target so
/// <see cref="FormPackageValidator"/> runs its DB-free structural checks (schemaVersion, title,
/// fields-required, duplicate ids) and surfaces target binding as a tolerated "not found" issue,
/// without requiring a live metadata database. The real target is bound at publish time.
/// </summary>
internal sealed class GenerationFormTargetResolver : IFormTargetMetadataResolver
{
    public Task<FormTargetMetadataResolution> ResolveAsync(
        FormTargetDefinition? target,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(default(FormTargetMetadataResolution));
}

/// <summary>Outcome of the generation-lenient gate.</summary>
/// <param name="Passed">True when there are no structural failures (runtime-binding issues are tolerated).</param>
/// <param name="StructuralFailures">Structural issues the model must fix in the repair loop.</param>
/// <param name="DeferredBindings">Runtime-binding issues surfaced as warnings (bound at publish).</param>
internal sealed record FormGenerationGateResult(
    bool Passed,
    IReadOnlyList<FormValidationIssue> StructuralFailures,
    IReadOnlyList<FormValidationIssue> DeferredBindings);
