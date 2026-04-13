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
    /// Simple types pass through as string values; complex types are serialized to JSON.
    /// </summary>
    public static Dictionary<string, string> TranslateInbound(
        IReadOnlyDictionary<string, string> gpParameters)
    {
        var result = new Dictionary<string, string>(gpParameters.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in gpParameters)
        {
            // Simple types (GPString, GPLong, GPDouble, GPBoolean) pass through.
            // Complex types (GPFeatureRecordSetLayer, GPRecordSet) are already
            // serialized as JSON strings by the client. Unit types (GPLinearUnit,
            // GPArealUnit) arrive as "<value> <unit>" strings.
            // All of these are opaque strings in the canonical model.
            result[key] = value;
        }

        return result;
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
        else
        {
            foreach (var entry in context.Request.Query)
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
    /// Resolves the output parameter name for an artifact.
    /// Uses the well-known metadata key, falling back to the artifact label.
    /// </summary>
    public static string ResolveOutputParameterName(ArtifactRef artifact)
    {
        if (artifact.Metadata.TryGetValue(OutputParameterMetadataKey, out var paramName)
            && !string.IsNullOrWhiteSpace(paramName))
        {
            return paramName;
        }

        return artifact.Label;
    }
}
