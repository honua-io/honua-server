// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.MapGeneration;

/// <summary>
/// Generation-lenient gate over <see cref="MapGenerationStructuralValidator"/>. The full publish path
/// resolves each source/layer ref against live metadata, the style refs against the style store, and
/// the sources for reachability — data that is unknowable from a natural-language prompt and is bound
/// when the map is published, not when it is generated. So, exactly like the form generation gate
/// (which tolerates serviceNotFound/layerNotFound from the structural form validator), map generation
/// must gate + repair only on STRUCTURAL issues and treat the runtime-binding issues as non-blocking
/// warnings.
///
/// This classifies <see cref="MapPackageValidationResult"/> issues by code: the runtime-binding codes
/// below are tolerated during generation; everything else is a structural failure the model must fix
/// in the bounded repair loop.
/// </summary>
internal static class MapGenerationValidationGate
{
    /// <summary>
    /// Issue codes that depend on the real layer/style/source (resolved at publish time), not on the
    /// structural correctness of the generated map. Tolerated during generation.
    /// </summary>
    private static readonly HashSet<string> RuntimeBindingCodes = new(StringComparer.Ordinal)
    {
        "layerNotFound",
        "layerNotResolved",
        "sourceNotFound",
        "sourceNotResolved",
        "styleNotFound",
        "styleNotResolved",
        "popupSourceNotFound",
        "labelSourceNotFound",
        "basemapNotFound",
    };

    /// <summary>
    /// Splits a structural-validation result into the structural failures that gate generation and the
    /// runtime-binding warnings that are deferred to publish.
    /// </summary>
    public static MapGenerationGateResult Evaluate(MapPackageValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);

        var structural = new List<MapValidationIssue>();
        var deferred = new List<MapValidationIssue>();
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

        return new MapGenerationGateResult(structural.Count == 0, structural, deferred);
    }
}

/// <summary>Outcome of the generation-lenient gate.</summary>
/// <param name="Passed">True when there are no structural failures (runtime-binding issues are tolerated).</param>
/// <param name="StructuralFailures">Structural issues the model must fix in the repair loop.</param>
/// <param name="DeferredBindings">Runtime-binding issues surfaced as warnings (bound at publish).</param>
internal sealed record MapGenerationGateResult(
    bool Passed,
    IReadOnlyList<MapValidationIssue> StructuralFailures,
    IReadOnlyList<MapValidationIssue> DeferredBindings);
