// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.NlQuery.Domain;
using Honua.Core.Queries.Filters;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Core.Features.NlQuery.Services;

/// <summary>
/// Compiles a <see cref="FilterPlan"/> into the existing <see cref="FilterExpression"/> AST.
/// Acts as a security boundary: only the allowed subset of operators maps to AST nodes,
/// and every property reference is validated against the layer schema.
/// </summary>
public static class FilterPlanCompiler
{
    private const int DefaultSrid = 4326;

    private static readonly HashSet<string> AllowedComparisonOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "neq", "lt", "lte", "gt", "gte", "like", "in"
    };

    private static readonly HashSet<string> AllowedSpatialOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "intersects", "within", "contains", "dwithin"
    };

    private static readonly HashSet<string> AllowedTemporalOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "before", "after", "during"
    };

    /// <summary>
    /// Compiles a filter plan into a <see cref="FilterExpression"/> AST, validating
    /// all property references and operators against the layer definition.
    /// </summary>
    /// <param name="plan">The filter plan to compile.</param>
    /// <param name="layer">The layer definition providing schema metadata.</param>
    /// <returns>A result containing either the compiled expression or an error.</returns>
    public static FilterPlanCompileResult Compile(FilterPlan plan, LayerDefinition layer)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(layer);

        if (plan.Clauses.Length == 0)
        {
            return FilterPlanCompileResult.Failure("Filter plan contains no clauses.");
        }

        try
        {
            var expression = CompileClauses(plan.Combinator, plan.Clauses, layer);
            return FilterPlanCompileResult.Success(expression);
        }
        catch (FilterPlanCompilationException ex)
        {
            return FilterPlanCompileResult.Failure(ex.Message);
        }
    }

    private static FilterExpression CompileClauses(
        FilterPlanCombinator combinator,
        FilterPlanClause[] clauses,
        LayerDefinition layer)
    {
        if (clauses.Length == 0)
        {
            throw new FilterPlanCompilationException("Empty clause list.");
        }

        var compiled = CompileClause(clauses[0], layer);

        for (var i = 1; i < clauses.Length; i++)
        {
            var right = CompileClause(clauses[i], layer);
            var op = combinator == FilterPlanCombinator.And ? BinaryOperator.And : BinaryOperator.Or;
            compiled = new BinaryExpression(compiled, op, right);
        }

        return compiled;
    }

    private static FilterExpression CompileClause(FilterPlanClause clause, LayerDefinition layer)
    {
        return clause.Type.ToLowerInvariant() switch
        {
            "comparison" => CompileComparison(
                clause.Comparison ?? throw new FilterPlanCompilationException("Comparison clause data is missing."),
                layer),
            "spatial" => CompileSpatial(
                clause.Spatial ?? throw new FilterPlanCompilationException("Spatial clause data is missing."),
                layer),
            "temporal" => CompileTemporal(
                clause.Temporal ?? throw new FilterPlanCompilationException("Temporal clause data is missing."),
                layer),
            "nested" => CompileNested(
                clause.Nested ?? throw new FilterPlanCompilationException("Nested clause data is missing."),
                layer),
            _ => throw new FilterPlanCompilationException($"Unknown clause type: '{clause.Type}'.")
        };
    }

    private static BinaryExpression CompileComparison(ComparisonClause clause, LayerDefinition layer)
    {
        if (!AllowedComparisonOperators.Contains(clause.Operator))
        {
            throw new FilterPlanCompilationException($"Unsupported comparison operator: '{clause.Operator}'.");
        }

        ValidatePropertyExists(clause.Property, layer);

        var property = new PropertyReference(clause.Property);

        if (string.Equals(clause.Operator, "in", StringComparison.OrdinalIgnoreCase))
        {
            var values = ParseInValues(clause.Value);
            return new BinaryExpression(property, BinaryOperator.In, values);
        }

        var op = MapComparisonOperator(clause.Operator);
        var literal = ParseLiteralValue(clause.Value);
        return new BinaryExpression(property, op, literal);
    }

    private static FilterExpression CompileSpatial(SpatialClause clause, LayerDefinition layer)
    {
        if (!AllowedSpatialOperators.Contains(clause.Operator))
        {
            throw new FilterPlanCompilationException($"Unsupported spatial operator: '{clause.Operator}'.");
        }

        var geometryLiteral = ParseGeoJsonGeometry(clause.Geometry);
        var geometryField = layer.GeometryField
            ?? throw new FilterPlanCompilationException("Layer does not have a geometry field.");
        var propertyRef = new PropertyReference(geometryField.Name);

        var spatialOp = MapSpatialOperator(clause.Operator);

        if (string.Equals(clause.Operator, "dwithin", StringComparison.OrdinalIgnoreCase))
        {
            var distance = clause.Distance
                ?? throw new FilterPlanCompilationException("Distance is required for dwithin operator.");

            var distanceMeters = ConvertDistanceToMeters(distance, clause.DistanceUnit);
            var distanceLiteral = new Literal(distanceMeters, LiteralType.Number);

            return new SpatialDistancePredicate(spatialOp, propertyRef, geometryLiteral, distanceLiteral);
        }

        return new SpatialPredicate(spatialOp, propertyRef, geometryLiteral);
    }

    private static TemporalPredicate CompileTemporal(TemporalClause clause, LayerDefinition layer)
    {
        if (!AllowedTemporalOperators.Contains(clause.Operator))
        {
            throw new FilterPlanCompilationException($"Unsupported temporal operator: '{clause.Operator}'.");
        }

        ValidatePropertyExists(clause.Property, layer);

        var property = new PropertyReference(clause.Property);
        var temporalOp = MapTemporalOperator(clause.Operator);

        switch (clause.Operator.ToLowerInvariant())
        {
            case "after":
                {
                    var start = ParseDateTimeLiteral(clause.Start, "start");
                    return new TemporalPredicate(temporalOp, property, start);
                }
            case "before":
                {
                    var end = ParseDateTimeLiteral(clause.End, "end");
                    return new TemporalPredicate(temporalOp, property, end);
                }
            case "during":
                {
                    var start = ParseDateTimeLiteral(clause.Start, "start");
                    var end = ParseDateTimeLiteral(clause.End, "end");
                    return new TemporalPredicate(temporalOp, property, new IntervalLiteral(start, end));
                }
            default:
                throw new FilterPlanCompilationException($"Unsupported temporal operator: '{clause.Operator}'.");
        }
    }

    private static FilterExpression CompileNested(NestedClause clause, LayerDefinition layer)
    {
        if (clause.Clauses.Length == 0)
        {
            throw new FilterPlanCompilationException("Nested clause contains no sub-clauses.");
        }

        return CompileClauses(clause.Combinator, clause.Clauses, layer);
    }

    private static void ValidatePropertyExists(string propertyName, LayerDefinition layer)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            throw new FilterPlanCompilationException("Property name cannot be empty.");
        }

        var exists = false;
        foreach (var field in layer.Fields)
        {
            if (string.Equals(field.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        }

        if (!exists)
        {
            throw new FilterPlanCompilationException(
                $"Property '{propertyName}' does not exist in layer '{layer.Name}'.");
        }
    }

    private static BinaryOperator MapComparisonOperator(string op)
    {
        return op.ToLowerInvariant() switch
        {
            "eq" => BinaryOperator.Equal,
            "neq" => BinaryOperator.NotEqual,
            "lt" => BinaryOperator.LessThan,
            "lte" => BinaryOperator.LessThanOrEqual,
            "gt" => BinaryOperator.GreaterThan,
            "gte" => BinaryOperator.GreaterThanOrEqual,
            "like" => BinaryOperator.Like,
            _ => throw new FilterPlanCompilationException($"Unsupported comparison operator: '{op}'.")
        };
    }

    private static SpatialOperator MapSpatialOperator(string op)
    {
        return op.ToLowerInvariant() switch
        {
            "intersects" => SpatialOperator.Intersects,
            "within" => SpatialOperator.Within,
            "contains" => SpatialOperator.Contains,
            "dwithin" => SpatialOperator.DWithin,
            _ => throw new FilterPlanCompilationException($"Unsupported spatial operator: '{op}'.")
        };
    }

    private static TemporalOperator MapTemporalOperator(string op)
    {
        return op.ToLowerInvariant() switch
        {
            "before" => TemporalOperator.Before,
            "after" => TemporalOperator.After,
            "during" => TemporalOperator.During,
            _ => throw new FilterPlanCompilationException($"Unsupported temporal operator: '{op}'.")
        };
    }

    private static GeometryLiteral ParseGeoJsonGeometry(object? geometry)
    {
        if (geometry is null)
        {
            throw new FilterPlanCompilationException("Spatial clause geometry is null.");
        }

        string geoJson;
        if (geometry is JsonElement jsonElement)
        {
            geoJson = jsonElement.GetRawText();
        }
        else
        {
            geoJson = JsonSerializer.Serialize(geometry);
        }

        try
        {
            var reader = new GeoJsonReader();
            var geom = reader.Read<NtsGeometry>(geoJson)
                ?? throw new FilterPlanCompilationException("Geometry could not be parsed from GeoJSON.");

            // GeoJSON without an explicit CRS defaults to WGS 84 (EPSG:4326) per RFC 7946.
            // The downstream filter translator handles ST_Transform to the layer's SRID.
            var srid = geom.SRID > 0 ? geom.SRID : DefaultSrid;
            var writer = new WKBWriter();
            var wkb = writer.Write(geom);

            return new GeometryLiteral(wkb, srid, geoJson);
        }
        catch (FilterPlanCompilationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FilterPlanCompilationException($"Invalid GeoJSON geometry: {ex.Message}");
        }
    }

    private static Literal ParseLiteralValue(object? value)
    {
        if (value is null)
        {
            return new Literal(null, LiteralType.Null);
        }

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => new Literal(element.GetString(), LiteralType.Text),
                JsonValueKind.Number => new Literal(element.GetDouble(), LiteralType.Number),
                JsonValueKind.True => new Literal(true, LiteralType.Boolean),
                JsonValueKind.False => new Literal(false, LiteralType.Boolean),
                JsonValueKind.Null => new Literal(null, LiteralType.Null),
                _ => throw new FilterPlanCompilationException($"Unsupported JSON value kind: {element.ValueKind}.")
            };
        }

        return value switch
        {
            string s => new Literal(s, LiteralType.Text),
            bool b => new Literal(b, LiteralType.Boolean),
            int i => new Literal((double)i, LiteralType.Number),
            long l => new Literal((double)l, LiteralType.Number),
            float f => new Literal((double)f, LiteralType.Number),
            double d => new Literal(d, LiteralType.Number),
            _ => new Literal(value.ToString(), LiteralType.Text)
        };
    }

    private static ValueList ParseInValues(object? value)
    {
        if (value is null)
        {
            throw new FilterPlanCompilationException("IN operator requires a non-null value list.");
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            var values = new List<FilterExpression>();
            foreach (var item in element.EnumerateArray())
            {
                values.Add(ParseLiteralValue(item));
            }

            return new ValueList(values);
        }

        throw new FilterPlanCompilationException("IN operator value must be an array.");
    }

    private static Literal ParseDateTimeLiteral(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FilterPlanCompilationException($"Temporal clause '{paramName}' cannot be empty.");
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            return new Literal(dto, LiteralType.DateTime);
        }

        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, out _))
        {
            return new Literal(value, LiteralType.Date);
        }

        throw new FilterPlanCompilationException(
            $"Temporal clause '{paramName}' value '{value}' is not a valid ISO 8601 date/time.");
    }

    private static double ConvertDistanceToMeters(double distance, string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit) || string.Equals(unit, "meters", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(unit, "m", StringComparison.OrdinalIgnoreCase))
        {
            return distance;
        }

        return unit.ToLowerInvariant() switch
        {
            "kilometers" or "km" => distance * 1000.0,
            "miles" or "mi" => distance * 1609.344,
            "feet" or "ft" => distance * 0.3048,
            "yards" or "yd" => distance * 0.9144,
            _ => throw new FilterPlanCompilationException($"Unsupported distance unit: '{unit}'.")
        };
    }
}

/// <summary>
/// Exception thrown during filter plan compilation for expected validation failures.
/// Used internally by the compiler; callers receive <see cref="FilterPlanCompileResult"/>.
/// </summary>
internal sealed class FilterPlanCompilationException : Exception
{
    public FilterPlanCompilationException(string message) : base(message) { }
}
