// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Validates analysis plan steps against the process catalog, checking that
/// referenced process IDs exist, required parameters are supplied, values parse
/// cleanly against their declared <see cref="ProcessParameterValueType"/>, and
/// per-process semantic rules (enum values, conditional requiredness, positive
/// numeric ranges) match the live handler contracts so plans accepted here are
/// also accepted downstream by <c>SpatialAnalyticsRequestHandlers</c>.
/// </summary>
internal static class ProcessPlanValidator
{
    // Accepted enum values mirror the canonical spellings in the live handlers
    // (SpatialAnalyticsRequestHandlers.Clusters/SpatialJoin/Density/BufferAggregate).
    // Comparison is case-insensitive so validator and handler treat the same
    // caller input the same way.
    private static readonly HashSet<string> ClusterAlgorithmValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "dbscan", "kmeans", "k-means"
    };

    private static readonly HashSet<string> SpatialJoinPredicateValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "intersects", "contains", "within", "dwithin"
    };

    private static readonly HashSet<string> DensityModeValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "hex", "hexgrid", "hex-grid", "square", "squaregrid", "square-grid"
    };

    private static readonly HashSet<string> BufferAggregateUnitValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "meters", "meter", "m",
        "kilometers", "kilometer", "km",
        "feet", "foot", "ft",
        "miles", "mile", "mi"
    };

    /// <summary>
    /// Validates all <see cref="AnalysisPlanStepKind.Geoprocess"/> steps in the plan
    /// against the catalog, returning any violations and warnings found.
    /// </summary>
    public static (List<GeoprocessingValidationFailure> Violations, List<string> Warnings) Validate(
        AnalysisPlan plan,
        IProcessCatalog catalog)
    {
        var violations = new List<GeoprocessingValidationFailure>();
        var warnings = new List<string>();

        foreach (var step in plan.Steps)
        {
            if (step.Kind != AnalysisPlanStepKind.Geoprocess)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(step.ProcessId))
            {
                violations.Add(new GeoprocessingValidationFailure
                {
                    Code = "MISSING_PROCESS_ID",
                    Message = $"Geoprocess step '{step.StepId}' requires a process identifier.",
                    FieldPath = $"steps[{step.StepId}].process_id"
                });
                continue;
            }

            var definition = catalog.GetProcess(step.ProcessId);
            if (definition == null)
            {
                violations.Add(new GeoprocessingValidationFailure
                {
                    Code = "UNKNOWN_PROCESS",
                    Message = $"Process '{step.ProcessId}' referenced by step '{step.StepId}' is not in the catalog.",
                    FieldPath = $"steps[{step.StepId}].process_id"
                });
                continue;
            }

            var paramsByName = definition.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);

            foreach (var param in definition.Parameters)
            {
                if (!param.Required)
                {
                    continue;
                }

                if (!step.Inputs.ContainsKey(param.Name))
                {
                    violations.Add(new GeoprocessingValidationFailure
                    {
                        Code = "MISSING_REQUIRED_PARAMETER",
                        Message = $"Step '{step.StepId}' is missing required parameter '{param.Name}' for process '{step.ProcessId}'.",
                        FieldPath = $"steps[{step.StepId}].inputs.{param.Name}"
                    });
                }
            }

            foreach (var (inputName, inputValue) in step.Inputs)
            {
                if (!paramsByName.TryGetValue(inputName, out var spec))
                {
                    violations.Add(new GeoprocessingValidationFailure
                    {
                        Code = "UNKNOWN_PARAMETER",
                        Message = $"Step '{step.StepId}' supplies unknown parameter '{inputName}' for process '{step.ProcessId}'.",
                        FieldPath = $"steps[{step.StepId}].inputs.{inputName}"
                    });
                    continue;
                }

                if (!IsValidForType(inputValue, spec.ValueType, out var typeErrorDetail))
                {
                    violations.Add(new GeoprocessingValidationFailure
                    {
                        Code = "INVALID_PARAMETER_VALUE",
                        Message = $"Step '{step.StepId}' supplies invalid value for parameter '{inputName}' of process '{step.ProcessId}': {typeErrorDetail}.",
                        FieldPath = $"steps[{step.StepId}].inputs.{inputName}"
                    });
                }
            }

            ApplyProcessSemantics(step, violations);
        }

        return (violations, warnings);
    }

    /// <summary>
    /// Applies per-process semantic rules that the live request handlers enforce
    /// (enum value sets, conditional requiredness, positive numeric ranges).
    /// Mirrors <c>SpatialAnalyticsRequestHandlers</c> so catalog validation does
    /// not admit plans the handlers will reject at execution time.
    /// </summary>
    private static void ApplyProcessSemantics(
        AnalysisPlanStep step,
        List<GeoprocessingValidationFailure> violations)
    {
        switch (step.ProcessId)
        {
            case "analytics.cluster":
                ValidateClusterSemantics(step, violations);
                break;
            case "analytics.spatial-join":
                ValidateSpatialJoinSemantics(step, violations);
                break;
            case "analytics.density":
                ValidateDensitySemantics(step, violations);
                break;
            case "analytics.buffer-aggregate":
                ValidateBufferAggregateSemantics(step, violations);
                break;
        }
    }

    private static void ValidateClusterSemantics(
        AnalysisPlanStep step,
        List<GeoprocessingValidationFailure> violations)
    {
        // algorithm must be one of the canonical values; empty defaults to dbscan.
        var hasAlgorithm = step.Inputs.TryGetValue("algorithm", out var algorithmRaw)
            && !string.IsNullOrWhiteSpace(algorithmRaw);
        var algorithm = hasAlgorithm ? algorithmRaw!.Trim() : "dbscan";

        if (hasAlgorithm && !ClusterAlgorithmValues.Contains(algorithm))
        {
            AddEnumViolation(step, "algorithm", algorithm, "dbscan, kmeans", violations);
            return;
        }

        var isDbscan = string.Equals(algorithm, "dbscan", StringComparison.OrdinalIgnoreCase);
        var isKMeans = string.Equals(algorithm, "kmeans", StringComparison.OrdinalIgnoreCase)
            || string.Equals(algorithm, "k-means", StringComparison.OrdinalIgnoreCase);

        if (isDbscan)
        {
            RequireConditionalParameter(step, "eps", "algorithm=dbscan", violations);
            RequireConditionalParameter(step, "minPoints", "algorithm=dbscan", violations);
        }
        else if (isKMeans)
        {
            RequireConditionalParameter(step, "k", "algorithm=kmeans", violations);
        }

        RequirePositiveDouble(step, "eps", violations);
        RequirePositiveInt(step, "minPoints", minimum: 1, violations);
        RequirePositiveInt(step, "k", minimum: 1, violations);
    }

    private static void ValidateSpatialJoinSemantics(
        AnalysisPlanStep step,
        List<GeoprocessingValidationFailure> violations)
    {
        var hasPredicate = step.Inputs.TryGetValue("predicate", out var predicateRaw)
            && !string.IsNullOrWhiteSpace(predicateRaw);
        var predicate = hasPredicate ? predicateRaw!.Trim() : "intersects";

        if (hasPredicate && !SpatialJoinPredicateValues.Contains(predicate))
        {
            AddEnumViolation(step, "predicate", predicate, "intersects, contains, within, dwithin", violations);
            return;
        }

        if (string.Equals(predicate, "dwithin", StringComparison.OrdinalIgnoreCase))
        {
            RequireConditionalParameter(step, "distance", "predicate=dwithin", violations);
        }

        RequirePositiveDouble(step, "distance", violations);
    }

    private static void ValidateDensitySemantics(
        AnalysisPlanStep step,
        List<GeoprocessingValidationFailure> violations)
    {
        if (step.Inputs.TryGetValue("mode", out var modeRaw)
            && !string.IsNullOrWhiteSpace(modeRaw)
            && !DensityModeValues.Contains(modeRaw.Trim()))
        {
            AddEnumViolation(step, "mode", modeRaw, "hex, square", violations);
        }

        RequirePositiveDouble(step, "cellSize", violations);
    }

    private static void ValidateBufferAggregateSemantics(
        AnalysisPlanStep step,
        List<GeoprocessingValidationFailure> violations)
    {
        if (step.Inputs.TryGetValue("unit", out var unitRaw)
            && !string.IsNullOrWhiteSpace(unitRaw)
            && !BufferAggregateUnitValues.Contains(unitRaw.Trim()))
        {
            AddEnumViolation(step, "unit", unitRaw, "meters, kilometers, feet, miles", violations);
        }

        RequirePositiveDouble(step, "distance", violations);
    }

    private static void AddEnumViolation(
        AnalysisPlanStep step,
        string parameter,
        string actualValue,
        string allowedList,
        List<GeoprocessingValidationFailure> violations)
    {
        violations.Add(new GeoprocessingValidationFailure
        {
            Code = "INVALID_PARAMETER_VALUE",
            Message = $"Step '{step.StepId}' supplies invalid value for parameter '{parameter}' of process '{step.ProcessId}': '{actualValue}' is not in the allowed set ({allowedList}).",
            FieldPath = $"steps[{step.StepId}].inputs.{parameter}"
        });
    }

    private static void RequireConditionalParameter(
        AnalysisPlanStep step,
        string parameter,
        string condition,
        List<GeoprocessingValidationFailure> violations)
    {
        if (step.Inputs.TryGetValue(parameter, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        // Avoid duplicate MISSING_REQUIRED_PARAMETER if the declared-required
        // path already reported this parameter (it cannot in current catalog
        // since these are declared optional, but guard defensively).
        var fieldPath = $"steps[{step.StepId}].inputs.{parameter}";
        if (violations.Any(v => v.Code == "MISSING_REQUIRED_PARAMETER" && v.FieldPath == fieldPath))
        {
            return;
        }

        violations.Add(new GeoprocessingValidationFailure
        {
            Code = "MISSING_REQUIRED_PARAMETER",
            Message = $"Step '{step.StepId}' is missing required parameter '{parameter}' for process '{step.ProcessId}' when {condition}.",
            FieldPath = fieldPath
        });
    }

    private static void RequirePositiveDouble(
        AnalysisPlanStep step,
        string parameter,
        List<GeoprocessingValidationFailure> violations)
    {
        if (!step.Inputs.TryGetValue(parameter, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed) || double.IsInfinity(parsed)
            || parsed <= 0d)
        {
            AddRangeViolationIfNew(step, parameter, $"expected positive number, got '{value}'", violations);
        }
    }

    private static void RequirePositiveInt(
        AnalysisPlanStep step,
        string parameter,
        int minimum,
        List<GeoprocessingValidationFailure> violations)
    {
        if (!step.Inputs.TryGetValue(parameter, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum)
        {
            AddRangeViolationIfNew(step, parameter, $"expected integer ≥ {minimum}, got '{value}'", violations);
        }
    }

    // Range checks run after the type-validation pass, which already emits
    // INVALID_PARAMETER_VALUE for non-numeric text. Skip duplicates so callers
    // see one violation per field.
    private static void AddRangeViolationIfNew(
        AnalysisPlanStep step,
        string parameter,
        string detail,
        List<GeoprocessingValidationFailure> violations)
    {
        var fieldPath = $"steps[{step.StepId}].inputs.{parameter}";
        if (violations.Any(v => v.Code == "INVALID_PARAMETER_VALUE" && v.FieldPath == fieldPath))
        {
            return;
        }

        violations.Add(new GeoprocessingValidationFailure
        {
            Code = "INVALID_PARAMETER_VALUE",
            Message = $"Step '{step.StepId}' supplies invalid value for parameter '{parameter}' of process '{step.ProcessId}': {detail}.",
            FieldPath = fieldPath
        });
    }

    private static bool IsValidForType(string? value, ProcessParameterValueType type, out string errorDetail)
    {
        errorDetail = "";

        if (value is null)
        {
            errorDetail = "value must not be null";
            return false;
        }

        switch (type)
        {
            case ProcessParameterValueType.Text:
                return true;

            case ProcessParameterValueType.WholeNumber:
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    errorDetail = $"expected 32-bit integer, got '{value}'";
                    return false;
                }
                return true;

            case ProcessParameterValueType.FloatingPoint:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl)
                    || double.IsNaN(dbl)
                    || double.IsInfinity(dbl))
                {
                    errorDetail = $"expected finite floating-point number, got '{value}'";
                    return false;
                }
                return true;

            case ProcessParameterValueType.Flag:
                if (!bool.TryParse(value, out _))
                {
                    errorDetail = $"expected boolean flag ('true' or 'false'), got '{value}'";
                    return false;
                }
                return true;

            case ProcessParameterValueType.Srid:
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid) || srid <= 0)
                {
                    errorDetail = $"expected positive SRID, got '{value}'";
                    return false;
                }
                return true;

            case ProcessParameterValueType.Wkb:
                if (!TryDecodeBase64NonEmpty(value))
                {
                    errorDetail = "expected base64-encoded WKB";
                    return false;
                }
                return true;

            case ProcessParameterValueType.WkbArray:
                if (!TryDecodeWkbArray(value, out var arrayError))
                {
                    errorDetail = arrayError;
                    return false;
                }
                return true;

            case ProcessParameterValueType.LayerId:
                if (string.IsNullOrWhiteSpace(value))
                {
                    errorDetail = "expected non-empty layer identifier";
                    return false;
                }
                return true;

            default:
                return true;
        }
    }

    private static bool TryDecodeBase64NonEmpty(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out var written) && written > 0;
    }

    private static bool TryDecodeWkbArray(string value, out string errorDetail)
    {
        errorDetail = "";

        if (string.IsNullOrWhiteSpace(value))
        {
            errorDetail = "expected JSON array of base64-encoded WKB strings";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                errorDetail = "expected JSON array of base64-encoded WKB strings";
                return false;
            }

            var index = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    errorDetail = $"WKB array element at index {index} is not a string";
                    return false;
                }

                var item = element.GetString();
                if (item is null || !TryDecodeBase64NonEmpty(item))
                {
                    errorDetail = $"WKB array element at index {index} is not a valid base64 WKB string";
                    return false;
                }

                index++;
            }

            if (index == 0)
            {
                errorDetail = "WKB array must contain at least one geometry";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            errorDetail = $"WKB array is not valid JSON: {ex.Message}";
            return false;
        }
    }
}
