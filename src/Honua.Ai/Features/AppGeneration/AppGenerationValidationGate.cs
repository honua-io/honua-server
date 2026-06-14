// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.AppGeneration;

/// <summary>
/// Generation-lenient gate over <see cref="AppGenerationStructuralValidator"/>. The full publish path
/// resolves each page component's content binding (<c>content:ref@vN</c>) against the live content store
/// and the share policy against workspace policy — data that is unknowable from a natural-language prompt
/// and is bound when the app is published, not when it is generated. So, exactly like the map generation
/// gate (which tolerates layer/style/source binding gaps from the structural map validator), app
/// generation must gate + repair only on STRUCTURAL issues and treat the runtime-binding issues as
/// non-blocking warnings.
///
/// This classifies <see cref="AppPackageValidationResult"/> issues by code: the runtime-binding codes
/// below are tolerated during generation; everything else is a structural failure the model must fix in
/// the bounded repair loop.
/// </summary>
internal static class AppGenerationValidationGate
{
    /// <summary>
    /// Issue codes that depend on the real content/page binding (resolved at publish time), not on the
    /// structural correctness of the generated app. Tolerated during generation.
    /// </summary>
    private static readonly HashSet<string> RuntimeBindingCodes = new(StringComparer.Ordinal)
    {
        "bindingNotResolved",
        "actionPageNotFound",
    };

    /// <summary>
    /// Splits a structural-validation result into the structural failures that gate generation and the
    /// runtime-binding warnings that are deferred to publish.
    /// </summary>
    public static AppGenerationGateResult Evaluate(AppPackageValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);

        var structural = new List<AppValidationIssue>();
        var deferred = new List<AppValidationIssue>();
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

        return new AppGenerationGateResult(structural.Count == 0, structural, deferred);
    }
}

/// <summary>Outcome of the generation-lenient gate.</summary>
/// <param name="Passed">True when there are no structural failures (runtime-binding issues are tolerated).</param>
/// <param name="StructuralFailures">Structural issues the model must fix in the repair loop.</param>
/// <param name="DeferredBindings">Runtime-binding issues surfaced as warnings (bound at publish).</param>
internal sealed record AppGenerationGateResult(
    bool Passed,
    IReadOnlyList<AppValidationIssue> StructuralFailures,
    IReadOnlyList<AppValidationIssue> DeferredBindings);
