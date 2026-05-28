// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Core.Features.FileImport.Services;

/// <summary>
/// Computes advisory schema suggestions from file preview data.
/// All suggestions are deterministic and heuristic-based.
/// </summary>
internal sealed partial class ImportSchemaSuggestionService : IImportSchemaSuggestionService
{
    /// <inheritdoc />
    public Task<SchemaSuggestion> SuggestAsync(
        FilePreview preview, string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var detectedSrid = preview.DetectedSrid ?? 4326;
        var sridSuggestion = ComputeSridSuggestion(detectedSrid, preview);
        var indexes = ComputeIndexSuggestions(preview);
        var fieldTypes = ComputeFieldTypeSuggestions(preview);
        var observations = ComputeObservations(preview, fileName);

        var suggestion = new SchemaSuggestion
        {
            SourceName = fileName,
            Srid = sridSuggestion,
            Indexes = indexes,
            FieldTypes = fieldTypes,
            DetectedFormat = preview.Format.ToString(),
            Observations = observations,
        };

        return Task.FromResult(suggestion);
    }

    private static SridSuggestion ComputeSridSuggestion(int detectedSrid, FilePreview preview)
    {
        // If no SRID detected, recommend WGS 84 as the safest default.
        // Only cite RFC 7946 for GeoJSON — it mandates WGS 84 for that format specifically.
        if (preview.DetectedSrid is null)
        {
            var reason = preview.Format == SupportedFileFormat.GeoJson
                ? "No CRS detected in source data; defaulting to WGS 84 (EPSG:4326) per RFC 7946."
                : "No CRS detected in source data; defaulting to WGS 84 (EPSG:4326) as a safe interoperable default.";
            return new SridSuggestion
            {
                RecommendedSrid = 4326,
                DetectedSrid = 4326,
                Reason = reason,
            };
        }

        // For well-known geographic CRS, keep as-is
        if (detectedSrid is 4326 or 4269 or 4267)
        {
            return new SridSuggestion
            {
                RecommendedSrid = detectedSrid,
                DetectedSrid = detectedSrid,
                Reason = $"Detected EPSG:{detectedSrid} — standard geographic CRS, no transformation needed.",
            };
        }

        // For Web Mercator, suggest keeping it but note it's projected
        if (detectedSrid == 3857)
        {
            return new SridSuggestion
            {
                RecommendedSrid = 3857,
                DetectedSrid = 3857,
                Reason = "Detected Web Mercator (EPSG:3857). Suitable for web mapping; consider EPSG:4326 if geographic coordinates are preferred.",
            };
        }

        // For other projected CRS, recommend keeping but note transformation availability
        return new SridSuggestion
        {
            RecommendedSrid = detectedSrid,
            DetectedSrid = detectedSrid,
            Reason = $"Detected EPSG:{detectedSrid}. Keeping source CRS; transformation to EPSG:4326 available at import time if needed.",
        };
    }

    private static List<IndexSuggestion> ComputeIndexSuggestions(FilePreview preview)
    {
        var indexes = new List<IndexSuggestion>();

        // Always suggest spatial index for geospatial data
        indexes.Add(new IndexSuggestion
        {
            Type = IndexSuggestionType.Spatial,
            Columns = ["shape"],
            Reason = "Spatial (GIST) index recommended for geometry column to accelerate spatial queries.",
        });

        // Suggest B-tree index on common ID-like fields
        foreach (var prop in preview.SampleProperties)
        {
            var lowerName = prop.Key.ToLowerInvariant();

            // Suggest B-tree for ID and key fields
            if (lowerName is "id" or "objectid" or "fid" or "gid")
            {
                indexes.Add(new IndexSuggestion
                {
                    Type = IndexSuggestionType.BTree,
                    Columns = [prop.Key],
                    Reason = $"B-tree index recommended on '{prop.Key}' — likely primary key or unique identifier.",
                });
            }

            // Suggest B-tree for common queryable string fields
            if (lowerName.EndsWith("_id", StringComparison.Ordinal) ||
                lowerName.EndsWith("_code", StringComparison.Ordinal) ||
                lowerName.EndsWith("_type", StringComparison.Ordinal))
            {
                indexes.Add(new IndexSuggestion
                {
                    Type = IndexSuggestionType.BTree,
                    Columns = [prop.Key],
                    Reason = $"B-tree index recommended on '{prop.Key}' — categorical/foreign key field likely used in filters.",
                });
            }

            // Suggest GIN for name/description fields
            if (lowerName is "name" or "description" or "title" or "label")
            {
                indexes.Add(new IndexSuggestion
                {
                    Type = IndexSuggestionType.Gin,
                    Columns = [prop.Key],
                    Reason = $"GIN index recommended on '{prop.Key}' for text search queries.",
                });
            }
        }

        return indexes;
    }

    private static List<FieldTypeSuggestion> ComputeFieldTypeSuggestions(FilePreview preview)
    {
        var suggestions = new List<FieldTypeSuggestion>();

        foreach (var (name, value) in preview.SampleProperties)
        {
            if (value is null) continue;

            var valueStr = value.ToString() ?? "";
            var detectedType = InferTypeFromValue(value);

            // Check if string value looks like a date
            if (value is string s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out _))
            {
                suggestions.Add(new FieldTypeSuggestion
                {
                    FieldName = name,
                    DetectedType = detectedType,
                    RecommendedType = "TIMESTAMP WITH TIME ZONE",
                    Reason = $"Value '{TruncateForDisplay(valueStr)}' appears to be a date/time — consider TIMESTAMPTZ for temporal queries.",
                });
            }

            // Check if string value looks like a UUID
            if (value is string uuidStr && Guid.TryParse(uuidStr, out _))
            {
                suggestions.Add(new FieldTypeSuggestion
                {
                    FieldName = name,
                    DetectedType = detectedType,
                    RecommendedType = "UUID",
                    Reason = "Value looks like a UUID — consider UUID type for storage efficiency.",
                });
            }

            // Check if string value is always numeric
            if (value is string numStr && double.TryParse(numStr, CultureInfo.InvariantCulture, out _) &&
                !IntegerPattern().IsMatch(numStr))
            {
                suggestions.Add(new FieldTypeSuggestion
                {
                    FieldName = name,
                    DetectedType = detectedType,
                    RecommendedType = "DOUBLE PRECISION",
                    Reason = $"Value '{TruncateForDisplay(numStr)}' is numeric but stored as text — consider numeric type.",
                });
            }
        }

        return suggestions;
    }

    private static List<string> ComputeObservations(FilePreview preview, string fileName)
    {
        var observations = new List<string>();

        observations.Add($"Source file: {fileName}");
        observations.Add($"Detected format: {preview.Format}");
        observations.Add($"Feature count: {preview.TotalFeatureCount}");
        observations.Add($"Fields detected: {preview.SampleProperties.Count}");

        if (preview.DetectedSrid is not null)
        {
            observations.Add($"Source CRS: EPSG:{preview.DetectedSrid}");
        }
        else
        {
            observations.Add("No CRS information detected in source data.");
        }

        if (preview.AvailableLayers.Length > 1)
        {
            observations.Add($"Multi-layer format with {preview.AvailableLayers.Length} layers: {string.Join(", ", preview.AvailableLayers)}");
        }

        if (preview.Warnings.Length > 0)
        {
            foreach (var w in preview.Warnings)
            {
                observations.Add($"Warning: {w}");
            }
        }

        return observations;
    }

    private static string InferTypeFromValue(object value) => value switch
    {
        int or long => "integer",
        float or double or decimal => "double",
        bool => "boolean",
        string => "text",
        _ => "unknown"
    };

    private static string TruncateForDisplay(string value)
    {
        const int maxLen = 30;
        return value.Length <= maxLen ? value : string.Concat(value.AsSpan(0, maxLen), "...");
    }

    [GeneratedRegex(@"^-?\d+$")]
    private static partial Regex IntegerPattern();
}
