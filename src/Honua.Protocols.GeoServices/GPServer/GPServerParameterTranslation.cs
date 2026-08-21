// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;

namespace Honua.Protocols.GeoServices.GPServer;

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
    public const string OutputParameterMetadataKey = GeoprocessingProtocolMetadataKeys.GeoServicesOutputParameterMetadataKey;

    /// <summary>
    /// Translates incoming Esri GP parameters to canonical opaque string inputs
    /// without spec-aware behavior. Equivalent to calling the overload with a
    /// <c>null</c> definition: GPMultiValue inputs are not unpacked from
    /// comma-delimited form and GPChoice <c>AllowedValues</c> are not enforced.
    /// </summary>
    public static Dictionary<string, string> TranslateInbound(
        IReadOnlyDictionary<string, string> gpParameters)
        => TranslateInbound(gpParameters, definition: null);

    /// <summary>
    /// Translates incoming Esri GP parameters to canonical opaque string inputs.
    /// Simple types pass through as string values. Complex GP types are normalized:
    /// GPDataFile/GPRasterDataLayer URLs are extracted, GPLinearUnit/GPArealUnit
    /// objects are normalized to "&lt;value&gt; &lt;unit&gt;" strings. When the
    /// owning <paramref name="definition"/> is supplied, GPMultiValue parameters
    /// declared as <see cref="ProcessParameterValueType.WkbArray"/> are unpacked
    /// from a JSON array or comma-delimited form into the canonical JSON-array
    /// string the runtime expects, and GPChoice values are validated against
    /// <see cref="ProcessParameterSpec.AllowedValues"/> — matched
    /// case-insensitively as Esri's GP framework does, then rewritten to the
    /// catalog spelling so canonical validators and executors that compare
    /// ordinally still accept them.
    /// </summary>
    public static Dictionary<string, string> TranslateInbound(
        IReadOnlyDictionary<string, string> gpParameters,
        ProcessDefinition? definition)
        => EsriGpTaskProjection.TranslateInbound(gpParameters, definition);

    /// <summary>
    /// Normalizes a single GP parameter value. Spec-less callers preserve the
    /// historical behavior; supply a <paramref name="spec"/> to opt into
    /// GPMultiValue unpacking and other spec-aware translation.
    /// </summary>
    internal static string NormalizeGPValue(string value, ProcessParameterSpec? spec = null)
        => EsriGpTaskProjection.NormalizeValue(value, spec);

    /// <summary>
    /// Maps a canonical <see cref="ArtifactKind"/> to the corresponding Esri GP data type string.
    /// </summary>
    public static string ToEsriDataType(ArtifactKind kind)
        => EsriGpTaskProjection.ToEsriDataType(kind);

    /// <summary>
    /// Maps a process-catalog parameter value type to an Esri GP data type string.
    /// </summary>
    public static string ToEsriDataType(ProcessParameterValueType valueType)
        => EsriGpTaskProjection.ToEsriDataType(valueType);

    /// <summary>
    /// Reads request parameters from the HTTP context (query string for GET,
    /// form-encoded body for POST).
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> ReadRequestParametersAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Always start with query-string parameters so they are honoured
        // regardless of HTTP method or content type.
        foreach (var entry in context.Request.Query.Where(e => !string.IsNullOrEmpty(e.Value.FirstOrDefault())))
        {
            result[entry.Key] = entry.Value.FirstOrDefault()!;
        }

        // For POST with form content, overlay form values (form takes precedence
        // over query-string when the same key appears in both locations).
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            foreach (var entry in form.Where(e => !string.IsNullOrEmpty(e.Value.FirstOrDefault())))
            {
                result[entry.Key] = entry.Value.FirstOrDefault()!;
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
