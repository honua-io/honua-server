// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.GeometryService.Services;

/// <summary>
/// Parses geometry service request parameters from GET query strings, POST form-data, or POST JSON bodies.
/// Handles ArcGIS-format geometry wrapper objects and spatial reference parsing.
/// </summary>
internal static class GeometryServiceRequestParser
{
    private const string UnsupportedMediaTypeErrorPrefix = "__unsupported_media_type__:";

    private static readonly FrozenDictionary<string, double> _unitMultipliers = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        // String name forms
        ["esriMeters"] = 1.0,
        ["esriSRUnit_Meter"] = 1.0,
        ["esriMillimeters"] = 0.001,
        ["esriSRUnit_Millimeter"] = 0.001,
        ["esriCentimeters"] = 0.01,
        ["esriSRUnit_Centimeter"] = 0.01,
        ["esriDecimeters"] = 0.1,
        ["esriSRUnit_Decimeter"] = 0.1,
        ["esriInches"] = 0.0254,
        ["esriSRUnit_Inch"] = 0.0254,
        ["esriFeet"] = 0.3048,
        ["esriSRUnit_Foot"] = 0.3048,
        ["esriSurveyFeet"] = 0.3048006096012192,
        ["esriSRUnit_SurveyFoot"] = 0.3048006096012192,
        ["esriKilometers"] = 1000.0,
        ["esriSRUnit_Kilometer"] = 1000.0,
        ["esriMiles"] = 1609.344,
        ["esriSRUnit_StatuteMile"] = 1609.344,
        ["esriSurveyMiles"] = 1609.3472186944373,
        ["esriSRUnit_SurveyMile"] = 1609.3472186944373,
        ["esriNauticalMiles"] = 1852.0,
        ["esriSRUnit_NauticalMile"] = 1852.0,
        ["esriYards"] = 0.9144,
        ["esriSRUnit_Yard"] = 0.9144,
        // Numeric codes
        ["9001"] = 1.0,        // meters
        ["9002"] = 0.3048,     // international feet
        ["9003"] = 0.3048006096012192, // survey feet
        ["109008"] = 0.0254,   // inches
        ["109006"] = 0.01,     // centimeters
        ["109007"] = 0.001,    // millimeters
        ["109005"] = 0.1,      // decimeters
        ["9036"] = 1000.0,     // kilometers
        ["9035"] = 1609.3472186944373, // survey miles
        ["9093"] = 1609.344,   // miles
        ["9030"] = 1852.0,     // nautical miles
        ["9096"] = 0.9144,     // yards
    }.ToFrozenDictionary();

    /// <summary>
    /// Reads request parameters from the query string (GET) or body (POST form-data / JSON).
    /// </summary>
    public static async Task<(IReadOnlyDictionary<string, StringValues>? Values, string? Error)> TryReadRequestValuesAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        // GET: use query string
        if (HttpMethods.IsGet(request.Method))
        {
            if (request.Query.Count == 0)
            {
                return (null, "Query string parameters are required.");
            }

            return (ToCaseInsensitiveDictionary(request.Query), null);
        }

        // POST: form-data
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(cancellationToken);
            var values = form.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            return (values, null);
        }

        // POST: JSON body
        if (request.ContentLength is 0)
        {
            return (null, "Request body is required.");
        }

        if (!IsSupportedJsonContentType(request.ContentType))
        {
            var receivedContentType = string.IsNullOrWhiteSpace(request.ContentType)
                ? "(missing)"
                : request.ContentType.Split(';', 2)[0].Trim();
            return (null, UnsupportedMediaTypeErrorPrefix + receivedContentType);
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "Invalid request body.");
            }

            var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var converted = ConvertJsonValue(property.Value);
                if (!StringValues.IsNullOrEmpty(converted))
                {
                    values[property.Name] = converted;
                }
            }

            return (values, null);
        }
        catch (JsonException)
        {
            return (null, "Invalid JSON payload.");
        }
    }

    public static bool TryGetUnsupportedMediaType(string? error, out string? receivedContentType)
    {
        receivedContentType = null;
        if (string.IsNullOrWhiteSpace(error) ||
            !error.StartsWith(UnsupportedMediaTypeErrorPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        receivedContentType = error[UnsupportedMediaTypeErrorPrefix.Length..];
        return true;
    }

    private static bool IsSupportedJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var mediaType = contentType.Split(';', 2)[0].Trim();
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
               (mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase) &&
                mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses the ArcGIS geometry wrapper format: {"geometryType":"...","geometries":[...]}
    /// Also supports legacy flat array: [{...},{...}].
    /// </summary>
    public static (string[] GeometryJsonStrings, string? GeometryType, string? Error) ParseGeometries(
        string? raw,
        int maxGeometryCount,
        int maxGeometryJsonLength)
    {
        if (maxGeometryCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxGeometryCount), "Maximum geometry count must be at least 1.");
        }

        if (maxGeometryJsonLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxGeometryJsonLength), "Maximum geometry size must be at least 1 character.");
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return ([], null, "Parameter 'geometries' is required.");
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // ArcGIS wrapper: {"geometryType":"...","geometries":[...]}
            if (root.ValueKind == JsonValueKind.Object)
            {
                string? geometryType = null;
                if (root.TryGetProperty("geometryType", out var gtElement) && gtElement.ValueKind == JsonValueKind.String)
                {
                    geometryType = gtElement.GetString();
                }

                if (root.TryGetProperty("geometries", out var geomArray) && geomArray.ValueKind == JsonValueKind.Array)
                {
                    var geometryCount = geomArray.GetArrayLength();
                    if (geometryCount > maxGeometryCount)
                    {
                        return ([], geometryType, $"Parameter 'geometries' exceeds the maximum of {maxGeometryCount} geometries.");
                    }

                    var geometries = new string[geometryCount];
                    var i = 0;
                    foreach (var item in geomArray.EnumerateArray())
                    {
                        var rawGeometry = item.GetRawText();
                        if (rawGeometry.Length > maxGeometryJsonLength)
                        {
                            return ([], geometryType, $"Geometry at index {i} exceeds the maximum size of {maxGeometryJsonLength} characters.");
                        }

                        geometries[i++] = rawGeometry;
                    }

                    return (geometries, geometryType, null);
                }

                return ([], geometryType, "Geometry wrapper object must contain a 'geometries' array.");
            }

            // Legacy flat array: [{...},{...}]
            if (root.ValueKind == JsonValueKind.Array)
            {
                var geometryCount = root.GetArrayLength();
                if (geometryCount > maxGeometryCount)
                {
                    return ([], null, $"Parameter 'geometries' exceeds the maximum of {maxGeometryCount} geometries.");
                }

                var geometries = new string[geometryCount];
                var i = 0;
                foreach (var item in root.EnumerateArray())
                {
                    var rawGeometry = item.GetRawText();
                    if (rawGeometry.Length > maxGeometryJsonLength)
                    {
                        return ([], null, $"Geometry at index {i} exceeds the maximum size of {maxGeometryJsonLength} characters.");
                    }

                    geometries[i++] = rawGeometry;
                }

                return (geometries, null, null);
            }

            return ([], null, "Parameter 'geometries' must be a JSON object or array.");
        }
        catch (JsonException)
        {
            return ([], null, "Parameter 'geometries' contains invalid JSON.");
        }
    }

    /// <summary>
    /// Parses a single geometry object from request parameters.
    /// </summary>
    public static (string? GeometryJson, string? Error) ParseSingleGeometry(
        string? raw,
        string parameterName = "geometry",
        int maxGeometryJsonLength = int.MaxValue)
    {
        if (maxGeometryJsonLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxGeometryJsonLength), "Maximum geometry size must be at least 1 character.");
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, $"Parameter '{parameterName}' is required.");
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, $"Parameter '{parameterName}' must be a JSON object.");
            }

            var geometryJson = doc.RootElement.GetRawText();
            if (geometryJson.Length > maxGeometryJsonLength)
            {
                return (null, $"Parameter '{parameterName}' exceeds the maximum size of {maxGeometryJsonLength} characters.");
            }

            return (geometryJson, null);
        }
        catch (JsonException)
        {
            return (null, $"Parameter '{parameterName}' contains invalid JSON.");
        }
    }

    /// <summary>
    /// Parses a spatial reference: plain integer "4326" or JSON object {"wkid":4326}.
    /// </summary>
    public static (int Srid, string? Error) ParseSpatialReference(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (0, null);
        }

        // Try plain integer first
        if (int.TryParse(raw, CultureInfo.InvariantCulture, out var srid))
        {
            return srid > 0 ? (srid, null) : (0, "Spatial reference WKID must be a positive integer.");
        }

        // Try JSON object {"wkid":N} or {"wkid":N,"latestWkid":M}
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("wkid", out var wkidElement) &&
                wkidElement.TryGetInt32(out var wkid) &&
                wkid > 0)
            {
                return (wkid, null);
            }

            return (0, "Spatial reference object must contain a positive 'wkid' property.");
        }
        catch (JsonException)
        {
            return (0, $"Invalid spatial reference value: '{raw}'.");
        }
    }

    /// <summary>
    /// Returns the unit multiplier for converting to meters.
    /// Supports string names (esriMeters) and numeric codes (9001).
    /// </summary>
    public static double GetUnitMultiplier(string? unit)
    {
        if (string.IsNullOrEmpty(unit))
        {
            return 1.0;
        }

        return _unitMultipliers.GetValueOrDefault(unit, 1.0);
    }

    /// <summary>
    /// Parses a comma-separated string of doubles.
    /// </summary>
    public static (double[] Values, string? Error) ParseDoubleArray(string? raw, int maxValues = int.MaxValue)
    {
        if (maxValues < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxValues), "Maximum values must be at least 1.");
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return ([], null);
        }

        // Try JSON array first
        if (raw.TrimStart().StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var length = doc.RootElement.GetArrayLength();
                    if (length > maxValues)
                    {
                        return ([], $"Parameter 'distances' exceeds the maximum of {maxValues} values.");
                    }

                    var values = new double[length];
                    var i = 0;
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (!item.TryGetDouble(out var d))
                        {
                            return ([], $"Invalid number in distances array at index {i}.");
                        }

                        values[i++] = d;
                    }

                    return (values, null);
                }
            }
            catch (JsonException)
            {
                return ([], "Invalid JSON in distances parameter.");
            }
        }

        // Comma-separated
        var parts = raw.Split(',');
        if (parts.Length > maxValues)
        {
            return ([], $"Parameter 'distances' exceeds the maximum of {maxValues} values.");
        }

        var result = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i].Trim(), CultureInfo.InvariantCulture, out var d))
            {
                return ([], $"Invalid number in distances: '{parts[i].Trim()}'.");
            }

            result[i] = d;
        }

        return (result, null);
    }

    /// <summary>
    /// Parses a boolean string value.
    /// </summary>
    public static bool ParseBool(string? raw)
    {
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates the f (format) parameter. Only JSON is supported.
    /// </summary>
    public static string? ValidateFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return null; // default to json
        }

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, "pjson", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"Format '{format}' is not supported. Only 'json' is supported.";
    }

    private static StringValues ConvertJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Array => new StringValues(element.GetRawText()),
            JsonValueKind.String => new StringValues(element.GetString() ?? string.Empty),
            JsonValueKind.Number => new StringValues(element.ToString()),
            JsonValueKind.True => new StringValues("true"),
            JsonValueKind.False => new StringValues("false"),
            JsonValueKind.Object => new StringValues(element.GetRawText()),
            _ => StringValues.Empty
        };
    }

    private static Dictionary<string, StringValues> ToCaseInsensitiveDictionary(IQueryCollection values)
    {
        if (values.Count == 0)
        {
            return new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        }

        return values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets a string value from the request values dictionary (case-insensitive).
    /// </summary>
    public static string? GetValue(IReadOnlyDictionary<string, StringValues> values, string key)
    {
        return values.TryGetValue(key, out var raw) ? raw.ToString() : null;
    }
}
