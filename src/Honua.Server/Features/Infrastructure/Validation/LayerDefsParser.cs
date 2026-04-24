// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Validation.Abstractions;

namespace Honua.Server.Features.Infrastructure.Validation;

/// <summary>
/// Shared parser for ArcGIS REST layerDefs values.
/// </summary>
internal static class LayerDefsParser
{
    private const string InvalidLayerDefsJsonMessage = "layerDefs contains invalid JSON.";

    public static bool TryParse(
        string? layerDefsValue,
        ICommonQueryValidator queryValidator,
        out Dictionary<int, string?> layerDefs,
        out string? error)
    {
        layerDefs = new Dictionary<int, string?>();
        error = null;

        if (string.IsNullOrWhiteSpace(layerDefsValue))
        {
            return true;
        }

        var trimmed = layerDefsValue.Trim();
        return trimmed.StartsWith('{')
            ? TryParseJson(trimmed, queryValidator, layerDefs, out error)
            : TryParsePairs(trimmed, queryValidator, layerDefs, out error);
    }

    private static bool TryParseJson(
        string layerDefsValue,
        ICommonQueryValidator queryValidator,
        Dictionary<int, string?> layerDefs,
        out string? error)
    {
        error = null;

        try
        {
            using var document = JsonDocument.Parse(layerDefsValue);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "layerDefs must be a JSON object.";
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
                {
                    error = $"Invalid layer id '{property.Name}' in layerDefs.";
                    return false;
                }

                string? where;
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    where = property.Value.GetString();
                }
                else if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    where = null;
                }
                else
                {
                    error = $"Invalid layerDefs value for layer '{property.Name}'. Expected a string.";
                    return false;
                }

                if (!ValidateWhere(where, queryValidator, out error))
                {
                    return false;
                }

                layerDefs[layerId] = string.IsNullOrWhiteSpace(where) ? null : where;
            }

            return true;
        }
        catch (JsonException)
        {
            error = InvalidLayerDefsJsonMessage;
            return false;
        }
    }

    private static bool TryParsePairs(
        string layerDefsValue,
        ICommonQueryValidator queryValidator,
        Dictionary<int, string?> layerDefs,
        out string? error)
    {
        error = null;

        var pairs = layerDefsValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pair in pairs)
        {
            var separatorIndex = pair.IndexOf(':');
            if (separatorIndex <= 0)
            {
                error = "layerDefs must use the format: layerId:where;layerId:where";
                return false;
            }

            var idPart = pair[..separatorIndex].Trim();
            var where = separatorIndex == pair.Length - 1
                ? string.Empty
                : pair[(separatorIndex + 1)..].Trim();

            if (!int.TryParse(idPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
            {
                error = $"Invalid layer id '{idPart}' in layerDefs.";
                return false;
            }

            if (!ValidateWhere(where, queryValidator, out error))
            {
                return false;
            }

            layerDefs[layerId] = string.IsNullOrWhiteSpace(where) ? null : where;
        }

        return true;
    }

    private static bool ValidateWhere(string? where, ICommonQueryValidator queryValidator, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(where))
        {
            return true;
        }

        var validation = queryValidator.ValidateWhereClause(where);
        if (validation.IsValid)
        {
            return true;
        }

        error = validation.ErrorMessage ?? "Invalid layerDefs where clause.";
        return false;
    }
}
