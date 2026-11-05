// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;

namespace Honua.Protocols.GeoServices.GPServer;

internal static class GPServerParameterTranslation
{
    public const string OutputParameterMetadataKey = GeoprocessingProtocolMetadataKeys.GeoServicesOutputParameterMetadataKey;

    // Without a task definition, GPMultiValue inputs stay comma-delimited and GPChoice values are not enforced.
    public static Dictionary<string, string> TranslateInbound(
        IReadOnlyDictionary<string, string> gpParameters)
        => TranslateInbound(gpParameters, definition: null);

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
            result[key] = NormalizeChoice(spec, normalized);
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
            // Only extract when "url" is the dominant property (data-file shape).
            // Feature/record set payloads also have "url" but carry "features" or "fields",
            // so we leave those as JSON passthrough.
            if (root.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String &&
                !root.TryGetProperty("features", out _) && !root.TryGetProperty("fields", out _))
            {
                return urlProp.GetString() ?? value;
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

    private static string NormalizeChoice(ProcessParameterSpec? spec, string value)
    {
        if (spec?.AllowedValues is not { Count: > 0 } allowed || string.IsNullOrEmpty(value))
        {
            return value;
        }

        // An exact match is already the catalog spelling, and it settles any
        // case-insensitive ambiguity in the caller's favour.
        if (allowed.Any(candidate => string.Equals(candidate, value, StringComparison.Ordinal)))
        {
            return value;
        }

        var matches = allowed
            .Where(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        throw new GeoprocessingValidationException(
            matches.Count > 1
                ? $"Parameter '{spec.Name}': '{value}' matches more than one allowed value ignoring case; " +
                  $"supply the exact spelling from [{string.Join(", ", allowed)}]."
                : $"Parameter '{spec.Name}': '{value}' is not in the allowed values [{string.Join(", ", allowed)}].");
    }

    public static JsonElement? TranslateDefaultValue(ProcessParameterSpec parameter)
    {
        if (parameter.DefaultValue is null)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            switch (parameter.ValueType)
            {
                case ProcessParameterValueType.Flag:
                    writer.WriteBooleanValue(bool.Parse(parameter.DefaultValue));
                    break;
                case ProcessParameterValueType.WholeNumber:
                case ProcessParameterValueType.Srid:
                    writer.WriteNumberValue(long.Parse(parameter.DefaultValue, NumberStyles.Integer, CultureInfo.InvariantCulture));
                    break;
                case ProcessParameterValueType.FloatingPoint:
                    writer.WriteNumberValue(double.Parse(parameter.DefaultValue, NumberStyles.Float, CultureInfo.InvariantCulture));
                    break;
                default:
                    writer.WriteStringValue(parameter.DefaultValue);
                    break;
            }
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

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

    public static string ToEsriDataType(ProcessParameterValueType valueType) => valueType switch
    {
        ProcessParameterValueType.Text => "GPString",
        ProcessParameterValueType.WholeNumber => "GPLong",
        ProcessParameterValueType.FloatingPoint => "GPDouble",
        ProcessParameterValueType.Flag => "GPBoolean",
        // These are inline base64 WKB (or inline GeoJSON), not data-file objects.
        ProcessParameterValueType.Wkb => "GPString",
        ProcessParameterValueType.WkbArray => "GPMultiValue:GPString",
        ProcessParameterValueType.Srid => "GPLong",
        ProcessParameterValueType.LayerId => "GPString",
        _ => "GPString"
    };

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

    // The artifact route key is the stable output identifier, not the human-readable title.
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
