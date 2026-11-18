// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Queries.Filters;
using NetTopologySuite.IO;

namespace Honua.Core.Features.Authorization.Services;

internal static class RlsPolicyRestrictions
{
    private const int MaximumFilterLength = 4096;
    private const int MaximumAreaLength = 65536;
    private const int MaximumAreaDepth = 16;

    public static FilterExpression? Parse(
        IFilterExpressionService filters,
        string? filterExpression,
        string? areaGeometry,
        string? geometryField)
    {
        ArgumentNullException.ThrowIfNull(filters);
        FilterExpression? expression = null;
        if (filterExpression is not null)
        {
            if (string.IsNullOrWhiteSpace(filterExpression) || filterExpression.Length > MaximumFilterLength)
            {
                throw new ArgumentException("The configured policy filter is invalid.", nameof(filterExpression));
            }

            expression = RequireExpression(filters.Parse(FilterLanguage.Cql2Text, filterExpression));
        }

        if (areaGeometry is null)
        {
            return expression;
        }

        if (string.IsNullOrWhiteSpace(geometryField) || areaGeometry.Length > MaximumAreaLength)
        {
            throw new ArgumentException("The configured policy area is invalid.", nameof(areaGeometry));
        }

        var area = ParseArea(filters, areaGeometry, geometryField);
        return expression is null ? area : new BinaryExpression(expression, BinaryOperator.And, area);
    }

    private static FilterExpression ParseArea(
        IFilterExpressionService filters, string areaGeometry, string geometryField)
    {
        try
        {
            using var document = JsonDocument.Parse(areaGeometry, new JsonDocumentOptions { MaxDepth = MaximumAreaDepth });
            ValidateArea(document.RootElement);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("op", "s_intersects");
                writer.WriteStartArray("args");
                writer.WriteStartObject();
                writer.WriteString("property", geometryField);
                writer.WriteEndObject();
                document.RootElement.WriteTo(writer);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            var expression = RequireExpression(filters.Parse(FilterLanguage.Cql2Json, Encoding.UTF8.GetString(stream.ToArray())));
            if (expression is not SpatialPredicate { Right: GeometryLiteral literal } ||
                !new WKBReader().Read(literal.Wkb).IsValid)
            {
                throw new ArgumentException("The configured policy area must have valid polygon topology.", nameof(areaGeometry));
            }

            return expression;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The configured policy area is invalid.", nameof(areaGeometry), ex);
        }
    }

    private static FilterExpression RequireExpression(FilterParseResult result)
        => result.IsSuccess && result.Expression is not null
            ? result.Expression
            : throw new ArgumentException("The configured policy restriction could not be parsed.");

    private static void ValidateArea(JsonElement area)
    {
        if (area.ValueKind != JsonValueKind.Object ||
            !area.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            type.GetString() is not ("Polygon" or "MultiPolygon") ||
            area.TryGetProperty("crs", out _) || area.TryGetProperty("bbox", out _) ||
            !area.TryGetProperty("coordinates", out var coordinates))
        {
            throw new ArgumentException("A policy area must be a WGS84 polygon or multipolygon.");
        }

        ValidateCoordinates(coordinates, type.GetString() == "Polygon" ? 2 : 3);
    }

    private static void ValidateCoordinates(JsonElement coordinates, int depth)
    {
        if (coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() == 0)
        {
            throw new ArgumentException("A policy area must contain coordinates.");
        }

        if (depth == 0)
        {
            if (coordinates.GetArrayLength() != 2 ||
                coordinates[0].ValueKind != JsonValueKind.Number ||
                !coordinates[0].TryGetDouble(out var x) || !double.IsFinite(x) || x is < -180 or > 180 ||
                coordinates[1].ValueKind != JsonValueKind.Number ||
                !coordinates[1].TryGetDouble(out var y) || !double.IsFinite(y) || y is < -90 or > 90)
            {
                throw new ArgumentException("A policy area must contain WGS84 coordinates.");
            }

            return;
        }

        foreach (var child in coordinates.EnumerateArray())
        {
            ValidateCoordinates(child, depth - 1);
        }
    }
}
