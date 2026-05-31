// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Text;
using System.Text.Json;
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
    /// <see cref="ProcessParameterSpec.AllowedValues"/>.
    /// </summary>
    public static Dictionary<string, string> TranslateInbound(
        IReadOnlyDictionary<string, string> gpParameters,
        ProcessDefinition? definition)
    {
        var result = new Dictionary<string, string>(gpParameters.Count, StringComparer.OrdinalIgnoreCase);
        var specsByName = BuildSpecLookup(definition);

        foreach (var (key, value) in gpParameters)
        {
            specsByName.TryGetValue(key, out var spec);
            var normalized = NormalizeGPValue(value, spec);
            ValidateChoice(spec, normalized);
            result[key] = normalized;
        }

        return result;
    }

    private static Dictionary<string, ProcessParameterSpec> BuildSpecLookup(ProcessDefinition? definition)
    {
        if (definition is null || definition.Parameters.Count == 0)
        {
            return new Dictionary<string, ProcessParameterSpec>(0, StringComparer.OrdinalIgnoreCase);
        }

        var lookup = new Dictionary<string, ProcessParameterSpec>(
            definition.Parameters.Count,
            StringComparer.OrdinalIgnoreCase);
        foreach (var spec in definition.Parameters)
        {
            lookup[spec.Name] = spec;
        }
        return lookup;
    }

    /// <summary>
    /// Normalizes a single GP parameter value. Spec-less callers preserve the
    /// historical behavior; supply a <paramref name="spec"/> to opt into
    /// GPMultiValue unpacking and other spec-aware translation.
    /// </summary>
    internal static string NormalizeGPValue(string value, ProcessParameterSpec? spec = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        // GPMultiValue unpacking: canonical executors (e.g., the geometry.union
        // dispatcher) expect a JSON array string for WkbArray params. Accept the
        // Esri wire forms — JSON array literal or comma-delimited string — and
        // re-emit the canonical JSON-array form.
        if (spec is { ValueType: ProcessParameterValueType.WkbArray } &&
            TryNormalizeMultiValue(value, out var multi))
        {
            return multi;
        }

        if (value[0] != '{')
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
    /// Re-emits a GPMultiValue input as the canonical JSON-array string the
    /// runtime executors expect. JSON-array literals are validated and
    /// re-serialised; comma-delimited strings are split and quoted. Returns
    /// <c>false</c> when the input does not look like a multi-value payload, in
    /// which case the caller falls back to scalar normalisation.
    /// </summary>
    private static bool TryNormalizeMultiValue(string value, out string normalized)
    {
        normalized = value;
        var trimmed = value.AsSpan().Trim();
        if (trimmed.IsEmpty)
        {
            return false;
        }

        if (trimmed[0] == '[')
        {
            try
            {
                using var doc = JsonDocument.Parse(value);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                // Already a JSON array — re-emit as a normalised string so the
                // downstream contract (JsonDocument.Parse on the spec parameter)
                // sees a stable shape regardless of inbound whitespace or
                // element ordering quirks.
                var items = new List<string>(doc.RootElement.GetArrayLength());
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }
                    var item = element.GetString();
                    if (string.IsNullOrEmpty(item))
                    {
                        return false;
                    }
                    items.Add(item);
                }
                normalized = EncodeStringArray(items);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        if (value.Contains(',', StringComparison.Ordinal))
        {
            var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                return false;
            }
            normalized = EncodeStringArray(parts);
            return true;
        }

        return false;
    }

    private static string EncodeStringArray(IReadOnlyList<string> items)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (var i = 0; i < items.Count; i++)
            {
                writer.WriteStringValue(items[i]);
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void ValidateChoice(ProcessParameterSpec? spec, string value)
    {
        if (spec?.AllowedValues is not { Count: > 0 } allowed || string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (var candidate in allowed)
        {
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new GeoprocessingValidationException(
            $"Parameter '{spec.Name}': '{value}' is not in the allowed values [{string.Join(", ", allowed)}].");
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
    /// Maps a process-catalog parameter value type to an Esri GP data type string.
    /// </summary>
    public static string ToEsriDataType(ProcessParameterValueType valueType) => valueType switch
    {
        ProcessParameterValueType.Text => "GPString",
        ProcessParameterValueType.WholeNumber => "GPLong",
        ProcessParameterValueType.FloatingPoint => "GPDouble",
        ProcessParameterValueType.Flag => "GPBoolean",
        ProcessParameterValueType.Wkb => "GPDataFile",
        ProcessParameterValueType.WkbArray => "GPMultiValue:GPDataFile",
        ProcessParameterValueType.Srid => "GPLong",
        ProcessParameterValueType.LayerId => "GPString",
        _ => "GPString"
    };

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
            var form = await context.Request.ReadFormAsync(cancellationToken);
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
