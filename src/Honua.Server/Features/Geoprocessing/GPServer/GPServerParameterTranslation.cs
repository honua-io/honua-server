// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Server.Features.Geoprocessing.GPServer;

/// <summary>
/// Bidirectional translation between Esri GP parameter types and canonical
/// opaque step inputs (<see cref="IReadOnlyDictionary{TKey,TValue}"/> of string).
/// Per ADR-0029, parameter translation is the adapter's responsibility.
/// </summary>
internal static class GPServerParameterTranslation
{
    /// <summary>
    /// Well-known metadata key on <see cref="ArtifactRef"/> that stores the
    /// GPServer output parameter name for per-output result routing.
    /// </summary>
    public const string OutputParameterMetadataKey = "geoservices.output_parameter";

    /// <summary>
    /// Translates incoming Esri GP parameters to canonical opaque string inputs.
    /// Simple types pass through as string values. Complex GP types are normalized:
    /// GPDataFile/GPRasterDataLayer URLs are extracted, GPLinearUnit/GPArealUnit
    /// objects are normalized to "&lt;value&gt; &lt;unit&gt;" strings.
    /// </summary>
    public static Dictionary<string, string> TranslateInbound(
        IReadOnlyDictionary<string, string> gpParameters)
    {
        var result = new Dictionary<string, string>(gpParameters.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in gpParameters)
        {
            result[key] = NormalizeGPValue(value);
        }

        return result;
    }

    /// <summary>
    /// Normalizes a single GP parameter value. JSON object payloads matching known
    /// GP type shapes are canonicalized; all other values pass through unchanged.
    /// </summary>
    internal static string NormalizeGPValue(string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '{')
        {
            return value;
        }

        try
        {
            using var doc = JsonDocument.Parse(value);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return value;
            }

            // GPDataFile / GPRasterDataLayer: { "url": "..." }
            if (root.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
            {
                // Only extract when "url" is the dominant property (data-file shape).
                // Feature/record set payloads also have "url" but carry "features" or "fields",
                // so we leave those as JSON passthrough.
                if (!root.TryGetProperty("features", out _) && !root.TryGetProperty("fields", out _))
                {
                    return urlProp.GetString() ?? value;
                }
            }

            // GPLinearUnit / GPArealUnit: { "distance": <number>, "units": "<string>" }
            if (root.TryGetProperty("distance", out var distanceProp) &&
                root.TryGetProperty("units", out var unitsProp) &&
                distanceProp.ValueKind == JsonValueKind.Number &&
                unitsProp.ValueKind == JsonValueKind.String)
            {
                var distance = distanceProp.GetDouble();
                var units = unitsProp.GetString();
                return FormattableString.Invariant($"{distance} {units}");
            }

            // GPFeatureRecordSetLayer / GPRecordSet / other complex types:
            // pass through as-is (already JSON strings in canonical model).
            return value;
        }
        catch (JsonException)
        {
            // Not valid JSON — treat as simple string value.
            return value;
        }
    }

    /// <summary>
    /// Maps a canonical <see cref="ArtifactKind"/> to the corresponding Esri GP data type string.
    /// </summary>
    public static string ToEsriDataType(ArtifactKind kind) => kind switch
    {
        ArtifactKind.FeatureLayer => "GPFeatureRecordSetLayer",
        ArtifactKind.Table => "GPRecordSet",
        ArtifactKind.Raster => "GPRasterDataLayer",
        ArtifactKind.File or ArtifactKind.Report or ArtifactKind.Map => "GPDataFile",
        ArtifactKind.Scalar => "GPString",
        ArtifactKind.AppBundle => "GPDataFile",
        _ => "GPString"
    };

    /// <summary>
    /// Reads request parameters from the HTTP context (query string for GET,
    /// form-encoded body for POST).
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> ReadRequestParametersAsync(
        HttpContext context)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Always start with query-string parameters so they are honoured
        // regardless of HTTP method or content type.
        foreach (var entry in context.Request.Query)
        {
            if (!string.IsNullOrEmpty(entry.Value.FirstOrDefault()))
            {
                result[entry.Key] = entry.Value.FirstOrDefault()!;
            }
        }

        // For POST with form content, overlay form values (form takes precedence
        // over query-string when the same key appears in both locations).
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync();
            foreach (var entry in form)
            {
                if (!string.IsNullOrEmpty(entry.Value.FirstOrDefault()))
                {
                    result[entry.Key] = entry.Value.FirstOrDefault()!;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves the output parameter name for an artifact using the well-known
    /// metadata key <see cref="OutputParameterMetadataKey"/>. Per ADR-0029
    /// invariant #3, the route key must be a stable output identifier — not
    /// <see cref="ArtifactRef.Label"/> (which is human-readable).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the artifact does not carry the required metadata binding.
    /// </exception>
    public static string ResolveOutputParameterName(ArtifactRef artifact)
    {
        if (artifact.Metadata.TryGetValue(OutputParameterMetadataKey, out var paramName)
            && !string.IsNullOrWhiteSpace(paramName))
        {
            return paramName;
        }

        throw new InvalidOperationException(
            $"Artifact '{artifact.ArtifactId}' is missing the required " +
            $"'{OutputParameterMetadataKey}' metadata binding for GPServer result routing.");
    }
}
