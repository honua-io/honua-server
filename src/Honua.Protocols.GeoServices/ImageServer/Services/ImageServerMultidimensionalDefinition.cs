// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// A single dimension constraint parsed from an ImageServer request-side
/// <c>multidimensionalDefinition</c> (e.g. getSamples). Pins one variable's dimension to a set
/// of coordinate values that select a slice of a multidimensional cube.
/// </summary>
/// <param name="VariableName">The multidimensional variable (e.g. <c>temperature</c>); may be empty when the request omits it.</param>
/// <param name="DimensionName">The dimension being pinned (e.g. <c>StdTime</c>, <c>StdZ</c>).</param>
/// <param name="Values">The coordinate values selecting the slice along <paramref name="DimensionName"/>.</param>
/// <param name="IsSlice">The Esri <c>isSlice</c> flag indicating whether <paramref name="Values"/> denote a single slice.</param>
internal readonly record struct ImageServerDimensionConstraint(
    string VariableName,
    string DimensionName,
    double[] Values,
    bool IsSlice);

/// <summary>
/// Parses and validates the Esri request-side <c>multidimensionalDefinition</c> parameter — an
/// array of <c>{ variableName, dimensionName, values:[...], isSlice }</c> objects that select a
/// per-slice (time/StdZ coordinate) view of a registered multidimensional cube.
/// </summary>
/// <remarks>
/// This validates request shape only. Resolving a parsed constraint to an actual pixel read is
/// delegated to the canonical Zarr point-slice reader so ImageServer adapters do not duplicate
/// storage, coordinate, or dimension-index behavior.
/// </remarks>
internal static class ImageServerMultidimensionalDefinition
{
    private const int MaxDefinitionLength = 8192;
    private const int MaxConstraints = 8;

    /// <summary>Reads the documented exportImage mosaic-rule dimension filter.</summary>
    public static bool TryParseExport(string? raw, string? mosaicRule,
        out IReadOnlyList<ImageServerDimensionConstraint> constraints, out string? error)
    {
        if (!TryParse(raw, out constraints, out error) || string.IsNullOrWhiteSpace(mosaicRule))
        {
            return error is null;
        }
        try
        {
            using var document = JsonDocument.Parse(mosaicRule);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                // Preserve ordinary mosaic parsing when there is no nested filter.
                return true;
            }
            var definitions = document.RootElement.EnumerateObject()
                .Where(static property => property.NameEquals("multidimensionalDefinition")).ToArray();
            if (definitions.Length == 0)
            {
                return true;
            }
            if (definitions.Length != 1 || !string.IsNullOrWhiteSpace(raw))
            {
                error = "Specify multidimensionalDefinition once, either directly or inside mosaicRule.";
                return false;
            }
            // A single coordinate-selected variable has no raster ordering ambiguity.
            // Permit only ordinary ordering hints; filtering, locking, merge operations
            // and other mosaic transforms must not be silently ignored by the slice reader.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var orderingStream = new MemoryStream();
            using var orderingWriter = new Utf8JsonWriter(orderingStream);
            orderingWriter.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name) || property.Name is not ("multidimensionalDefinition" or "mosaicMethod" or
                    "sortField" or "sortValue" or "ascending"))
                {
                    error = "Coordinate-selected slices support only dimension filters and acquisition-date ordering hints.";
                    return false;
                }
                if (property.Name is "multidimensionalDefinition")
                {
                    continue;
                }
                if (property.Name == "ascending"
                    ? property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    : property.Value.ValueKind != JsonValueKind.String)
                {
                    error = "Mosaic ordering hints must use their declared string/boolean types.";
                    return false;
                }
                // The coordinate filter chooses exactly one slice. Distance from
                // an ordering base cannot affect that slice. Keep ordinary catalog
                // mosaic parsing strict, including its unsupported base-sort rule.
                if (property.Name != "sortValue")
                {
                    property.WriteTo(orderingWriter);
                }
                else if (!DateTimeOffset.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out _))
                {
                    error = "A coordinate-selected acquisition ordering base must be a valid date.";
                    return false;
                }
            }
            orderingWriter.WriteEndObject();
            orderingWriter.Flush();
            if (!ImageServerMosaicRule.TryParse(System.Text.Encoding.UTF8.GetString(orderingStream.ToArray()),
                    out var parsed, out error, out _) ||
                parsed.Method is not (MosaicMethod.None or MosaicMethod.ByDate) ||
                (names.Contains("sortValue") && parsed.Method != MosaicMethod.ByDate))
            {
                if (string.IsNullOrWhiteSpace(error)) error = "Coordinate-selected slices do not support this mosaic method.";
                return false;
            }
            if (!TryParse(definitions[0].Value.GetRawText(), out constraints, out error)) return false;
            if (constraints.Count == 0 || constraints.Any(static constraint => constraint.Values.Length != 1))
            {
                error = "A mosaic dimension filter must select exactly one coordinate per dimension.";
                return false;
            }
            return true;
        }
        catch (JsonException)
        {
            // No nested JSON filter was read. The existing mosaic parser retains
            // ownership of bare operation tokens and malformed ordinary rules.
            return true;
        }
    }

    /// <summary>
    /// Attempts to parse the raw <c>multidimensionalDefinition</c> value. Returns <c>true</c>
    /// (with an empty <paramref name="constraints"/>) when the value is absent. Returns
    /// <c>false</c> with a client-facing <paramref name="error"/> when the value is present but
    /// malformed.
    /// </summary>
    public static bool TryParse(string? raw, out IReadOnlyList<ImageServerDimensionConstraint> constraints, out string? error)
    {
        constraints = Array.Empty<ImageServerDimensionConstraint>();
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (raw.Length > MaxDefinitionLength)
        {
            error = $"multidimensionalDefinition must not exceed {MaxDefinitionLength} characters.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            error = "multidimensionalDefinition must be a valid JSON array of dimension constraints.";
            return false;
        }

        using (document)
        {
            // Esri accepts either a bare array of constraints or an object that wraps the array
            // under "dimensions"; support both so ArcGIS clients round-trip cleanly.
            var array = document.RootElement;
            if (array.ValueKind == JsonValueKind.Object && array.TryGetProperty("dimensions", out var nested))
            {
                array = nested;
            }

            if (array.ValueKind != JsonValueKind.Array)
            {
                error = "multidimensionalDefinition must be a JSON array of dimension constraints.";
                return false;
            }

            if (array.GetArrayLength() > MaxConstraints)
            {
                error = $"multidimensionalDefinition supports at most {MaxConstraints} dimension constraints.";
                return false;
            }

            var parsed = new List<ImageServerDimensionConstraint>(array.GetArrayLength());
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    error = "Each multidimensionalDefinition entry must be a { variableName, dimensionName, values } object.";
                    return false;
                }

                var dimensionName = ReadString(element, "dimensionName");
                if (string.IsNullOrWhiteSpace(dimensionName))
                {
                    error = "Each multidimensionalDefinition entry requires a dimensionName.";
                    return false;
                }

                if (!TryReadValues(element, out var values, out var valuesError))
                {
                    error = valuesError;
                    return false;
                }

                var isSlice = element.TryGetProperty("isSlice", out var isSliceElement) &&
                              isSliceElement.ValueKind == JsonValueKind.True;

                parsed.Add(new ImageServerDimensionConstraint(
                    ReadString(element, "variableName") ?? string.Empty,
                    dimensionName!,
                    values,
                    isSlice));
            }

            constraints = parsed;
            return true;
        }
    }

    private static bool TryReadValues(JsonElement element, out double[] values, out string? error)
    {
        values = Array.Empty<double>();
        error = null;

        if (!element.TryGetProperty("values", out var valuesElement))
        {
            error = "Each multidimensionalDefinition entry requires a values array.";
            return false;
        }

        if (valuesElement.ValueKind != JsonValueKind.Array || valuesElement.GetArrayLength() == 0)
        {
            error = "multidimensionalDefinition values must be a non-empty array of numeric coordinates.";
            return false;
        }

        var parsed = new List<double>(valuesElement.GetArrayLength());
        foreach (var value in valuesElement.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var numeric))
            {
                parsed.Add(numeric);
                continue;
            }

            // Esri also encodes time coordinates as ISO-8601 strings; accept those as epoch ms.
            if (value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
            {
                parsed.Add(instant.ToUnixTimeMilliseconds());
                continue;
            }

            error = "multidimensionalDefinition values must be numeric coordinates or ISO-8601 instants.";
            return false;
        }

        values = parsed.ToArray();
        return true;
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
