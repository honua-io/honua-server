// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;

namespace Honua.Ai.AnalysisGeneration;

/// <summary>
/// Generation-lenient gate over <see cref="ProcessPlanValidator"/>. The deep run/publish path resolves
/// each plan step's input layer against live catalog metadata and the execution engine — data that is
/// unknowable from a natural-language prompt and is bound when the analysis is run or published, not
/// when it is generated. So, exactly like the form generation gate (which runs the structural
/// <c>FormPackageValidator</c> checks and tolerates target binding as a deferred "not found"), analysis
/// generation gates + repairs only on STRUCTURAL issues — the method (processId) is in the catalog,
/// required parameters are present, values parse against their declared type, and per-process semantic
/// rules hold — and treats input-layer-resolution issues as non-blocking warnings.
///
/// <see cref="ProcessPlanValidator"/> is itself DB-free: it validates each Geoprocess step against the
/// process catalog vocabulary without touching layer metadata, so it is the natural structural gate.
/// Its failures are classified by code: the runtime-binding codes below (should the validator ever
/// surface them) are tolerated during generation; everything else is a structural failure the model
/// must fix in the bounded repair loop.
/// </summary>
internal static class AnalysisGenerationValidationGate
{
    /// <summary>
    /// Violation codes that depend on the real input layer or execution context (resolved at run/publish
    /// time), not on the structural correctness of the generated plan. Tolerated during generation, the
    /// analysis counterpart to the form gate tolerating <c>serviceNotFound</c>/<c>layerNotFound</c>.
    /// </summary>
    private static readonly HashSet<string> RuntimeBindingCodes = new(StringComparer.Ordinal)
    {
        "UNKNOWN_LAYER",
        "LAYER_NOT_FOUND",
        "INPUT_NOT_RESOLVED",
        "UNKNOWN_DATASET",
    };

    /// <summary>
    /// Runs the structural validator and splits its result into the structural failures that gate
    /// generation and the runtime-binding warnings that are deferred to run/publish.
    /// </summary>
    public static AnalysisGenerationGateResult Evaluate(AnalysisPlan plan, IProcessCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(catalog);

        var (violations, _) = ProcessPlanValidator.Validate(plan, catalog);

        var structural = new List<GeoprocessingValidationFailure>();
        var deferred = new List<GeoprocessingValidationFailure>();
        foreach (var violation in violations)
        {
            if (RuntimeBindingCodes.Contains(violation.Code))
            {
                deferred.Add(violation);
            }
            else
            {
                structural.Add(violation);
            }
        }

        return new AnalysisGenerationGateResult(structural.Count == 0, structural, deferred);
    }

    /// <summary>Projects the gate result onto the public validation contract (errors + warnings).</summary>
    public static AnalysisGenerationValidation ToValidation(AnalysisGenerationGateResult gate)
    {
        ArgumentNullException.ThrowIfNull(gate);

        var issues = new List<AnalysisGenerationValidationIssue>(gate.StructuralFailures.Count + gate.DeferredBindings.Count);
        foreach (var failure in gate.StructuralFailures)
        {
            issues.Add(ToIssue(failure, "error"));
        }

        foreach (var failure in gate.DeferredBindings)
        {
            issues.Add(ToIssue(failure, "warning"));
        }

        return new AnalysisGenerationValidation { Issues = issues };
    }

    private static AnalysisGenerationValidationIssue ToIssue(GeoprocessingValidationFailure failure, string severity) =>
        new()
        {
            Code = failure.Code,
            Message = failure.Message,
            Path = failure.FieldPath,
            Severity = severity
        };
}

/// <summary>Outcome of the generation-lenient gate.</summary>
/// <param name="Passed">True when there are no structural failures (runtime-binding issues are tolerated).</param>
/// <param name="StructuralFailures">Structural issues the model must fix in the repair loop.</param>
/// <param name="DeferredBindings">Runtime-binding issues surfaced as warnings (bound at run/publish).</param>
internal sealed record AnalysisGenerationGateResult(
    bool Passed,
    IReadOnlyList<GeoprocessingValidationFailure> StructuralFailures,
    IReadOnlyList<GeoprocessingValidationFailure> DeferredBindings);
