// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.SpatialAnalytics.Domain;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Validates analysis plan steps against the process catalog, checking that
/// referenced process IDs exist, required parameters are supplied, values parse
/// cleanly against their declared <see cref="ProcessParameterValueType"/>, and
/// per-process semantic rules (enum values, conditional requiredness, numeric
/// ranges including <see cref="AnalyticsLimits"/> upper bounds) match the live
/// handler contracts so plans accepted here are also accepted downstream by
/// <c>SpatialAnalyticsRequestHandlers</c>.
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

    // Mirrors SpatialAnalyticsRequestHandlers.TryParseStatisticType so the
    // validator rejects the same statisticType values the handler would reject.
    private static readonly HashSet<string> StatisticTypeValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "count", "sum", "min", "max", "avg", "stddev", "var"
    };

    // Mirrors AnalyticsFeatureQueryFactory.IsDistanceBasedSpatialRelationship so
    // the validator rejects the same distance-based spatialRel values the
    // handler rejects (the analytics endpoints already overload `distance`).
    private static readonly HashSet<string> RejectedSpatialRelValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "esriSpatialRelWithinDistance", "esriSpatialRelBeyondDistance"
    };

    /// <summary>
    /// Validates all <see cref="AnalysisPlanStepKind.Geoprocess"/> steps in the plan
    /// against the catalog, returning any violations and warnings found. Uses the
    /// default <see cref="AnalyticsLimits"/> values when no configured instance is
    /// supplied — call the overload taking <see cref="AnalyticsLimits"/> to enforce
    /// the runtime-configured upper bounds the live handlers use.
    /// </summary>
    public static (List<GeoprocessingValidationFailure> Violations, List<string> Warnings) Validate(
        AnalysisPlan plan,
        IProcessCatalog catalog)
        => Validate(plan, catalog, new AnalyticsLimits());

    /// <summary>
    /// Validates the plan using the supplied <paramref name="analyticsLimits"/> so
    /// upper-bound checks (eps, k, distance, cellSize, buffer cap) match the
    /// bounds enforced by <c>SpatialAnalyticsRequestHandlers</c> at execution time.
    /// </summary>
    public static (List<GeoprocessingValidationFailure> Violations, List<string> Warnings) Validate(
        AnalysisPlan plan,
        IProcessCatalog catalog,
        AnalyticsLimits analyticsLimits)
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

            // Declared-required: key must be present. Blank declared-required
            // values fall through to type validation so callers get a concrete
            // type-specific INVALID_PARAMETER_VALUE hint (e.g. empty layerId).
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

            // Flag unknown parameter names up front so callers see typos before
            // other rules. Skips type/semantic validation for unknown params.
            var unknownInputs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (inputName, _) in step.Inputs)
            {
                if (paramsByName.ContainsKey(inputName))
                {
                    continue;
                }

                unknownInputs.Add(inputName);
                violations.Add(new GeoprocessingValidationFailure
                {
                    Code = "UNKNOWN_PARAMETER",
                    Message = $"Step '{step.StepId}' supplies unknown parameter '{inputName}' for process '{step.ProcessId}'.",
                    FieldPath = $"steps[{step.StepId}].inputs.{inputName}"
                });
            }

            // Semantic rules (enum, conditional requiredness, numeric bounds)
            // run before type validation so blank conditional values surface as
            // MISSING_REQUIRED_PARAMETER — matching handler error semantics — and
            // so range violations emit a single, bound-aware message per field.
            ApplyProcessSemantics(step, analyticsLimits, violations);

            foreach (var (inputName, inputValue) in step.Inputs)
            {
                if (unknownInputs.Contains(inputName))
                {
                    continue;
                }

                var spec = paramsByName[inputName];
                var fieldPath = $"steps[{step.StepId}].inputs.{inputName}";

                // Blank values for optional parameters are "not supplied" to the
                // live handlers (SpatialAnalytics*.cs uses IsNullOrWhiteSpace).
                // Skipping them here keeps handler/validator behavior aligned.
                if (!spec.Required && string.IsNullOrWhiteSpace(inputValue))
                {
                    continue;
                }

                // Suppress duplicate INVALID/MISSING when a semantic rule already
                // flagged this field (range, conditional required, enum).
                if (violations.Any(v => v.FieldPath == fieldPath))
                {
                    continue;
                }

                if (!IsValidForType(inputValue, spec.ValueType, out var typeErrorDetail))
                {
                    violations.Add(new GeoprocessingValidationFailure
                    {
                        Code = "INVALID_PARAMETER_VALUE",
                        Message = $"Step '{step.StepId}' supplies invalid value for parameter '{inputName}' of process '{step.ProcessId}': {typeErrorDetail}.",
                        FieldPath = fieldPath
                    });
                }
            }
        }

        return (violations, warnings);
    }

    /// <summary>
    /// Applies per-process semantic rules that the live request handlers enforce
    /// (enum value sets, conditional requiredness, numeric ranges including the
    /// upper bounds carried by <see cref="AnalyticsLimits"/>). Mirrors
    /// <c>SpatialAnalyticsRequestHandlers</c> so catalog validation does not
    /// admit plans the handlers will reject at execution time.
    /// </summary>
    private static void ApplyProcessSemantics(
        AnalysisPlanStep step,
        AnalyticsLimits analyticsLimits,
        List<GeoprocessingValidationFailure> violations)
    {
        switch (step.ProcessId)
        {
            case "analytics.cluster":
                ValidateClusterSemantics(step, analyticsLimits, violations);
                ApplySharedAnalyticsFilterSemantics(step, violations);
                break;
            case "analytics.spatial-join":
                ValidateSpatialJoinSemantics(step, analyticsLimits, violations);
                ApplySharedAnalyticsFilterSemantics(step, violations);
                break;
            case "analytics.density":
                ValidateDensitySemantics(step, analyticsLimits, violations);
                ApplySharedAnalyticsFilterSemantics(step, violations);
                break;
            case "analytics.buffer-aggregate":
                ValidateBufferAggregateSemantics(step, analyticsLimits, violations);
                ApplySharedAnalyticsFilterSemantics(step, violations);
                break;
        }
    }

    // Validates the structured Text inputs every analytics handler honors via
    // AnalyticsFeatureQueryFactory. Each parser is dependency-free so the
    // validator can apply the same rejections the handler would apply at
    // execution time, without needing IFilterExpressionService or layer metadata.
    private static void ApplySharedAnalyticsFilterSemantics(
        AnalysisPlanStep step,
        List<GeoprocessingValidationFailure> violations)
    {
        ValidateObjectIds(step, violations);

        // AnalyticsFeatureQueryFactory only inspects spatialRel inside its
        // geometry branch: if no `geometry` input is supplied, spatialRel is
        // never consulted and distance-based variants do not fail the request.
        // Gate the validator on the same signal so plans without geometry are
        // not rejected here for a value the handler would ignore.
        if (step.Inputs.TryGetValue("geometry", out var geometryRaw)
            && !string.IsNullOrWhiteSpace(geometryRaw))
        {
            ValidateSpatialRel(step, violations);
        }
    }

    private static void ValidateClusterSemantics(
        AnalysisPlanStep step,
        AnalyticsLimits analyticsLimits,
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

        // Cluster eps — handler rejects <= MinEps (0) and > MaxDbscanEpsMeters.
        RequireDoubleInRange(
            step, "eps",
            minimumExclusive: ClusterQuery.MinEps,
            maximum: analyticsLimits.MaxDbscanEpsMeters,
            maximumUnit: "meters",
            violations);

        // minPoints — handler rejects < MinMinPoints (1). No upper bound.
        RequireIntAtLeast(step, "minPoints", ClusterQuery.MinMinPoints, violations);

        // k — handler rejects < MinK (1) or > MaxKMeansK.
        RequireIntInRange(step, "k", ClusterQuery.MinK, analyticsLimits.MaxKMeansK, violations);

        // outStatistics JSON syntax (handler parses via TryParseOutStatisticsJson).
        ValidateOutStatistics(step, violations);

        // Cluster handler rejects outStatistics unless returnHullPerCluster=true:
        // per-feature output cannot carry GROUP BY aggregates. Mirror here so the
        // validator does not admit plans the handler will 400.
        var hasOutStatistics = step.Inputs.TryGetValue("outStatistics", out var clusterStats)
            && !string.IsNullOrWhiteSpace(clusterStats);
        var returnHull = step.Inputs.TryGetValue("returnHullPerCluster", out var hullRaw)
            && !string.IsNullOrWhiteSpace(hullRaw)
            && bool.TryParse(hullRaw, out var parsedHull)
            && parsedHull;
        if (hasOutStatistics && !returnHull)
        {
            AddRangeViolationIfNew(step, "outStatistics",
                "outStatistics requires returnHullPerCluster=true; per-feature cluster assignments cannot carry aggregate statistics",
                violations);
        }
    }

    private static void ValidateSpatialJoinSemantics(
        AnalysisPlanStep step,
        AnalyticsLimits analyticsLimits,
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

        // DWithin distance — handler rejects <= 0 and > MaxDWithinDistanceMeters.
        RequireDoubleInRange(
            step, "distance",
            minimumExclusive: 0d,
            maximum: analyticsLimits.MaxDWithinDistanceMeters,
            maximumUnit: "meters",
            violations);

        // Self-join guard: handler rejects joinLayerId == layerId because the SQL
        // builder cannot meaningfully self-join. Only fires when both ids parse
        // as ints; non-numeric ids are caught by the LayerId type validator.
        ValidateSpatialJoinJoinLayerId(step, violations);

        // outStatistics JSON syntax (handler parses via TryParseOutStatisticsJson).
        ValidateOutStatistics(step, violations);
    }

    private static void ValidateSpatialJoinJoinLayerId(
        AnalysisPlanStep step,
        List<GeoprocessingValidationFailure> violations)
    {
        if (!step.Inputs.TryGetValue("joinLayerId", out var joinRaw)
            || string.IsNullOrWhiteSpace(joinRaw)
            || !int.TryParse(joinRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var joinId))
        {
            return;
        }

        if (!step.Inputs.TryGetValue("layerId", out var layerRaw)
            || string.IsNullOrWhiteSpace(layerRaw)
            || !int.TryParse(layerRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
        {
            return;
        }

        if (joinId == layerId)
        {
            AddRangeViolationIfNew(step, "joinLayerId",
                "joinLayerId must differ from the target layerId (self-join is not supported)",
                violations);
        }
    }

    private static void ValidateDensitySemantics(
        AnalysisPlanStep step,
        AnalyticsLimits analyticsLimits,
        List<GeoprocessingValidationFailure> violations)
    {
        if (step.Inputs.TryGetValue("mode", out var modeRaw)
            && !string.IsNullOrWhiteSpace(modeRaw)
            && !DensityModeValues.Contains(modeRaw.Trim()))
        {
            AddEnumViolation(step, "mode", modeRaw, "hex, square", violations);
        }

        // cellSize — handler enforces Min/MaxDensityCellSizeMeters window (inclusive).
        RequireDoubleInClosedRange(
            step, "cellSize",
            minimum: analyticsLimits.MinDensityCellSizeMeters,
            maximum: analyticsLimits.MaxDensityCellSizeMeters,
            unit: "meters",
            violations);
    }

    private static void ValidateBufferAggregateSemantics(
        AnalysisPlanStep step,
        AnalyticsLimits analyticsLimits,
        List<GeoprocessingValidationFailure> violations)
    {
        var hasUnit = step.Inputs.TryGetValue("unit", out var unitRaw)
            && !string.IsNullOrWhiteSpace(unitRaw);
        var unitLabel = hasUnit ? unitRaw!.Trim() : "meters";

        if (hasUnit && !BufferAggregateUnitValues.Contains(unitLabel))
        {
            AddEnumViolation(step, "unit", unitLabel, "meters, kilometers, feet, miles", violations);
            return;
        }

        // Buffer distance — handler accepts >= 0 (non-negative) and enforces the
        // MaxBufferDistanceMeters cap after unit conversion so non-meter units
        // cannot bypass the limit.
        if (!step.Inputs.TryGetValue("distance", out var distanceRaw)
            || string.IsNullOrWhiteSpace(distanceRaw))
        {
            return;
        }

        if (!double.TryParse(distanceRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance)
            || double.IsNaN(distance) || double.IsInfinity(distance)
            || distance < BufferAggregateQuery.MinDistanceMeters)
        {
            AddRangeViolationIfNew(step, "distance",
                $"expected non-negative number ≥ {BufferAggregateQuery.MinDistanceMeters.ToString(CultureInfo.InvariantCulture)}, got '{distanceRaw}'",
                violations);
            return;
        }

        var unit = ResolveBufferDistanceUnit(unitLabel);
        var distanceInMeters = ConvertDistanceToMeters(distance, unit);
        if (distanceInMeters > analyticsLimits.MaxBufferDistanceMeters)
        {
            AddRangeViolationIfNew(step, "distance",
                $"distance must not exceed {analyticsLimits.MaxBufferDistanceMeters.ToString(CultureInfo.InvariantCulture)} meters (in the supplied unit), got '{distanceRaw}'",
                violations);
        }

        // outStatistics JSON syntax (handler parses via TryParseOutStatisticsJson).
        ValidateOutStatistics(step, violations);

        // Buffer-aggregate handler rejects outStatistics unless dissolve=true:
        // per-feature output cannot carry GROUP BY aggregates. Mirror here so
        // the validator does not admit plans the handler will 400.
        var hasOutStatistics = step.Inputs.TryGetValue("outStatistics", out var bufferStats)
            && !string.IsNullOrWhiteSpace(bufferStats);
        // dissolve defaults to true; unparseable strings keep the default so
        // the type validator's INVALID_PARAMETER_VALUE for the bad flag is the
        // only error surfaced (no spurious cross-field violation here).
        var dissolve = true;
        if (step.Inputs.TryGetValue("dissolve", out var dissolveRaw)
            && !string.IsNullOrWhiteSpace(dissolveRaw)
            && bool.TryParse(dissolveRaw, out var parsedDissolve))
        {
            dissolve = parsedDissolve;
        }
        if (hasOutStatistics && !dissolve)
        {
            AddRangeViolationIfNew(step, "outStatistics",
                "outStatistics requires dissolve=true; per-feature buffers cannot carry aggregate statistics",
                violations);
        }
    }

    // Mirrors AnalyticsFeatureQueryFactory.TryParseObjectIds — comma-separated
    // longs with empty entries skipped. Catches non-numeric inputs the handler
    // would 400 on, before they reach the SQL builder.
    private static void ValidateObjectIds(
        AnalysisPlanStep step,
        List<GeoprocessingValidationFailure> violations)
    {
        if (!step.Inputs.TryGetValue("objectIds", out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                AddRangeViolationIfNew(step, "objectIds",
                    $"expected comma-separated integer feature identifiers, got '{raw}'",
                    violations);
                return;
            }
        }
    }

    // Mirrors AnalyticsFeatureQueryFactory.IsDistanceBasedSpatialRelationship —
    // distance-based spatialRel values would collide with the operation-specific
    // `distance` parameter, so the handler 400s and the validator must too.
    private static void ValidateSpatialRel(
        AnalysisPlanStep step,
        List<GeoprocessingValidationFailure> violations)
    {
        if (!step.Inputs.TryGetValue("spatialRel", out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        if (RejectedSpatialRelValues.Contains(raw.Trim()))
        {
            AddRangeViolationIfNew(step, "spatialRel",
                $"distance-based spatial relationships (esriSpatialRelWithinDistance / esriSpatialRelBeyondDistance) are not supported; use the operation-specific 'distance' parameter or apply the predicate via the 'where' clause instead, got '{raw}'",
                violations);
        }
    }

    // Mirrors SpatialAnalyticsRequestHandlers.TryParseOutStatisticsJson — accepts
    // either a JSON array or a single object that the handler wraps into an
    // array, and enforces statisticType ∈ supported set + non-empty field names.
    private static void ValidateOutStatistics(
        AnalysisPlanStep step,
        List<GeoprocessingValidationFailure> violations)
    {
        if (!step.Inputs.TryGetValue("outStatistics", out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var fieldPath = $"steps[{step.StepId}].inputs.outStatistics";
        if (violations.Any(v => v.FieldPath == fieldPath))
        {
            return;
        }

        if (!TryValidateOutStatisticsJson(raw, out var error))
        {
            AddRangeViolationIfNew(step, "outStatistics", error, violations);
        }
    }

    private static bool TryValidateOutStatisticsJson(string raw, out string error)
    {
        error = "";
        // The handler wraps a single JSON object into a single-element array
        // before parsing, so the validator must accept the same shape.
        var json = raw.TrimStart().StartsWith('[') ? raw : $"[{raw}]";

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                error = "outStatistics must be a JSON array";
                return false;
            }

            foreach (var element in root.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    error = "each outStatistics entry must be a JSON object";
                    return false;
                }

                // Each field must be a JSON string — JsonElement.GetString()
                // throws InvalidOperationException for numeric/boolean/object
                // tokens, so syntactically valid JSON with the wrong value kind
                // would otherwise escape as an unhandled exception (500) rather
                // than surface as INVALID_PARAMETER_VALUE.
                if (!TryReadStringProperty(element, "statisticType", out var statisticType)
                    || !TryReadStringProperty(element, "onStatisticField", out var onField)
                    || !TryReadStringProperty(element, "outStatisticFieldName", out var outFieldName))
                {
                    error = "each outStatistics entry requires statisticType, onStatisticField, and outStatisticFieldName";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(statisticType)
                    || string.IsNullOrWhiteSpace(onField)
                    || string.IsNullOrWhiteSpace(outFieldName))
                {
                    error = "each outStatistics entry requires statisticType, onStatisticField, and outStatisticFieldName";
                    return false;
                }

                if (!StatisticTypeValues.Contains(statisticType))
                {
                    error = $"unsupported statisticType '{statisticType}' (allowed: count, sum, min, max, avg, stddev, var)";
                    return false;
                }
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"outStatistics is not valid JSON: {ex.Message}";
            return false;
        }
    }

    // Treats absent properties as null and non-string value kinds (numbers,
    // booleans, objects, arrays, null literal) as a validation failure so the
    // caller reports INVALID_PARAMETER_VALUE instead of propagating
    // JsonElement.GetString()'s InvalidOperationException.
    private static bool TryReadStringProperty(JsonElement element, string propertyName, out string? value)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            value = null;
            return true;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            value = null;
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static DistanceUnit ResolveBufferDistanceUnit(string unitLabel) => unitLabel.ToLowerInvariant() switch
    {
        "kilometers" or "kilometer" or "km" => DistanceUnit.Kilometers,
        "feet" or "foot" or "ft" => DistanceUnit.Feet,
        "miles" or "mile" or "mi" => DistanceUnit.Miles,
        _ => DistanceUnit.Meters,
    };

    // Mirrors SpatialAnalyticsRequestHandlers.BufferAggregate.ConvertDistanceToMeters
    // so validator caps agree bit-for-bit with the handler's pre-execute check.
    private static double ConvertDistanceToMeters(double distance, DistanceUnit unit) => unit switch
    {
        DistanceUnit.Kilometers => distance * 1000d,
        DistanceUnit.Feet => distance * 0.3048d,
        DistanceUnit.Miles => distance * 1609.344d,
        _ => distance,
    };

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

    private static void RequireDoubleInRange(
        AnalysisPlanStep step,
        string parameter,
        double minimumExclusive,
        double maximum,
        string maximumUnit,
        List<GeoprocessingValidationFailure> violations)
    {
        if (!step.Inputs.TryGetValue(parameter, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed) || double.IsInfinity(parsed)
            || parsed <= minimumExclusive)
        {
            AddRangeViolationIfNew(step, parameter, $"expected positive number, got '{value}'", violations);
            return;
        }

        if (parsed > maximum)
        {
            AddRangeViolationIfNew(step, parameter,
                $"must not exceed {maximum.ToString(CultureInfo.InvariantCulture)} {maximumUnit}, got '{value}'",
                violations);
        }
    }

    private static void RequireDoubleInClosedRange(
        AnalysisPlanStep step,
        string parameter,
        double minimum,
        double maximum,
        string unit,
        List<GeoprocessingValidationFailure> violations)
    {
        if (!step.Inputs.TryGetValue(parameter, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed) || double.IsInfinity(parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            AddRangeViolationIfNew(step, parameter,
                $"must be between {minimum.ToString(CultureInfo.InvariantCulture)} and {maximum.ToString(CultureInfo.InvariantCulture)} {unit}, got '{value}'",
                violations);
        }
    }

    private static void RequireIntAtLeast(
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

    private static void RequireIntInRange(
        AnalysisPlanStep step,
        string parameter,
        int minimum,
        int maximum,
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
            return;
        }

        if (parsed > maximum)
        {
            AddRangeViolationIfNew(step, parameter, $"must not exceed {maximum}, got '{value}'", violations);
        }
    }

    // Range checks emit one violation per field, even when multiple bounds or
    // type checks would each report the same parameter. Keeps error payloads
    // small enough for user-facing surfaces without hiding any offending field.
    private static void AddRangeViolationIfNew(
        AnalysisPlanStep step,
        string parameter,
        string detail,
        List<GeoprocessingValidationFailure> violations)
    {
        var fieldPath = $"steps[{step.StepId}].inputs.{parameter}";
        if (violations.Any(v => v.FieldPath == fieldPath))
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
                // Spatial analytics REST routes constrain {layerId:int} and the
                // handler joinLayerId path uses int.TryParse, so non-integer ids
                // are 400'd at execution. Validate the same shape here so plans
                // with opaque-string ids do not slip through the catalog gate.
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId)
                    || layerId <= 0)
                {
                    errorDetail = $"expected positive integer layer identifier, got '{value}'";
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
