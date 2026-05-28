// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Server.Features.Protocols.OData.Models;

namespace Honua.Server.Features.Protocols.OData.Services;

internal sealed record ParsedFeaturePayload
{
    public int? LayerId { get; init; }
    public long? ObjectId { get; init; }
    public ODataSpatialGeometry? Geometry { get; init; }
    public bool GeometrySpecified { get; init; }
    public Dictionary<string, object?> Attributes { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal static class ODataFeaturePayloadParser
{
    public static bool TryParse(
        ODataFeatureRequest request,
        out ParsedFeaturePayload payload,
        out string? errorMessage)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (request.AdditionalProperties != null)
        {
            foreach (var kvp in request.AdditionalProperties)
            {
                properties[kvp.Key] = kvp.Value;
            }
        }

        if (request.Attributes != null)
        {
            properties["Attributes"] = request.Attributes;
        }

        if (request.GeometrySpecified)
        {
            properties["Geometry"] = request.Geometry;
        }

        return TryParse(properties, out payload, out errorMessage);
    }

    public static bool TryParse(
        IDictionary<string, object?> properties,
        out ParsedFeaturePayload payload,
        out string? errorMessage)
    {
        payload = new ParsedFeaturePayload();
        errorMessage = null;

        if (properties == null)
        {
            errorMessage = "Request body is required.";
            return false;
        }

        if (TryGetProperty(properties, "LayerId", out var layerValue))
        {
            if (!TryParseInt(layerValue, out var layerId))
            {
                errorMessage = "LayerId must be a valid integer.";
                return false;
            }

            payload = payload with { LayerId = layerId };
        }

        if (TryGetProperty(properties, "ObjectId", out var objectValue))
        {
            if (!TryParseLong(objectValue, out var objectId))
            {
                errorMessage = "ObjectId must be a valid integer.";
                return false;
            }

            payload = payload with { ObjectId = objectId };
        }

        if (TryGetProperty(properties, "Geometry", out var geometryValue))
        {
            var geometry = ParseGeometryValue(geometryValue, out errorMessage);
            if (errorMessage != null)
            {
                return false;
            }

            payload = payload with { Geometry = geometry, GeometrySpecified = true };
        }

        if (TryGetProperty(properties, "Attributes", out var attributesValue))
        {
            var attributes = ParseAttributesValue(attributesValue, out errorMessage);
            if (errorMessage != null)
            {
                return false;
            }

            payload = payload with { Attributes = attributes };
        }

        foreach (var (key, value) in properties)
        {
            if (ODataUtilityService.IsReservedFeatureProperty(key) ||
                key.StartsWith("@odata.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            payload.Attributes[key] = value;
        }

        return true;
    }

    private static bool TryGetProperty(
        IDictionary<string, object?> properties,
        string name,
        out object? value)
    {
        foreach (var kvp in properties)
        {
            if (kvp.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryParseInt(object? value, out int result)
    {
        result = default;
        return value switch
        {
            int i => TryAssign(i, out result),
            long l when l is >= int.MinValue and <= int.MaxValue => TryAssign((int)l, out result),
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => TryAssign(parsed, out result),
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var parsed) =>
                TryAssign(parsed, out result),
            JsonElement element when element.ValueKind == JsonValueKind.String &&
                                     int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) =>
                TryAssign(parsed, out result),
            _ => false
        };
    }

    private static bool TryParseLong(object? value, out long result)
    {
        result = default;
        return value switch
        {
            long l => TryAssign(l, out result),
            int i => TryAssign(i, out result),
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => TryAssign(parsed, out result),
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var parsed) =>
                TryAssign(parsed, out result),
            JsonElement element when element.ValueKind == JsonValueKind.String &&
                                     long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) =>
                TryAssign(parsed, out result),
            _ => false
        };
    }

    private static bool TryAssign<T>(T value, out T result)
    {
        result = value;
        return true;
    }

    private static ODataSpatialGeometry? ParseGeometryValue(object? value, out string? errorMessage)
    {
        errorMessage = null;

        return value switch
        {
            null => null,
            ODataSpatialGeometry geometry => geometry,
            JsonElement element => ParseGeometryElement(element, out errorMessage),
            Dictionary<string, object?> dict => DeserializeGeometry(dict, out errorMessage),
            _ => SetError<ODataSpatialGeometry?>("Geometry must be an object in OData spatial format.", out errorMessage)
        };
    }

    private static ODataSpatialGeometry? ParseGeometryElement(JsonElement element, out string? errorMessage)
    {
        errorMessage = null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Object:
                try
                {
                    return JsonSerializer.Deserialize<ODataSpatialGeometry>(
                        element.GetRawText(),
                        ODataJsonContext.Default.ODataSpatialGeometry);
                }
                catch (JsonException)
                {
                    errorMessage = "Geometry must be a valid OData spatial object.";
                    return null;
                }
            default:
                return SetError<ODataSpatialGeometry?>("Geometry must be an object in OData spatial format.", out errorMessage);
        }
    }

    private static Dictionary<string, object?> ParseAttributesValue(object? value, out string? errorMessage)
    {
        errorMessage = null;

        return value switch
        {
            null => SetError<Dictionary<string, object?>>("Attributes must be an object.", out errorMessage),
            Dictionary<string, object?> dict => new Dictionary<string, object?>(dict, StringComparer.OrdinalIgnoreCase),
            IReadOnlyDictionary<string, object?> dict => new Dictionary<string, object?>(dict, StringComparer.OrdinalIgnoreCase),
            JsonElement element when element.ValueKind == JsonValueKind.Object => ParseAttributesObject(element),
            JsonElement element when element.ValueKind == JsonValueKind.String => ParseAttributesJson(element.GetString(), out errorMessage),
            string attrsString => ParseAttributesJson(attrsString, out errorMessage),
            _ => SetError<Dictionary<string, object?>>("Attributes must be an object or JSON string.", out errorMessage)
        };
    }

    private static ODataSpatialGeometry? DeserializeGeometry(
        Dictionary<string, object?> dict,
        out string? errorMessage)
    {
        errorMessage = null;

        try
        {
            var json = JsonSerializer.Serialize(dict, ODataJsonContext.Default.DictionaryStringObject);
            return JsonSerializer.Deserialize<ODataSpatialGeometry>(json, ODataJsonContext.Default.ODataSpatialGeometry);
        }
        catch (JsonException)
        {
            errorMessage = "Geometry must be a valid OData spatial object.";
            return null;
        }
    }

    private static Dictionary<string, object?> ParseAttributesJson(string? attrsString, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(attrsString))
        {
            errorMessage = "Attributes must be a valid JSON object.";
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                attrsString,
                ODataJsonContext.Default.DictionaryStringObject);

            return parsed ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            errorMessage = "Attributes must be a valid JSON object.";
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, object?> ParseAttributesObject(JsonElement attrsElement)
    {
        var parsed = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in attrsElement.EnumerateObject())
        {
            parsed[prop.Name] = prop.Value;
        }

        return parsed;
    }

    private static T SetError<T>(string message, out string? errorMessage)
    {
        errorMessage = message;
        return default!;
    }
}
