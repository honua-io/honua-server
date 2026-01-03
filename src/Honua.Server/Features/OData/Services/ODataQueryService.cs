// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Log category for OData query operations.
/// </summary>
internal sealed class ODataQueryLog;

/// <summary>
/// Service for handling OData query operations including filtering, ordering, pagination, and field selection.
/// Converts OData query parameters to SQL fragments and handles query result processing.
/// </summary>
internal sealed partial class ODataQueryService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ODataQueryService"/> class.
    /// </summary>
    public ODataQueryService()
    {
    }

    /// <summary>
    /// Builds a feature query from OData parameters with proper validation and conversion.
    /// </summary>
    public FeatureQuery BuildFeatureQuery(
        string? filter,
        string? orderby,
        int? resultRecordCount,
        int? resultOffset,
        LayerDefinition layer,
        out SpatialFilter? spatialFilter,
        out string? error)
    {
        spatialFilter = null;
        error = null;

        // Extract spatial filter from the filter expression
        var remainingFilter = filter;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            if (TryExtractSpatialFilter(filter, out var parsedSpatialFilter, out var nonSpatialFilter, out var spatialError))
            {
                spatialFilter = parsedSpatialFilter;
                remainingFilter = nonSpatialFilter;
            }
            else if (spatialError != null)
            {
                error = spatialError;
                return new FeatureQuery(); // Return empty query on error
            }
        }

        // Convert remaining filter to SQL
        var (sqlFragment, whereClause) = ConvertODataFilterToSqlFragment(remainingFilter);

        return new FeatureQuery
        {
            Where = whereClause,
            SqlFilter = sqlFragment,
            SpatialFilter = spatialFilter,
            SpatialReferenceSrid = layer.SpatialReference.Srid,
            OrderBy = ParseODataOrderBy(orderby, layer),
            Limit = resultRecordCount,
            Offset = resultOffset
        };
    }

    /// <summary>
    /// Applies basic filtering to layer collections using simple OData expressions.
    /// </summary>
    public IEnumerable<LayerDefinition> ApplyBasicFilter(
        IEnumerable<LayerDefinition> layers,
        string filter)
    {
        // Simple name filtering - production would use a proper OData expression parser
        if (filter.Contains("name", StringComparison.OrdinalIgnoreCase))
        {
            var nameMatch = LayerNameFilterRegex().Match(filter);
            if (nameMatch.Success)
            {
                var nameValue = nameMatch.Groups[1].Value;
                return layers.Where(l => string.Equals(l.Name, nameValue, StringComparison.OrdinalIgnoreCase));
            }
        }

        return layers;
    }

    /// <summary>
    /// Applies field selection to result objects using an AOT-compatible approach.
    /// </summary>
    public object[] ApplyFieldSelection(Dictionary<string, object?>[] data, string select)
    {
        var fields = select.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return data.Select(item =>
        {
            var dict = new Dictionary<string, object?>();

            if (item is IDictionary<string, object?> existingDict)
            {
                // If it's already a dictionary, filter based on selected fields
                foreach (var kvp in existingDict)
                {
                    if (fields.Contains(kvp.Key))
                    {
                        dict[kvp.Key] = kvp.Value;
                    }
                }
            }

            return dict;
        }).ToArray();
    }

    /// <summary>
    /// Converts basic OData $filter expressions to SQL WHERE clauses.
    /// Supports: eq, ne, gt, lt, ge, le, contains, startswith, endswith, geo.distance, geo.intersects
    /// </summary>
    public (SqlFragment? sqlFragment, string? whereClause) ConvertODataFilterToSqlFragment(string? odataFilter)
    {
        if (string.IsNullOrWhiteSpace(odataFilter))
            return (null, null);

        var sql = odataFilter;
        var parameters = new List<object?>();
        var paramIndex = 0; // Start from 0 for @p0, @p1, etc.

        // Handle spatial functions first
        sql = ProcessSpatialFunctions(sql, parameters, ref paramIndex);

        // Handle string functions
        sql = ProcessStringFunctions(sql, parameters, ref paramIndex);

        // Replace OData operators with SQL equivalents
        sql = sql
            .Replace(" eq ", " = ", StringComparison.OrdinalIgnoreCase)
            .Replace(" ne ", " <> ", StringComparison.OrdinalIgnoreCase)
            .Replace(" gt ", " > ", StringComparison.OrdinalIgnoreCase)
            .Replace(" lt ", " < ", StringComparison.OrdinalIgnoreCase)
            .Replace(" ge ", " >= ", StringComparison.OrdinalIgnoreCase)
            .Replace(" le ", " <= ", StringComparison.OrdinalIgnoreCase)
            .Replace(" and ", " AND ", StringComparison.OrdinalIgnoreCase)
            .Replace(" or ", " OR ", StringComparison.OrdinalIgnoreCase);

        // Convert OData field references to JSONB queries with parameterization
        sql = ProcessFieldComparisons(sql, parameters, ref paramIndex);

        // If we have parameters, return SqlFragment; otherwise fallback to string
        if (parameters.Count > 0)
        {
            return (new SqlFragment(sql, parameters), null);
        }

        return (null, sql);
    }

    /// <summary>
    /// Processes spatial functions in the OData filter expression.
    /// </summary>
    private string ProcessSpatialFunctions(string sql, List<object?> parameters, ref int paramIndex)
    {
        var currentParamIndex = paramIndex;

        // Handle geo.distance(Geometry, geography'POINT(x y)') lt/gt value
        sql = Regex.Replace(
            sql,
            @"geo\.distance\(\s*(?<field>\w+)\s*,\s*geography'(?<geometry>[^']+)'\s*\)\s*(?<op>lt|gt|le|ge|eq|ne)\s*(?<distance>\d+(?:\.\d+)?)",
            match =>
            {
                var field = match.Groups["field"].Value;
                var geometry = match.Groups["geometry"].Value;
                var op = match.Groups["op"].Value;
                var distance = match.Groups["distance"].Value;

                var fieldSql = MapODataField(field);
                var sqlOp = ConvertODataOperator(op);

                // Add parameters for geometry and distance
                var geometryParamIndex = currentParamIndex++;
                var distanceParamIndex = currentParamIndex++;
                parameters.Add(geometry);
                parameters.Add(double.Parse(distance, CultureInfo.InvariantCulture));

                return $"ST_Distance({fieldSql}::geography, ST_GeomFromText(@p{geometryParamIndex})::geography) {sqlOp} @p{distanceParamIndex}";
            },
            RegexOptions.IgnoreCase);

        paramIndex = currentParamIndex;

        // Handle geo.intersects(Geometry, geography'POLYGON(...)')
        currentParamIndex = paramIndex;
        sql = Regex.Replace(
            sql,
            @"geo\.intersects\(\s*(?<field>\w+)\s*,\s*geography'(?<geometry>[^']+)'\s*\)",
            match =>
            {
                var field = match.Groups["field"].Value;
                var geometry = match.Groups["geometry"].Value;
                var fieldSql = MapODataField(field);

                var geometryParamIndex = currentParamIndex++;
                parameters.Add(geometry);

                return $"ST_Intersects({fieldSql}, ST_GeomFromText(@p{geometryParamIndex}))";
            },
            RegexOptions.IgnoreCase);

        paramIndex = currentParamIndex;

        // Handle geo.distance with specific geometry types
        sql = ProcessGeometryDistanceFunction(sql, parameters, ref paramIndex);
        sql = ProcessGeometryIntersectsFunction(sql, parameters, ref paramIndex);

        return sql;
    }

    /// <summary>
    /// Processes string functions in the OData filter expression.
    /// </summary>
    private string ProcessStringFunctions(string sql, List<object?> parameters, ref int paramIndex)
    {
        var currentParamIndex = paramIndex;

        // Handle contains function
        sql = Regex.Replace(
            sql,
            @"contains\(\s*(?<field>\w+)\s*,\s*'(?<value>[^']*)'\s*\)",
            match =>
            {
                var field = match.Groups["field"].Value;
                var value = match.Groups["value"].Value;
                var fieldSql = MapODataField(field);

                var valueParamIndex = currentParamIndex++;
                parameters.Add($"%{value}%");

                return $"{fieldSql} LIKE @p{valueParamIndex}";
            },
            RegexOptions.IgnoreCase);

        // Handle startswith function
        sql = Regex.Replace(
            sql,
            @"startswith\(\s*(?<field>\w+)\s*,\s*'(?<value>[^']*)'\s*\)",
            match =>
            {
                var field = match.Groups["field"].Value;
                var value = match.Groups["value"].Value;
                var fieldSql = MapODataField(field);

                var valueParamIndex = currentParamIndex++;
                parameters.Add($"{value}%");

                return $"{fieldSql} LIKE @p{valueParamIndex}";
            },
            RegexOptions.IgnoreCase);

        // Handle endswith function
        sql = Regex.Replace(
            sql,
            @"endswith\(\s*(?<field>\w+)\s*,\s*'(?<value>[^']*)'\s*\)",
            match =>
            {
                var field = match.Groups["field"].Value;
                var value = match.Groups["value"].Value;
                var fieldSql = MapODataField(field);

                var valueParamIndex = currentParamIndex++;
                parameters.Add($"%{value}");

                return $"{fieldSql} LIKE @p{valueParamIndex}";
            },
            RegexOptions.IgnoreCase);

        paramIndex = currentParamIndex;
        return sql;
    }

    /// <summary>
    /// Processes field comparisons in the OData filter expression.
    /// </summary>
    private string ProcessFieldComparisons(string sql, List<object?> parameters, ref int paramIndex)
    {
        var currentParamIndex = paramIndex;

        var result = Regex.Replace(
            sql,
            @"\b(?<field>\w+)\s*(?<op>=|<>|>|<|>=|<=)\s*(?<value>('([^']*)')|(-?\d+(?:\.\d+)?)|true|false|null)",
            match =>
            {
                var field = match.Groups["field"].Value;
                var op = match.Groups["op"].Value;
                var value = match.Groups["value"].Value;
                var fieldLower = field.Trim().ToLowerInvariant();
                var isCoreField = fieldLower == "objectid" || fieldLower == "layerid";

                var fieldSql = MapODataField(field);
                var valueLower = value.ToLowerInvariant();

                if (valueLower == "null")
                {
                    return op == "<>"
                        ? $"{fieldSql} IS NOT NULL"
                        : $"{fieldSql} IS NULL";
                }

                var valueParamIndex = currentParamIndex++;

                if (valueLower is "true" or "false")
                {
                    var castedField = isCoreField ? fieldSql : $"({fieldSql})::boolean";
                    parameters.Add(bool.Parse(valueLower));
                    return $"{castedField} {op} @p{valueParamIndex}";
                }

                if (value.StartsWith('\'') && value.EndsWith('\''))
                {
                    parameters.Add(value.Substring(1, value.Length - 2));
                    return $"{fieldSql} {op} @p{valueParamIndex}";
                }

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numValue))
                {
                    var castedField = isCoreField ? fieldSql : $"({fieldSql})::double precision";
                    parameters.Add(numValue);
                    return $"{castedField} {op} @p{valueParamIndex}";
                }

                parameters.Add(value);
                return $"{fieldSql} {op} @p{valueParamIndex}";
            },
            RegexOptions.IgnoreCase);

        paramIndex = currentParamIndex;
        return result;
    }

    /// <summary>
    /// Processes geometry distance functions.
    /// </summary>
    private string ProcessGeometryDistanceFunction(string sql, List<object?> parameters, ref int paramIndex)
    {
        var currentParamIndex = paramIndex;

        var result = Regex.Replace(
            sql,
            @"geo\.distance\s*\(\s*geometry\s*,\s*geography'POINT\s*\(\s*(?<lon>-?\d+\.?\d*)\s+(?<lat>-?\d+\.?\d*)\s*\)'\s*\)\s*(?<op>lt|le|gt|ge|eq|ne)\s+(?<dist>\d+\.?\d*)",
            match =>
            {
                var lon = match.Groups["lon"].Value;
                var lat = match.Groups["lat"].Value;
                var op = MapODataOperatorToSql(match.Groups["op"].Value);
                var dist = match.Groups["dist"].Value;

                var lonParamIndex = currentParamIndex++;
                var latParamIndex = currentParamIndex++;
                var distParamIndex = currentParamIndex++;
                parameters.Add(double.Parse(lon, CultureInfo.InvariantCulture));
                parameters.Add(double.Parse(lat, CultureInfo.InvariantCulture));
                parameters.Add(double.Parse(dist, CultureInfo.InvariantCulture));

                return $"ST_Distance(geometry::geography, ST_SetSRID(ST_MakePoint(@p{lonParamIndex}, @p{latParamIndex}), 4326)::geography) {op} @p{distParamIndex}";
            },
            RegexOptions.IgnoreCase);

        paramIndex = currentParamIndex;
        return result;
    }

    /// <summary>
    /// Processes geometry intersects functions.
    /// </summary>
    private string ProcessGeometryIntersectsFunction(string sql, List<object?> parameters, ref int paramIndex)
    {
        var currentParamIndex = paramIndex;

        // Handle POLYGON intersects
        sql = Regex.Replace(
            sql,
            @"geo\.intersects\s*\(\s*geometry\s*,\s*geography'(?<wkt>POLYGON\s*\([^)]+\)\s*)'?\s*\)",
            match =>
            {
                var wkt = match.Groups["wkt"].Value;
                var wktParamIndex = currentParamIndex++;
                parameters.Add(wkt);
                return $"ST_Intersects(geometry, ST_SetSRID(ST_GeomFromText(@p{wktParamIndex}), 4326))";
            },
            RegexOptions.IgnoreCase);

        // Handle POINT intersects
        sql = Regex.Replace(
            sql,
            @"geo\.intersects\s*\(\s*geometry\s*,\s*geography'(?<wkt>POINT\s*\([^)]+\))'?\s*\)",
            match =>
            {
                var wkt = match.Groups["wkt"].Value;
                var wktParamIndex = currentParamIndex++;
                parameters.Add(wkt);
                return $"ST_Intersects(geometry, ST_SetSRID(ST_GeomFromText(@p{wktParamIndex}), 4326))";
            },
            RegexOptions.IgnoreCase);

        paramIndex = currentParamIndex;
        return sql;
    }

    /// <summary>
    /// Maps an OData field name to the corresponding SQL column reference.
    /// </summary>
    private static string MapODataField(string field)
    {
        var fieldName = field.Trim();
        var fieldLower = fieldName.ToLowerInvariant();

        if (fieldLower == "objectid")
        {
            return "objectid";
        }

        if (fieldLower == "layerid")
        {
            return "layer_id";
        }

        if (fieldLower == "geometry")
        {
            return "geometry";
        }

        return $"attributes->>'{fieldName}'";
    }

    /// <summary>
    /// Converts an OData comparison operator to its SQL equivalent.
    /// </summary>
    private static string ConvertODataOperator(string odataOp)
    {
        return odataOp.ToLowerInvariant() switch
        {
            "eq" => "=",
            "ne" => "<>",
            "gt" => ">",
            "ge" => ">=",
            "lt" => "<",
            "le" => "<=",
            _ => odataOp
        };
    }

    /// <summary>
    /// Maps OData comparison operators to SQL operators with error handling.
    /// </summary>
    private static string MapODataOperatorToSql(string op)
    {
        return op.ToLowerInvariant() switch
        {
            "eq" => "=",
            "ne" => "<>",
            "gt" => ">",
            "lt" => "<",
            "ge" => ">=",
            "le" => "<=",
            _ => throw new ArgumentException($"Unknown OData operator: {op}")
        };
    }

    /// <summary>
    /// Parses OData $orderby expression into OrderByClause array.
    /// Format: "field1 asc, field2 desc" or "field1, field2 desc"
    /// Default direction is ascending when not specified.
    /// </summary>
    private static ImmutableArray<OrderByClause>? ParseODataOrderBy(string? orderby, LayerDefinition layer)
    {
        if (string.IsNullOrWhiteSpace(orderby))
        {
            return null;
        }

        var clauses = new List<OrderByClause>();
        var parts = orderby.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                continue;
            }

            var fieldName = tokens[0].Trim();

            // Validate field name (alphanumeric and underscores only)
            if (!FieldNameRegex().IsMatch(fieldName))
            {
                throw new ArgumentException($"Invalid field name in $orderby: {fieldName}");
            }

            // Default to ascending, check for explicit direction
            var ascending = true;
            if (tokens.Length > 1)
            {
                var direction = tokens[1].Trim().ToLowerInvariant();
                if (direction == "desc")
                {
                    ascending = false;
                }
                else if (direction != "asc")
                {
                    throw new ArgumentException($"Invalid sort direction in $orderby: {direction}. Use 'asc' or 'desc'.");
                }
            }

            var fieldDefinition = layer.Fields.FirstOrDefault(f =>
                f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
            var resolvedField = fieldDefinition?.Name ?? fieldName;
            var fieldType = fieldDefinition?.Type;

            clauses.Add(new OrderByClause(resolvedField, ascending, fieldType));
        }

        return clauses.Count > 0 ? clauses.ToImmutableArray() : null;
    }

    /// <summary>
    /// Attempts to extract spatial filter from an OData filter expression.
    /// </summary>
    private bool TryExtractSpatialFilter(string filter, out SpatialFilter? spatialFilter, out string? nonSpatialFilter, out string? error)
    {
        spatialFilter = null;
        nonSpatialFilter = filter;
        error = null;

        if (string.IsNullOrWhiteSpace(filter))
        {
            return false;
        }

        var trimmed = filter.Trim();

        if (TryParseODataSpatialFilter(trimmed, out var parsedSpatialFilter, out error))
        {
            spatialFilter = parsedSpatialFilter;
            nonSpatialFilter = null;
            return true;
        }

        var parts = AndSplitRegex().Split(trimmed);
        if (parts.Length == 2)
        {
            if (TryParseODataSpatialFilter(parts[0].Trim(), out parsedSpatialFilter, out error))
            {
                spatialFilter = parsedSpatialFilter;
                nonSpatialFilter = parts[1].Trim();
                return true;
            }

            if (TryParseODataSpatialFilter(parts[1].Trim(), out parsedSpatialFilter, out error))
            {
                spatialFilter = parsedSpatialFilter;
                nonSpatialFilter = parts[0].Trim();
                return true;
            }
        }

        if (trimmed.Contains("geo.", StringComparison.OrdinalIgnoreCase))
        {
            error ??= "Unsupported spatial filter format.";
        }

        return false;
    }

    /// <summary>
    /// Attempts to parse an OData spatial filter expression.
    /// </summary>
    private bool TryParseODataSpatialFilter(string filter, out SpatialFilter spatialFilter, out string? error)
    {
        spatialFilter = default;
        error = null;

        var intersectsMatch = IntersectsRegex().Match(filter);

        if (intersectsMatch.Success)
        {
            var field = intersectsMatch.Groups["field"].Value;
            if (!field.Equals("geometry", StringComparison.OrdinalIgnoreCase))
            {
                error = "Spatial filters are only supported on Geometry.";
                return false;
            }

            if (!TryCreateWkbFromWkt(intersectsMatch.Groups["wkt"].Value, out var geometryWkb, out error))
            {
                return false;
            }

            spatialFilter = SpatialFilter.Create(geometryWkb, SpatialRelationship.Intersects, 4326);
            return true;
        }

        var distanceMatch = DistanceRegex().Match(filter);

        if (distanceMatch.Success)
        {
            var field = distanceMatch.Groups["field"].Value;
            if (!field.Equals("geometry", StringComparison.OrdinalIgnoreCase))
            {
                error = "Spatial filters are only supported on Geometry.";
                return false;
            }

            if (!TryCreateWkbFromWkt(distanceMatch.Groups["wkt"].Value, out var geometryWkb, out error))
            {
                return false;
            }

            if (!double.TryParse(distanceMatch.Groups["distance"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var distanceValue) ||
                distanceValue <= 0)
            {
                error = "Distance must be a positive number.";
                return false;
            }

            var op = distanceMatch.Groups["op"].Value.ToLowerInvariant();
            var withinDistance = op is "lt" or "le" or "eq";

            spatialFilter = SpatialFilter.CreateDistanceFilter(
                geometryWkb,
                distanceValue,
                DistanceUnit.Meters,
                withinDistance,
                4326);

            return true;
        }

        if (filter.Contains("geo.", StringComparison.OrdinalIgnoreCase))
        {
            error = "Unsupported spatial filter format.";
        }

        return false;
    }

    /// <summary>
    /// Attempts to create WKB from a WKT string.
    /// </summary>
    private bool TryCreateWkbFromWkt(string wkt, out byte[] geometryWkb, out string? error)
    {
        geometryWkb = Array.Empty<byte>();
        error = null;

        try
        {
            var reader = new NetTopologySuite.IO.WKTReader();
            var geometry = reader.Read(wkt);
            if (geometry == null)
            {
                error = "Invalid spatial filter geometry.";
                return false;
            }

            if (geometry.SRID == 0)
            {
                geometry.SRID = 4326;
            }

            var writer = new NetTopologySuite.IO.WKBWriter(NetTopologySuite.IO.ByteOrder.LittleEndian, handleSRID: true);
            geometryWkb = writer.Write(geometry);
            return true;
        }
        catch
        {
            error = "Invalid spatial filter geometry.";
            return false;
        }
    }

    /// <summary>
    /// Regex patterns for parsing various filter expressions.
    /// </summary>
    [GeneratedRegex(@"name\s+eq\s+'([^']*)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LayerNameFilterRegex();

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex FieldNameRegex();

    [GeneratedRegex(@"\s+and\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AndSplitRegex();

    [GeneratedRegex(@"^geo\.intersects\(\s*(?<field>\w+)\s*,\s*geography'(?<wkt>[^']+)'\s*\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntersectsRegex();

    [GeneratedRegex(@"^geo\.distance\(\s*(?<field>\w+)\s*,\s*geography'(?<wkt>[^']+)'\s*\)\s*(?<op>lt|le|gt|ge|eq|ne)\s*(?<distance>-?\d+(?:\.\d+)?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DistanceRegex();
}
