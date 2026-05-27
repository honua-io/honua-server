// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Styling.Services;

/// <summary>
/// Orchestrates the style suggestion pipeline: profile → select field → classify → suggest.
/// </summary>
internal sealed partial class StyleSuggestionService : IStyleSuggestionService
{
    private const int MaxProfilingFields = 10;
    private const int SampleLimit = 50_000;
    private const int NumericValueLimit = 2_000;

    private static readonly HashSet<string> DescriptiveFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "category", "class", "status", "zone", "name", "kind", "group",
        "classification", "land_use", "landuse", "use", "code", "subtype"
    };

    private static readonly HashSet<MetadataV2FieldType> ExcludedFieldTypes =
    [
        MetadataV2FieldType.Geometry,
        MetadataV2FieldType.Geography,
        MetadataV2FieldType.Binary,
        MetadataV2FieldType.Json
    ];

    private readonly IFieldProfilingService _profilingService;
    private readonly ILogger<StyleSuggestionService> _logger;

    public StyleSuggestionService(
        IFieldProfilingService profilingService,
        ILogger<StyleSuggestionService> logger)
    {
        _profilingService = profilingService;
        _logger = logger;
    }

    public async Task<StyleSuggestion> SuggestAsync(
        MetadataV2Resource resource,
        int storageLayerId,
        StyleSuggestionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        options ??= new StyleSuggestionOptions();

        var geometryType = resource.ReadGeometryType();
        var candidates = GetProfilingCandidates(resource, options);
        if (candidates.Length == 0)
        {
            var fallbackReason = !string.IsNullOrEmpty(options.PreferredField)
                ? $"preferred field '{options.PreferredField}' not found or not eligible for classification"
                : "no candidate fields";
            LogGeometryOnlyFallback(_logger, storageLayerId, fallbackReason);
            return BuildGeometryOnlySuggestion(resource, storageLayerId, geometryType, fallbackReason, options.Edition);
        }

        LogProfilingCandidates(_logger, storageLayerId, candidates.Length);

        var profiles = await _profilingService.ProfileFieldsAsync(
            storageLayerId, candidates, SampleLimit, cancellationToken).ConfigureAwait(false);

        if (profiles.Count == 0)
        {
            LogGeometryOnlyFallback(_logger, storageLayerId, "profiling returned no results");
            return BuildGeometryOnlySuggestion(resource, storageLayerId, geometryType, "profiling returned no results", options.Edition);
        }

        var selectedProfile = SelectBestField(profiles, geometryType, options);
        if (selectedProfile == null)
        {
            LogGeometryOnlyFallback(_logger, storageLayerId, "no field scored above threshold");
            return BuildGeometryOnlySuggestion(resource, storageLayerId, geometryType, "no field scored above threshold", options.Edition);
        }

        var (reason, classification) = await ClassifyFieldAsync(
            storageLayerId, selectedProfile, options, cancellationToken).ConfigureAwait(false);

        var palette = ResolvePalette(selectedProfile, classification.Method, options);

        // Clamp classification to palette capacity to avoid repeating colors
        // (e.g. Viridis supports max 9 classes but classCount can reach 12).
        if (classification.ClassCount > palette.MaxClassCount)
            classification = ClampClassification(classification, palette.MaxClassCount);

        var colors = palette.GetColors(classification.ClassCount);

        var legend = BuildLegend(selectedProfile.FieldName, classification, colors,
            selectedProfile.MinValue, selectedProfile.MaxValue);
        var observations = BuildObservations(geometryType, selectedProfile, classification, palette);

        return new StyleSuggestion
        {
            LayerId = storageLayerId,
            GeometryType = geometryType,
            SuggestedField = new FieldSuggestion
            {
                Name = selectedProfile.FieldName,
                Type = selectedProfile.FieldType,
                Reason = reason,
                Profile = selectedProfile
            },
            Classification = classification,
            PaletteName = palette.Name,
            PaletteColors = colors,
            Legend = legend,
            Observations = observations,
            Edition = options.Edition
        };
    }

    private static MetadataV2Field[] GetProfilingCandidates(
        MetadataV2Resource resource, StyleSuggestionOptions options)
    {
        var fields = resource.SchemaFields
            .Where(f => !ExcludedFieldTypes.Contains(f.Type))
            .Where(f => f.Type != MetadataV2FieldType.Uuid)
            .Where(f => f.Type != MetadataV2FieldType.Boolean);

        // If a preferred field is specified, only profile that one
        if (!string.IsNullOrEmpty(options.PreferredField))
        {
            var preferred = fields.FirstOrDefault(f =>
                string.Equals(f.Name, options.PreferredField, StringComparison.OrdinalIgnoreCase));
            return preferred != null ? [preferred] : [];
        }

        return fields.Take(MaxProfilingFields).ToArray();
    }

    private static FieldProfile? SelectBestField(
        IReadOnlyList<FieldProfile> profiles,
        MetadataV2GeometryType geometryType,
        StyleSuggestionOptions options)
    {
        if (!string.IsNullOrEmpty(options.PreferredField))
        {
            return profiles.FirstOrDefault(p =>
                string.Equals(p.FieldName, options.PreferredField, StringComparison.OrdinalIgnoreCase));
        }

        // Score each profile
        var scored = profiles
            .Select(p => (Profile: p, Score: ScoreField(p, geometryType)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();

        return scored.Count > 0 ? scored[0].Profile : null;
    }

    private static double ScoreField(FieldProfile profile, MetadataV2GeometryType geometryType)
    {
        var score = 0d;

        // Penalize high-null fields
        if (profile.NullPercentage > 0.5)
            return 0;

        // Penalize cardinality of 1 (constant field)
        if (profile.DistinctCount <= 1)
            return 0;

        var isNumeric = profile.MinValue.HasValue;
        var isLowCardinality = profile.DistinctCount <= 12;

        // Geometry-type preferences
        switch (geometryType)
        {
            case MetadataV2GeometryType.Polygon:
            case MetadataV2GeometryType.MultiPolygon:
            case MetadataV2GeometryType.GeometryCollection:
            case MetadataV2GeometryType.Mixed:
                // Polygons (and GeometryCollection, treated as polygon-like): prefer numeric for choropleth
                if (isNumeric) score += 3;
                break;

            case MetadataV2GeometryType.Point:
            case MetadataV2GeometryType.MultiPoint:
                // Points: prefer low-cardinality categorical
                if (isLowCardinality) score += 3;
                if (!isNumeric) score += 1;
                break;

            case MetadataV2GeometryType.LineString:
            case MetadataV2GeometryType.MultiLineString:
                // Lines: prefer categorical, then numeric
                if (isLowCardinality) score += 2;
                if (!isNumeric) score += 1;
                break;

            default:
                score += 1;
                break;
        }

        // Bonus for low null percentage
        score += (1 - profile.NullPercentage) * 2;

        // Bonus for moderate cardinality (not too low, not too high)
        if (profile.CardinalityRatio > 0.01 && profile.CardinalityRatio < 0.8)
            score += 1;

        // Bonus for descriptive field names
        if (DescriptiveFieldNames.Contains(profile.FieldName))
            score += 2;

        return score;
    }

    private async Task<(string Reason, ClassificationResult Classification)> ClassifyFieldAsync(
        int layerId,
        FieldProfile profile,
        StyleSuggestionOptions options,
        CancellationToken cancellationToken)
    {
        var isNumeric = profile.MinValue.HasValue;
        var classCount = Math.Max(2, Math.Min(options.ClassCount, 12));

        // Determine method
        var method = options.PreferredMethod ?? DetermineMethod(profile);

        if (method == ClassificationMethod.UniqueValue || !isNumeric)
        {
            // Normalize numeric sample values so that MapLibre's to-string coercion
            // produces a matching string (e.g. JSONB "1.0" → "1" to match to-string(1.0)).
            var categories = profile.SampleValues
                .Take(classCount)
                .Select(sv => isNumeric && double.TryParse(sv.Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var num)
                    ? num.ToString("G", CultureInfo.InvariantCulture)
                    : sv.Value)
                .ToArray();

            return (
                $"Categorical: {profile.DistinctCount} distinct values, suitable for unique-value classification.",
                new ClassificationResult
                {
                    Method = ClassificationMethod.UniqueValue,
                    FieldName = profile.FieldName,
                    Categories = categories,
                    ClassCount = categories.Length
                });
        }

        // Numeric classification
        var values = await _profilingService.GetNumericValuesAsync(
            layerId, profile.FieldName, NumericValueLimit, cancellationToken).ConfigureAwait(false);

        double[] breaks;
        if (values.Length == 0)
        {
            breaks = ClassificationAlgorithms.EqualInterval(
                profile.MinValue!.Value, profile.MaxValue!.Value, classCount);
            method = ClassificationMethod.EqualInterval;
        }
        else
        {
            breaks = method switch
            {
                ClassificationMethod.Quantile =>
                    ClassificationAlgorithms.Quantile(values, classCount),
                ClassificationMethod.NaturalBreaks =>
                    ClassificationAlgorithms.NaturalBreaks(values, classCount),
                _ =>
                    ClassificationAlgorithms.EqualInterval(
                        profile.MinValue!.Value, profile.MaxValue!.Value, classCount)
            };
        }

        var reason = $"Numeric ({method}): range [{profile.MinValue:F2}..{profile.MaxValue:F2}], " +
                     $"{profile.DistinctCount} distinct values.";

        return (reason, new ClassificationResult
        {
            Method = method,
            FieldName = profile.FieldName,
            Breaks = breaks,
            ClassCount = breaks.Length + 1
        });
    }

    /// <summary>
    /// Trims a classification result so it fits within <paramref name="maxClasses"/>.
    /// For numeric breaks, keeps the first N-1 breaks (tail merges into a single class).
    /// For categories, keeps the first N by frequency (already frequency-ordered from profiling).
    /// </summary>
    private static ClassificationResult ClampClassification(ClassificationResult classification, int maxClasses)
    {
        if (classification.Categories != null)
        {
            return new ClassificationResult
            {
                Method = classification.Method,
                FieldName = classification.FieldName,
                Categories = classification.Categories[..maxClasses],
                ClassCount = maxClasses
            };
        }

        if (classification.Breaks != null)
        {
            var newBreakCount = maxClasses - 1;
            return new ClassificationResult
            {
                Method = classification.Method,
                FieldName = classification.FieldName,
                Breaks = classification.Breaks[..newBreakCount],
                ClassCount = maxClasses
            };
        }

        return classification;
    }

    private static ClassificationMethod DetermineMethod(FieldProfile profile)
    {
        var isNumeric = profile.MinValue.HasValue;

        if (!isNumeric || profile.DistinctCount <= 12)
            return ClassificationMethod.UniqueValue;

        return ClassificationMethod.NaturalBreaks;
    }

    private static ColorPalette ResolvePalette(
        FieldProfile profile,
        ClassificationMethod method,
        StyleSuggestionOptions options)
    {
        if (!string.IsNullOrEmpty(options.PreferredPalette))
        {
            var custom = ColorPalettes.GetByName(options.PreferredPalette);
            if (custom != null) return custom;
        }

        return ColorPalettes.SelectPalette(profile, method);
    }

    private static LegendInfo BuildLegend(
        string fieldName,
        ClassificationResult classification,
        string[] colors,
        double? dataMin = null,
        double? dataMax = null)
    {
        var entries = new List<LegendEntry>();

        if (classification.Method == ClassificationMethod.UniqueValue && classification.Categories != null)
        {
            for (var i = 0; i < classification.Categories.Length && i < colors.Length; i++)
            {
                entries.Add(new LegendEntry
                {
                    Label = classification.Categories[i],
                    Color = colors[i]
                });
            }
        }
        else if (classification.Breaks != null)
        {
            for (var i = 0; i <= classification.Breaks.Length; i++)
            {
                // Use actual data bounds for edge classes to match drawingInfo output.
                var low = i == 0 ? dataMin : classification.Breaks[i - 1];
                var high = i < classification.Breaks.Length ? classification.Breaks[i] : dataMax;
                var colorIdx = Math.Min(i, colors.Length - 1);

                var label = i == 0
                    ? high.HasValue ? $"< {high.Value.ToString("F2", CultureInfo.InvariantCulture)}" : "Minimum"
                    : i == classification.Breaks.Length
                        ? low.HasValue ? $">= {low.Value.ToString("F2", CultureInfo.InvariantCulture)}" : "Maximum"
                        : $"{low?.ToString("F2", CultureInfo.InvariantCulture) ?? "?"} - {high?.ToString("F2", CultureInfo.InvariantCulture) ?? "?"}";

                entries.Add(new LegendEntry
                {
                    Label = label,
                    Color = colors[colorIdx],
                    MinValue = low,
                    MaxValue = high
                });
            }
        }

        return new LegendInfo { Title = fieldName, Entries = entries };
    }

    private static List<string> BuildObservations(
        MetadataV2GeometryType geometryType,
        FieldProfile profile,
        ClassificationResult classification,
        ColorPalette palette)
    {
        var obs = new List<string>
        {
            $"Geometry type: {geometryType}.",
            $"Selected field: '{profile.FieldName}' ({profile.FieldType}).",
            $"Field statistics: {profile.DistinctCount} distinct values, {profile.NullPercentage:P0} nulls."
        };

        if (profile.MinValue.HasValue)
            obs.Add($"Numeric range: {profile.MinValue:F2} to {profile.MaxValue:F2} (mean: {profile.MeanValue:F2}, stddev: {profile.StandardDeviation:F2}).");

        obs.Add($"Classification: {classification.Method} with {classification.ClassCount} classes.");
        obs.Add($"Palette: {palette.Name} ({palette.Category}, colorblind-safe: {palette.IsColorblindSafe}).");

        return obs;
    }

    private static StyleSuggestion BuildGeometryOnlySuggestion(
        MetadataV2Resource resource,
        int storageLayerId,
        MetadataV2GeometryType geometryType,
        string fallbackReason,
        HonuaEdition edition)
    {
        const string defaultColor = "#2D69A5";

        return new StyleSuggestion
        {
            LayerId = storageLayerId,
            GeometryType = geometryType,
            SuggestedField = null,
            Classification = null,
            PaletteName = "Default",
            PaletteColors = [defaultColor],
            Legend = new LegendInfo
            {
                Title = resource.Metadata.Name,
                Entries = [new LegendEntry { Label = resource.Metadata.Name, Color = defaultColor }]
            },
            Observations =
            [
                $"Geometry type: {geometryType}.",
                $"Using enhanced geometry-aware defaults ({fallbackReason})."
            ],
            Edition = edition
        };
    }

    [LoggerMessage(
        EventId = 4578,
        Level = LogLevel.Debug,
        Message = "Style suggestion: {CandidateCount} profiling candidates for layer {LayerId}")]
    private static partial void LogProfilingCandidates(ILogger logger, int layerId, int candidateCount);

    [LoggerMessage(
        EventId = 4579,
        Level = LogLevel.Debug,
        Message = "Style suggestion: geometry-only fallback for layer {LayerId}, reason: {Reason}")]
    private static partial void LogGeometryOnlyFallback(ILogger logger, int layerId, string reason);
}
