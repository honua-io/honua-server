// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.NlQuery.Domain;

namespace Honua.Ai.QueryGeneration;

/// <summary>
/// Generation-lenient structural gate over a generated <see cref="SavedQueryContent"/>. The deep
/// run/preview path compiles the <see cref="FilterPlan"/> against the live target layer's Metadata v2
/// schema (<c>FilterPlanCompiler</c>), resolving every property reference and the layer's geometry
/// field — data that is unknowable from a natural-language prompt and is bound when the saved query is
/// previewed or run, not when it is generated. So, exactly like the form and analysis generation gates
/// (which run a structural validator and tolerate target/layer binding as deferred "not found"),
/// query generation gates + repairs only on STRUCTURAL issues — the filter plan's combinators and
/// per-clause operators are in the supported vocabulary, each clause is well-formed (a comparison has a
/// property + value, a spatial clause has valid GeoJSON geometry and the dwithin distance, a temporal
/// clause has its required bounds), and the out-field projection is well-formed — and treats the layer
/// reference and field-schema resolution as deferred non-blocking warnings.
///
/// The validator below is DB-FREE: it walks only the generated structure (no Metadata v2 resource, no
/// catalog, no feature store) and NULL-GUARDS every collection/array access so a partial or malformed
/// model proposal can never throw. It mirrors the operator vocabularies enforced by
/// <c>FilterPlanCompiler</c> so a structurally-passing plan is one the compiler can later bind.
/// </summary>
internal static class QueryGenerationValidationGate
{
    // The operator vocabularies the canonical FilterPlanCompiler accepts. A plan whose operators fall
    // outside these sets can never compile, so they are structural failures the model must repair.
    private static readonly HashSet<string> ComparisonOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "neq", "lt", "lte", "gt", "gte", "like", "in", "isNull",
    };

    private static readonly HashSet<string> SpatialOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "intersects", "within", "contains", "dwithin",
    };

    private static readonly HashSet<string> TemporalOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "before", "after", "during",
    };

    private static readonly HashSet<string> ClauseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "comparison", "spatial", "temporal", "nested",
    };

    private const int MaxNestingDepth = 4;

    /// <summary>
    /// Runs the structural checks and splits the result into the structural failures that gate
    /// generation and the deferred run/preview-binding warnings. The layer reference is tolerated when
    /// unbound (a placeholder layerId of 0/negative is the "operator binds the real layer later"
    /// signal, surfaced as a deferred warning), the analysis counterpart to tolerating layerNotFound.
    /// </summary>
    public static QueryGenerationGateResult Evaluate(SavedQueryContent query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var structural = new List<QueryStructuralFailure>();
        var deferred = new List<QueryStructuralFailure>();

        // Layer reference: required for a runnable saved query, but the real numeric layerId is bound at
        // run/preview (the prompt rarely knows it). A non-positive placeholder is tolerated as deferred.
        if (query.LayerId <= 0)
        {
            deferred.Add(new QueryStructuralFailure(
                "LAYER_NOT_BOUND",
                "The query has no bound target layer; the operator binds the real layer id at run/preview time.",
                "layerId"));
        }

        ValidateFilterPlan(query.FilterPlan, structural, depth: 0, path: "filterPlan");
        ValidateOutFields(query.OutFields, structural);

        return new QueryGenerationGateResult(structural.Count == 0, structural, deferred);
    }

    private static void ValidateFilterPlan(
        FilterPlan? plan,
        List<QueryStructuralFailure> structural,
        int depth,
        string path)
    {
        // A query with no filter plan is a valid "select all" saved query; nothing structural to check.
        if (plan is null)
        {
            return;
        }

        ValidateClauses(plan.Clauses, structural, depth, path);
    }

    private static void ValidateClauses(
        FilterPlanClause[]? clauses,
        List<QueryStructuralFailure> structural,
        int depth,
        string path)
    {
        if (clauses is null || clauses.Length == 0)
        {
            return;
        }

        for (var i = 0; i < clauses.Length; i++)
        {
            var clause = clauses[i];
            var clausePath = $"{path}.clauses[{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}]";
            if (clause is null)
            {
                structural.Add(new QueryStructuralFailure("CLAUSE_NULL", "A filter clause is null.", clausePath));
                continue;
            }

            ValidateClause(clause, structural, depth, clausePath);
        }
    }

    private static void ValidateClause(
        FilterPlanClause clause,
        List<QueryStructuralFailure> structural,
        int depth,
        string path)
    {
        if (string.IsNullOrWhiteSpace(clause.Type) || !ClauseTypes.Contains(clause.Type))
        {
            structural.Add(new QueryStructuralFailure(
                "CLAUSE_TYPE_INVALID",
                $"Unknown clause type '{clause.Type}'. Use one of: comparison, spatial, temporal, nested.",
                $"{path}.type"));
            return;
        }

        switch (clause.Type.ToLowerInvariant())
        {
            case "comparison":
                ValidateComparison(clause.Comparison, structural, $"{path}.comparison");
                break;
            case "spatial":
                ValidateSpatial(clause.Spatial, structural, $"{path}.spatial");
                break;
            case "temporal":
                ValidateTemporal(clause.Temporal, structural, $"{path}.temporal");
                break;
            case "nested":
                ValidateNested(clause.Nested, structural, depth, $"{path}.nested");
                break;
            default:
                break;
        }
    }

    private static void ValidateComparison(
        ComparisonClause? comparison,
        List<QueryStructuralFailure> structural,
        string path)
    {
        if (comparison is null)
        {
            structural.Add(new QueryStructuralFailure("COMPARISON_MISSING", "Comparison clause data is missing.", path));
            return;
        }

        if (string.IsNullOrWhiteSpace(comparison.Property))
        {
            structural.Add(new QueryStructuralFailure("PROPERTY_MISSING", "Comparison clause has no property name.", $"{path}.property"));
        }

        if (string.IsNullOrWhiteSpace(comparison.Operator) || !ComparisonOperators.Contains(comparison.Operator))
        {
            structural.Add(new QueryStructuralFailure(
                "COMPARISON_OPERATOR_INVALID",
                $"Unsupported comparison operator '{comparison.Operator}'. Use one of: eq, neq, lt, lte, gt, gte, like, in, isNull.",
                $"{path}.operator"));
            return;
        }

        // "in" needs an array value; "isNull" needs no value; every other comparison needs a scalar value.
        if (string.Equals(comparison.Operator, "in", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsArrayValue(comparison.Value))
            {
                structural.Add(new QueryStructuralFailure(
                    "IN_VALUE_INVALID",
                    "The 'in' operator requires an array value.",
                    $"{path}.value"));
            }
        }
        else if (!string.Equals(comparison.Operator, "isNull", StringComparison.OrdinalIgnoreCase))
        {
            if (IsNullValue(comparison.Value))
            {
                structural.Add(new QueryStructuralFailure(
                    "COMPARISON_VALUE_MISSING",
                    $"The '{comparison.Operator}' operator requires a value.",
                    $"{path}.value"));
            }
        }
    }

    private static void ValidateSpatial(
        SpatialClause? spatial,
        List<QueryStructuralFailure> structural,
        string path)
    {
        if (spatial is null)
        {
            structural.Add(new QueryStructuralFailure("SPATIAL_MISSING", "Spatial clause data is missing.", path));
            return;
        }

        if (string.IsNullOrWhiteSpace(spatial.Operator) || !SpatialOperators.Contains(spatial.Operator))
        {
            structural.Add(new QueryStructuralFailure(
                "SPATIAL_OPERATOR_INVALID",
                $"Unsupported spatial operator '{spatial.Operator}'. Use one of: intersects, within, contains, dwithin.",
                $"{path}.operator"));
            return;
        }

        if (!IsValidGeometry(spatial.Geometry))
        {
            structural.Add(new QueryStructuralFailure(
                "SPATIAL_GEOMETRY_INVALID",
                "Spatial clause requires a non-empty GeoJSON geometry object.",
                $"{path}.geometry"));
        }

        if (string.Equals(spatial.Operator, "dwithin", StringComparison.OrdinalIgnoreCase)
            && spatial.Distance is not { } distance)
        {
            structural.Add(new QueryStructuralFailure(
                "SPATIAL_DISTANCE_MISSING",
                "The 'dwithin' operator requires a distance.",
                $"{path}.distance"));
        }
        else if (string.Equals(spatial.Operator, "dwithin", StringComparison.OrdinalIgnoreCase)
            && spatial.Distance is { } d && d <= 0)
        {
            structural.Add(new QueryStructuralFailure(
                "SPATIAL_DISTANCE_INVALID",
                "The 'dwithin' distance must be a positive number.",
                $"{path}.distance"));
        }
    }

    private static void ValidateTemporal(
        TemporalClause? temporal,
        List<QueryStructuralFailure> structural,
        string path)
    {
        if (temporal is null)
        {
            structural.Add(new QueryStructuralFailure("TEMPORAL_MISSING", "Temporal clause data is missing.", path));
            return;
        }

        if (string.IsNullOrWhiteSpace(temporal.Property))
        {
            structural.Add(new QueryStructuralFailure("PROPERTY_MISSING", "Temporal clause has no property name.", $"{path}.property"));
        }

        if (string.IsNullOrWhiteSpace(temporal.Operator) || !TemporalOperators.Contains(temporal.Operator))
        {
            structural.Add(new QueryStructuralFailure(
                "TEMPORAL_OPERATOR_INVALID",
                $"Unsupported temporal operator '{temporal.Operator}'. Use one of: before, after, during.",
                $"{path}.operator"));
            return;
        }

        var op = temporal.Operator.ToLowerInvariant();
        if ((op is "after" or "during") && string.IsNullOrWhiteSpace(temporal.Start))
        {
            structural.Add(new QueryStructuralFailure(
                "TEMPORAL_START_MISSING",
                $"The '{op}' operator requires a 'start' bound.",
                $"{path}.start"));
        }

        if ((op is "before" or "during") && string.IsNullOrWhiteSpace(temporal.End))
        {
            structural.Add(new QueryStructuralFailure(
                "TEMPORAL_END_MISSING",
                $"The '{op}' operator requires an 'end' bound.",
                $"{path}.end"));
        }
    }

    private static void ValidateNested(
        NestedClause? nested,
        List<QueryStructuralFailure> structural,
        int depth,
        string path)
    {
        if (nested is null)
        {
            structural.Add(new QueryStructuralFailure("NESTED_MISSING", "Nested clause data is missing.", path));
            return;
        }

        if (depth + 1 > MaxNestingDepth)
        {
            structural.Add(new QueryStructuralFailure(
                "NESTING_TOO_DEEP",
                $"Filter plan nesting exceeds the maximum depth of {MaxNestingDepth.ToString(System.Globalization.CultureInfo.InvariantCulture)}.",
                path));
            return;
        }

        if (nested.Clauses is null || nested.Clauses.Length == 0)
        {
            structural.Add(new QueryStructuralFailure("NESTED_EMPTY", "Nested clause contains no sub-clauses.", $"{path}.clauses"));
            return;
        }

        ValidateClauses(nested.Clauses, structural, depth + 1, path);
    }

    private static void ValidateOutFields(IReadOnlyList<string>? outFields, List<QueryStructuralFailure> structural)
    {
        // Empty out-fields means "all fields" and is valid. Only a blank/whitespace entry is structural.
        if (outFields is null || outFields.Count == 0)
        {
            return;
        }

        for (var i = 0; i < outFields.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(outFields[i]))
            {
                structural.Add(new QueryStructuralFailure(
                    "OUTFIELD_BLANK",
                    "Output field selection contains a blank field name.",
                    $"outFields[{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}]"));
            }
        }
    }

    private static bool IsNullValue(object? value)
    {
        if (value is null)
        {
            return true;
        }

        return value is JsonElement element && element.ValueKind == JsonValueKind.Null;
    }

    private static bool IsArrayValue(object? value)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Array;
        }

        return value is System.Collections.IEnumerable and not string;
    }

    private static bool IsValidGeometry(JsonElement? geometry)
    {
        if (geometry is not { } element)
        {
            return false;
        }

        // A well-formed GeoJSON geometry is a non-empty object. The deep compiler parses it fully; here
        // we only ensure it is structurally an object so the model cannot pass a null/scalar placeholder.
        return element.ValueKind == JsonValueKind.Object
            && element.EnumerateObject().Any();
    }

    /// <summary>Projects the gate result onto the public validation contract (errors + warnings).</summary>
    public static QueryGenerationValidation ToValidation(QueryGenerationGateResult gate)
    {
        ArgumentNullException.ThrowIfNull(gate);

        var issues = new List<QueryGenerationValidationIssue>(gate.StructuralFailures.Count + gate.DeferredBindings.Count);
        foreach (var failure in gate.StructuralFailures)
        {
            issues.Add(ToIssue(failure, "error"));
        }

        foreach (var failure in gate.DeferredBindings)
        {
            issues.Add(ToIssue(failure, "warning"));
        }

        return new QueryGenerationValidation { Issues = issues };
    }

    private static QueryGenerationValidationIssue ToIssue(QueryStructuralFailure failure, string severity) =>
        new()
        {
            Code = failure.Code,
            Message = failure.Message,
            Path = failure.FieldPath,
            Severity = severity,
        };
}

/// <summary>A single structural validation failure within a generated saved query.</summary>
/// <param name="Code">Machine-readable violation code.</param>
/// <param name="Message">Human-readable violation message.</param>
/// <param name="FieldPath">Path to the offending field, when applicable.</param>
internal sealed record QueryStructuralFailure(string Code, string Message, string? FieldPath);

/// <summary>Outcome of the generation-lenient gate.</summary>
/// <param name="Passed">True when there are no structural failures (deferred bindings are tolerated).</param>
/// <param name="StructuralFailures">Structural issues the model must fix in the repair loop.</param>
/// <param name="DeferredBindings">Run/preview-binding issues surfaced as warnings (bound at run/preview).</param>
internal sealed record QueryGenerationGateResult(
    bool Passed,
    IReadOnlyList<QueryStructuralFailure> StructuralFailures,
    IReadOnlyList<QueryStructuralFailure> DeferredBindings);
