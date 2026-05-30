// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Server.Features.Protocols.Ogc.Api.Features.Services;

internal static class OgcGeoJsonGeometryShapeValidator
{
    public static GeometryShapeValidationResult GetValidationResult(JsonElement geometryElement, string type)
    {
        return type switch
        {
            "Point" => IsValidPosition(GetCoordinates(geometryElement)) ? GeometryShapeValidationResult.Valid : GeometryShapeValidationResult.Invalid,
            "MultiPoint" => IsPositionArrayArray(GetCoordinates(geometryElement), minPositions: 1) ? GeometryShapeValidationResult.Valid : GeometryShapeValidationResult.Invalid,
            "LineString" => IsPositionArrayArray(GetCoordinates(geometryElement), minPositions: 2) ? GeometryShapeValidationResult.Valid : GeometryShapeValidationResult.Invalid,
            "MultiLineString" => IsLineStringArray(GetCoordinates(geometryElement), minLineStrings: 1) ? GeometryShapeValidationResult.Valid : GeometryShapeValidationResult.Invalid,
            "Polygon" => IsPolygonCoordinates(GetCoordinates(geometryElement)) ? GeometryShapeValidationResult.Valid : GeometryShapeValidationResult.Invalid,
            "MultiPolygon" => IsMultiPolygonCoordinates(GetCoordinates(geometryElement)) ? GeometryShapeValidationResult.Valid : GeometryShapeValidationResult.Invalid,
            "GeometryCollection" => IsGeometryCollectionShape(geometryElement) ? GeometryShapeValidationResult.Valid : GeometryShapeValidationResult.Invalid,
            _ => GeometryShapeValidationResult.UnknownType
        };
    }

    public static bool IsKnownValidGeometry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var type = typeElement.GetString();
        return !string.IsNullOrWhiteSpace(type) &&
            GetValidationResult(element, type) == GeometryShapeValidationResult.Valid;
    }

    private static JsonElement? GetCoordinates(JsonElement geometryElement)
    {
        return geometryElement.TryGetProperty("coordinates", out var coordinates) ? coordinates : null;
    }

    private static bool IsLineStringArray(JsonElement? value, int minLineStrings)
    {
        if (value is not { ValueKind: JsonValueKind.Array } element)
        {
            return false;
        }

        var count = 0;
        foreach (var lineString in element.EnumerateArray())
        {
            if (!IsPositionArrayArray(lineString, minPositions: 2))
            {
                return false;
            }

            count++;
        }

        return count >= minLineStrings;
    }

    private static bool IsPolygonCoordinates(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Array } element)
        {
            return false;
        }

        var ringCount = 0;
        foreach (var ring in element.EnumerateArray())
        {
            if (!IsLinearRing(ring))
            {
                return false;
            }

            ringCount++;
        }

        return ringCount > 0;
    }

    private static bool IsMultiPolygonCoordinates(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Array } element)
        {
            return false;
        }

        var polygonCount = 0;
        foreach (var polygon in element.EnumerateArray())
        {
            if (!IsPolygonCoordinates(polygon))
            {
                return false;
            }

            polygonCount++;
        }

        return polygonCount > 0;
    }

    private static bool IsPositionArrayArray(JsonElement? value, int minPositions)
    {
        if (value is not { ValueKind: JsonValueKind.Array } element)
        {
            return false;
        }

        var count = 0;
        foreach (var position in element.EnumerateArray())
        {
            if (!IsValidPosition(position))
            {
                return false;
            }

            count++;
        }

        return count >= minPositions;
    }

    private static bool IsLinearRing(JsonElement ring)
    {
        if (!IsPositionArrayArray(ring, minPositions: 4))
        {
            return false;
        }

        using var enumerator = ring.EnumerateArray();
        if (!enumerator.MoveNext())
        {
            return false;
        }

        var first = enumerator.Current;
        var last = first;
        while (enumerator.MoveNext())
        {
            last = enumerator.Current;
        }

        return PositionsEqual(first, last);
    }

    private static bool IsGeometryCollectionShape(JsonElement geometryElement)
    {
        if (!geometryElement.TryGetProperty("geometries", out var geometries) ||
            geometries.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var childGeometry in geometries.EnumerateArray())
        {
            if (childGeometry.ValueKind != JsonValueKind.Object ||
                !childGeometry.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var type = typeElement.GetString();
            if (string.IsNullOrWhiteSpace(type) ||
                GetValidationResult(childGeometry, type) != GeometryShapeValidationResult.Valid)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidPosition(JsonElement? positionElement)
    {
        if (positionElement is not { ValueKind: JsonValueKind.Array } element)
        {
            return false;
        }

        var ordinalCount = 0;
        foreach (var ordinate in element.EnumerateArray())
        {
            if (ordinate.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            ordinalCount++;
        }

        return ordinalCount >= 2;
    }

    private static bool PositionsEqual(JsonElement first, JsonElement second)
    {
        var firstOrdinates = first.EnumerateArray().ToArray();
        var secondOrdinates = second.EnumerateArray().ToArray();
        if (firstOrdinates.Length != secondOrdinates.Length)
        {
            return false;
        }

        for (var i = 0; i < firstOrdinates.Length; i++)
        {
            if (!firstOrdinates[i].TryGetDouble(out var firstValue) ||
                !secondOrdinates[i].TryGetDouble(out var secondValue) ||
                firstValue != secondValue)
            {
                return false;
            }
        }

        return true;
    }
}

internal enum GeometryShapeValidationResult
{
    UnknownType,
    Invalid,
    Valid
}
