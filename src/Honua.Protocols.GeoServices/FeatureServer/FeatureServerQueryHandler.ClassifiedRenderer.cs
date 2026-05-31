// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Infrastructure.Models;

namespace Honua.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Classified <c>generateRenderer</c> support (classBreaksDef + uniqueValueDef).
/// Both paths reuse the shared FeatureServer query/statistics executor rather
/// than building a parallel data access path.
/// </summary>
internal sealed partial class FeatureServerQueryHandler
{
    private const int MaxClassBreakCount = 32;
    private const int MaxUniqueValueCount = 256;
    private const int MaxUniqueValueFields = 3;

    // Categorical color ramp (Esri-style RGBA, alpha 255). The renderer varies
    // the symbol color across this ramp; fills are emitted with a reduced alpha
    // so polygon outlines remain visible.
    private static readonly int[][] RendererColorRamp =
    [
        [ 31, 119, 180 ],
        [ 255, 127, 14 ],
        [ 44, 160, 44 ],
        [ 214, 39, 40 ],
        [ 148, 103, 189 ],
        [ 140, 86, 75 ],
        [ 227, 119, 194 ],
        [ 127, 127, 127 ],
        [ 188, 189, 34 ],
        [ 23, 190, 207 ],
    ];

    /// <summary>
    /// Builds a classified renderer (<c>classBreaks</c> or <c>uniqueValue</c>)
    /// for the supplied <c>classificationDef</c>. Returns an Esri renderer JSON
    /// document on success, or a problem result describing the validation error.
    /// </summary>
    public async Task<IResult> HandleGenerateClassifiedRendererAsync(
        string serviceId,
        int layerId,
        JsonElement classificationDef,
        MetadataV2GeometryType geometryType,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var (layerContext, validationError) = await ResolveQueryLayerContextV2Async(
            serviceId,
            layerId,
            context,
            FeatureServerProtocol,
            cancellationToken).ConfigureAwait(false);
        if (validationError != null)
        {
            return validationError;
        }

        var queryLayer = layerContext!;

        // Reject geometry types we cannot symbolize (mirrors the simple-renderer path).
        if (BuildClassifiedSymbol(geometryType, RendererColorRamp[0]) is null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Layer geometry type is not supported");
        }

        if (!classificationDef.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid classificationDef",
                ["classificationDef.type must be 'classBreaksDef' or 'uniqueValueDef'."]);
        }

        var classificationType = typeElement.GetString();
        if (string.Equals(classificationType, "classBreaksDef", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildClassBreaksRendererAsync(
                queryLayer, classificationDef, geometryType, context, serviceId, layerId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.Equals(classificationType, "uniqueValueDef", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildUniqueValueRendererAsync(
                queryLayer, classificationDef, geometryType, context, serviceId, layerId, cancellationToken)
                .ConfigureAwait(false);
        }

        return StandardErrorHelpers.CreateBadRequest(context,
            "Unsupported classificationDef type",
            [$"Unsupported classificationDef.type '{classificationType}'. Supported: classBreaksDef, uniqueValueDef."]);
    }

    private async Task<IResult> BuildClassBreaksRendererAsync(
        QueryLayerContext queryLayer,
        JsonElement classificationDef,
        MetadataV2GeometryType geometryType,
        HttpContext context,
        string serviceId,
        int layerId,
        CancellationToken cancellationToken)
    {
        if (!TryGetStringProperty(classificationDef, "classificationField", out var classificationField))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid classificationDef",
                ["classBreaksDef requires a 'classificationField'."]);
        }

        if (!TryResolveNumericField(queryLayer.Resource, classificationField, out var fieldType))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid classificationField",
                [$"Field '{classificationField}' does not exist or is not numeric on the layer."]);
        }

        string? normalizationField = null;
        if (TryGetStringProperty(classificationDef, "normalizationField", out var normField))
        {
            if (!TryResolveNumericField(queryLayer.Resource, normField, out _))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid normalizationField",
                    [$"Field '{normField}' does not exist or is not numeric on the layer."]);
            }

            normalizationField = normField;
        }

        var breakCount = 5;
        if (classificationDef.TryGetProperty("breakCount", out var breakCountElement)
            && breakCountElement.ValueKind == JsonValueKind.Number
            && breakCountElement.TryGetInt32(out var parsedBreakCount))
        {
            breakCount = parsedBreakCount;
        }

        if (breakCount < 1 || breakCount > MaxClassBreakCount)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid breakCount",
                [$"breakCount must be between 1 and {MaxClassBreakCount}."]);
        }

        var method = ClassificationMethod.EqualInterval;
        if (TryGetStringProperty(classificationDef, "classificationMethod", out var methodName)
            && !TryParseClassificationMethod(methodName, out method))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Unsupported classificationMethod",
                [$"Unsupported classificationMethod '{methodName}'. Supported: esriClassifyEqualInterval, esriClassifyQuantile, esriClassifyNaturalBreaks, esriClassifyStandardDeviation."]);
        }

        // Normalization is only meaningful when the renderer ratios two fields;
        // computing the breaks over a normalized field requires materializing the
        // ratio values which is out of scope for the statistics fast paths.
        if (normalizationField is not null
            && method is ClassificationMethod.Quantile or ClassificationMethod.NaturalBreaks)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Unsupported normalization",
                ["normalizationField is only supported with esriClassifyEqualInterval or esriClassifyStandardDeviation."]);
        }

        double[] breaks;
        double minValue;
        try
        {
            (minValue, breaks) = await ComputeClassBreaksAsync(
                queryLayer, classificationField, fieldType, method, breakCount, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (IsClientSafeInvalidOperation(ex))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Invalid classification", [ex.Message]);
        }

        if (breaks.Length == 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Insufficient data",
                ["The layer has no non-null values for the classification field; cannot generate class breaks."]);
        }

        var classBreakInfos = new Dictionary<string, object?>[breaks.Length];
        var previous = minValue;
        for (var i = 0; i < breaks.Length; i++)
        {
            var max = breaks[i];
            var color = RampColor(i, breaks.Length);
            classBreakInfos[i] = new Dictionary<string, object?>
            {
                ["classMinValue"] = previous,
                ["classMaxValue"] = max,
                ["label"] = FormatRangeLabel(previous, max),
                ["symbol"] = BuildClassifiedSymbol(geometryType, color)
            };
            previous = max;
        }

        var renderer = new Dictionary<string, object?>
        {
            ["type"] = "classBreaks",
            ["field"] = classificationField,
            ["classificationMethod"] = ToEsriMethodName(method),
            ["minValue"] = minValue,
            ["classBreakInfos"] = classBreakInfos
        };

        if (normalizationField is not null)
        {
            renderer["normalizationType"] = "esriNormalizeByField";
            renderer["normalizationField"] = normalizationField;
        }

        FeatureServerLog.QueryExecuted(_logger, "generateRenderer.classBreaks", serviceId, layerId, 0);
        return Results.Json(renderer, FeatureServerJsonContext.Default.DictionaryStringObject, contentType: "application/json");
    }

    private async Task<IResult> BuildUniqueValueRendererAsync(
        QueryLayerContext queryLayer,
        JsonElement classificationDef,
        MetadataV2GeometryType geometryType,
        HttpContext context,
        string serviceId,
        int layerId,
        CancellationToken cancellationToken)
    {
        if (!classificationDef.TryGetProperty("uniqueValueFields", out var fieldsElement)
            || fieldsElement.ValueKind != JsonValueKind.Array)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid classificationDef",
                ["uniqueValueDef requires a 'uniqueValueFields' array with 1 to 3 fields."]);
        }

        var fields = new List<string>();
        foreach (var element in fieldsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid uniqueValueFields",
                    ["uniqueValueFields must contain field-name strings."]);
            }

            var fieldName = element.GetString();
            if (string.IsNullOrWhiteSpace(fieldName) || !TryResolveField(queryLayer.Resource, fieldName, out _))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid uniqueValueFields",
                    [$"Field '{fieldName}' does not exist on the layer."]);
            }

            fields.Add(fieldName);
        }

        if (fields.Count == 0 || fields.Count > MaxUniqueValueFields)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid uniqueValueFields",
                ["uniqueValueFields must contain between 1 and 3 fields."]);
        }

        var delimiter = ",";
        if (TryGetStringProperty(classificationDef, "fieldDelimiter", out var parsedDelimiter)
            && parsedDelimiter.Length > 0)
        {
            delimiter = parsedDelimiter;
        }

        ImmutableArray<ImmutableArray<string?>> distinctRows;
        try
        {
            distinctRows = await ComputeDistinctValuesAsync(queryLayer, fields, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (IsClientSafeInvalidOperation(ex))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Invalid classification", [ex.Message]);
        }

        var uniqueValueInfos = new Dictionary<string, object?>[distinctRows.Length];
        for (var i = 0; i < distinctRows.Length; i++)
        {
            var row = distinctRows[i];
            var value = string.Join(delimiter, row.Select(static v => v ?? string.Empty));
            var color = RampColor(i, distinctRows.Length);
            uniqueValueInfos[i] = new Dictionary<string, object?>
            {
                ["value"] = value,
                ["label"] = value,
                ["symbol"] = BuildClassifiedSymbol(geometryType, color)
            };
        }

        var renderer = new Dictionary<string, object?>
        {
            ["type"] = "uniqueValue",
            ["field1"] = fields[0],
            ["field2"] = fields.Count > 1 ? fields[1] : null,
            ["field3"] = fields.Count > 2 ? fields[2] : null,
            ["fieldDelimiter"] = delimiter,
            ["uniqueValueInfos"] = uniqueValueInfos
        };

        FeatureServerLog.QueryExecuted(_logger, "generateRenderer.uniqueValue", serviceId, layerId, 0);
        return Results.Json(renderer, FeatureServerJsonContext.Default.DictionaryStringObject, contentType: "application/json");
    }

    /// <summary>
    /// Computes the class-break upper bounds (and the minimum value) for the
    /// classification field. EqualInterval and StandardDeviation derive breaks
    /// from the shared statistics executor (min/max/avg/stddev); Quantile and
    /// NaturalBreaks materialize the ordered field values through the shared
    /// query executor and compute breaks in-memory.
    /// </summary>
    private async Task<(double MinValue, double[] Breaks)> ComputeClassBreaksAsync(
        QueryLayerContext queryLayer,
        string classificationField,
        MetadataV2FieldType fieldType,
        ClassificationMethod method,
        int breakCount,
        CancellationToken cancellationToken)
    {
        var statisticsQuery = new FeatureQuery
        {
            OutStatistics =
            [
                new StatisticDefinition { StatisticType = StatisticType.Min, OnStatisticField = classificationField, OutStatisticFieldName = "min_value", FieldType = fieldType },
                new StatisticDefinition { StatisticType = StatisticType.Max, OnStatisticField = classificationField, OutStatisticFieldName = "max_value", FieldType = fieldType },
                new StatisticDefinition { StatisticType = StatisticType.Avg, OnStatisticField = classificationField, OutStatisticFieldName = "avg_value", FieldType = fieldType },
                new StatisticDefinition { StatisticType = StatisticType.Stddev, OnStatisticField = classificationField, OutStatisticFieldName = "stddev_value", FieldType = fieldType },
                new StatisticDefinition { StatisticType = StatisticType.Count, OnStatisticField = classificationField, OutStatisticFieldName = "count_value", FieldType = fieldType }
            ]
        };

        var statisticsRows = await _queryExecutor.QueryStatisticsAsync(
            queryLayer.Service,
            queryLayer.Resource,
            queryLayer.Publication,
            queryLayer.StorageLayerId,
            statisticsQuery,
            cancellationToken).ConfigureAwait(false);

        if (statisticsRows.IsDefaultOrEmpty)
        {
            return (0d, []);
        }

        var stats = statisticsRows[0];
        var count = (long)(ToDouble(GetStat(stats, "count_value")) ?? 0d);
        if (count <= 0)
        {
            return (0d, []);
        }

        var min = ToDouble(GetStat(stats, "min_value"));
        var max = ToDouble(GetStat(stats, "max_value"));
        if (!min.HasValue || !max.HasValue)
        {
            return (0d, []);
        }

        if (max.Value <= min.Value)
        {
            // Degenerate range: a single class covering the only value.
            return (min.Value, [max.Value]);
        }

        switch (method)
        {
            case ClassificationMethod.EqualInterval:
                return (min.Value, BuildEqualIntervalBreaks(min.Value, max.Value, breakCount));

            case ClassificationMethod.StandardDeviation:
            {
                var avg = ToDouble(GetStat(stats, "avg_value"));
                var stddev = ToDouble(GetStat(stats, "stddev_value"));
                if (!avg.HasValue || !stddev.HasValue || stddev.Value <= 0)
                {
                    // Fall back to equal interval when stddev is unavailable/zero.
                    return (min.Value, BuildEqualIntervalBreaks(min.Value, max.Value, breakCount));
                }

                return (min.Value, BuildStandardDeviationBreaks(min.Value, max.Value, avg.Value, stddev.Value));
            }

            case ClassificationMethod.Quantile:
            case ClassificationMethod.NaturalBreaks:
            {
                var values = await MaterializeOrderedValuesAsync(queryLayer, classificationField, cancellationToken)
                    .ConfigureAwait(false);
                if (values.Length == 0)
                {
                    return (min.Value, []);
                }

                var breaks = method == ClassificationMethod.Quantile
                    ? BuildQuantileBreaks(values, breakCount)
                    : BuildNaturalBreaks(values, breakCount);
                return (values[0], breaks);
            }

            default:
                return (min.Value, BuildEqualIntervalBreaks(min.Value, max.Value, breakCount));
        }
    }

    private async Task<double[]> MaterializeOrderedValuesAsync(
        QueryLayerContext queryLayer,
        string classificationField,
        CancellationToken cancellationToken)
    {
        var query = new FeatureQuery
        {
            OutFields = [classificationField],
            ExcludeAttributes = false,
            OrderBy = [new OrderByClause(classificationField, ascending: true)]
        };

        var result = await _queryExecutor.QueryWithValidationAsync(
            queryLayer.Service,
            queryLayer.Resource,
            queryLayer.Publication,
            queryLayer.StorageLayerId,
            query,
            cancellationToken).ConfigureAwait(false);

        if (result.Items.IsDefaultOrEmpty)
        {
            return [];
        }

        var values = new List<double>(result.Items.Length);
        foreach (var feature in result.Items)
        {
            if (feature.Attributes.TryGetValue(classificationField, out var raw))
            {
                var value = ToDouble(raw);
                if (value.HasValue)
                {
                    values.Add(value.Value);
                }
            }
        }

        values.Sort();
        return [.. values];
    }

    private async Task<ImmutableArray<ImmutableArray<string?>>> ComputeDistinctValuesAsync(
        QueryLayerContext queryLayer,
        List<string> fields,
        CancellationToken cancellationToken)
    {
        var query = new FeatureQuery
        {
            OutFields = [.. fields],
            Distinct = true,
            OrderBy = [.. fields.Select(static f => new OrderByClause(f, ascending: true))]
        };

        var result = await _queryExecutor.QueryWithValidationAsync(
            queryLayer.Service,
            queryLayer.Resource,
            queryLayer.Publication,
            queryLayer.StorageLayerId,
            query,
            cancellationToken).ConfigureAwait(false);

        if (result.Items.IsDefaultOrEmpty)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = ImmutableArray.CreateBuilder<ImmutableArray<string?>>();
        foreach (var feature in result.Items)
        {
            var row = ImmutableArray.CreateBuilder<string?>(fields.Count);
            foreach (var field in fields)
            {
                row.Add(feature.Attributes.TryGetValue(field, out var raw) ? FormatValue(raw) : null);
            }

            var rowValues = row.ToImmutable();
            var key = string.Join("\u001F", rowValues.Select(static v => v ?? " "));
            if (seen.Add(key))
            {
                rows.Add(rowValues);
                if (rows.Count >= MaxUniqueValueCount)
                {
                    break;
                }
            }
        }

        return rows.ToImmutable();
    }

    private static double[] BuildEqualIntervalBreaks(double min, double max, int breakCount)
    {
        var breaks = new double[breakCount];
        var step = (max - min) / breakCount;
        for (var i = 0; i < breakCount; i++)
        {
            breaks[i] = i == breakCount - 1 ? max : min + (step * (i + 1));
        }

        return breaks;
    }

    private static double[] BuildStandardDeviationBreaks(double min, double max, double avg, double stddev)
    {
        // Classic 1-stddev interval breaks clamped to the observed range.
        var raw = new List<double>();
        for (var k = -3; k <= 3; k++)
        {
            var boundary = avg + (k * stddev);
            if (boundary > min && boundary < max)
            {
                raw.Add(boundary);
            }
        }

        raw.Add(max);
        raw.Sort();

        var breaks = new List<double>(raw.Count);
        foreach (var value in raw)
        {
            if (breaks.Count == 0 || value > breaks[^1])
            {
                breaks.Add(value);
            }
        }

        return [.. breaks];
    }

    private static double[] BuildQuantileBreaks(double[] sortedValues, int breakCount)
    {
        var breaks = new List<double>(breakCount);
        var n = sortedValues.Length;
        for (var i = 1; i <= breakCount; i++)
        {
            double boundary;
            if (i == breakCount)
            {
                boundary = sortedValues[n - 1];
            }
            else
            {
                var rank = (double)i / breakCount * (n - 1);
                var lower = (int)Math.Floor(rank);
                var upper = (int)Math.Ceiling(rank);
                var fraction = rank - lower;
                boundary = sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * fraction);
            }

            if (breaks.Count == 0 || boundary > breaks[^1])
            {
                breaks.Add(boundary);
            }
        }

        if (breaks.Count == 0)
        {
            breaks.Add(sortedValues[n - 1]);
        }

        return [.. breaks];
    }

    private static double[] BuildNaturalBreaks(double[] sortedValues, int breakCount)
    {
        var n = sortedValues.Length;
        if (n <= breakCount)
        {
            // Each distinct value becomes its own class boundary.
            return [.. sortedValues.Distinct()];
        }

        // Jenks natural breaks (Fisher-Jenks dynamic programming).
        var mat1 = new int[n + 1, breakCount + 1];
        var mat2 = new double[n + 1, breakCount + 1];

        for (var i = 1; i <= breakCount; i++)
        {
            mat1[1, i] = 1;
            mat2[1, i] = 0;
            for (var j = 2; j <= n; j++)
            {
                mat2[j, i] = double.PositiveInfinity;
            }
        }

        for (var l = 2; l <= n; l++)
        {
            var sum = 0d;
            var sumSquares = 0d;
            var count = 0d;
            for (var m = 1; m <= l; m++)
            {
                var lowerClassLimit = l - m + 1;
                var value = sortedValues[lowerClassLimit - 1];
                count++;
                sum += value;
                sumSquares += value * value;
                var variance = sumSquares - ((sum * sum) / count);
                var i4 = lowerClassLimit - 1;
                if (i4 != 0)
                {
                    for (var j = 2; j <= breakCount; j++)
                    {
                        if (mat2[l, j] >= variance + mat2[i4, j - 1])
                        {
                            mat1[l, j] = lowerClassLimit;
                            mat2[l, j] = variance + mat2[i4, j - 1];
                        }
                    }
                }
            }

            mat1[l, 1] = 1;
            mat2[l, 1] = sumSquares - ((sum * sum) / count);
        }

        var breaks = new double[breakCount];
        var k = n;
        for (var j = breakCount; j >= 1; j--)
        {
            var id = mat1[k, j] - 1;
            breaks[j - 1] = j == breakCount ? sortedValues[n - 1] : sortedValues[k - 1];
            k = id;
        }

        // Deduplicate ascending boundaries.
        var result = new List<double>(breakCount);
        foreach (var value in breaks)
        {
            if (result.Count == 0 || value > result[^1])
            {
                result.Add(value);
            }
        }

        if (result.Count == 0 || result[^1] < sortedValues[n - 1])
        {
            result.Add(sortedValues[n - 1]);
        }

        return [.. result];
    }

    private static Dictionary<string, object?>? BuildClassifiedSymbol(MetadataV2GeometryType geometryType, int[] rgb)
    {
        var strokeColor = new[] { rgb[0], rgb[1], rgb[2], 255 };
        var fillColor = new[] { rgb[0], rgb[1], rgb[2], 130 };
        var outline = new Dictionary<string, object?>
        {
            ["type"] = "esriSLS",
            ["style"] = "esriSLSSolid",
            ["color"] = new[] { rgb[0], rgb[1], rgb[2], 255 },
            ["width"] = 1
        };

        return geometryType switch
        {
            MetadataV2GeometryType.Point or MetadataV2GeometryType.MultiPoint => new Dictionary<string, object?>
            {
                ["type"] = "esriSMS",
                ["style"] = "esriSMSCircle",
                ["color"] = strokeColor,
                ["size"] = 6,
                ["outline"] = outline
            },
            MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString => new Dictionary<string, object?>
            {
                ["type"] = "esriSLS",
                ["style"] = "esriSLSSolid",
                ["color"] = strokeColor,
                ["width"] = 2
            },
            MetadataV2GeometryType.Polygon or MetadataV2GeometryType.MultiPolygon => new Dictionary<string, object?>
            {
                ["type"] = "esriSFS",
                ["style"] = "esriSFSSolid",
                ["color"] = fillColor,
                ["outline"] = outline
            },
            _ => null
        };
    }

    private static int[] RampColor(int index, int total)
    {
        if (total <= 1)
        {
            return RendererColorRamp[0];
        }

        // Sample evenly across the categorical ramp so adjacent classes differ.
        var position = (double)index / Math.Max(total - 1, 1);
        var rampIndex = (int)Math.Round(position * (RendererColorRamp.Length - 1));
        rampIndex = Math.Clamp(rampIndex, 0, RendererColorRamp.Length - 1);
        return RendererColorRamp[rampIndex];
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            var text = property.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                value = text;
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveField(MetadataV2Resource resource, string fieldName, out MetadataV2FieldType fieldType)
    {
        foreach (var field in resource.SchemaFields)
        {
            if (string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                fieldType = field.Type;
                return true;
            }
        }

        fieldType = MetadataV2FieldType.Unknown;
        return false;
    }

    private static bool TryResolveNumericField(MetadataV2Resource resource, string fieldName, out MetadataV2FieldType fieldType)
    {
        if (!TryResolveField(resource, fieldName, out fieldType))
        {
            return false;
        }

        return fieldType is MetadataV2FieldType.Integer
            or MetadataV2FieldType.BigInteger
            or MetadataV2FieldType.Double
            or MetadataV2FieldType.Float;
    }

    private static bool TryParseClassificationMethod(string value, out ClassificationMethod method)
    {
        switch (value)
        {
            case var _ when string.Equals(value, "esriClassifyEqualInterval", StringComparison.OrdinalIgnoreCase):
                method = ClassificationMethod.EqualInterval;
                return true;
            case var _ when string.Equals(value, "esriClassifyQuantile", StringComparison.OrdinalIgnoreCase):
                method = ClassificationMethod.Quantile;
                return true;
            case var _ when string.Equals(value, "esriClassifyNaturalBreaks", StringComparison.OrdinalIgnoreCase):
                method = ClassificationMethod.NaturalBreaks;
                return true;
            case var _ when string.Equals(value, "esriClassifyStandardDeviation", StringComparison.OrdinalIgnoreCase):
                method = ClassificationMethod.StandardDeviation;
                return true;
            default:
                method = ClassificationMethod.EqualInterval;
                return false;
        }
    }

    private static string ToEsriMethodName(ClassificationMethod method) => method switch
    {
        ClassificationMethod.Quantile => "esriClassifyQuantile",
        ClassificationMethod.NaturalBreaks => "esriClassifyNaturalBreaks",
        ClassificationMethod.StandardDeviation => "esriClassifyStandardDeviation",
        _ => "esriClassifyEqualInterval"
    };

    private static object? GetStat(IReadOnlyDictionary<string, object?> stats, string alias)
        => stats.TryGetValue(alias, out var value) ? value : null;

    private static double? ToDouble(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case double d:
                return d;
            case float f:
                return f;
            case decimal m:
                return (double)m;
            case long l:
                return l;
            case int i:
                return i;
            case short s:
                return s;
            case byte b:
                return b;
            case string str when double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed):
                return parsed;
            default:
                return null;
        }
    }

    private static string? FormatValue(object? value) => value switch
    {
        null => null,
        string s => s,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static string FormatRangeLabel(double min, double max)
        => $"{FormatNumber(min)} - {FormatNumber(max)}";

    private static string FormatNumber(double value)
    {
        if (value == Math.Truncate(value) && Math.Abs(value) < 1e15)
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }

        return Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
    }

    private enum ClassificationMethod
    {
        EqualInterval,
        Quantile,
        NaturalBreaks,
        StandardDeviation
    }
}
