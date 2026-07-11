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
