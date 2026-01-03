// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Service for converting OData filter expressions to SQL fragments and spatial filters.
/// Extracted from ODataEndpoints to improve maintainability and testability.
/// </summary>
internal sealed class ODataFilterConverter
{
    /// <summary>
    /// Converts OData $filter expression to SQL fragment with parameterization
    /// </summary>
    public static (SqlFragment? SqlFragment, string? WhereClause) ConvertODataFilterToSqlFragment(string? odataFilter)
    {
        if (string.IsNullOrWhiteSpace(odataFilter))
            return (null, null);

        var processor = new ODataFilterProcessor(odataFilter);
        return processor.Process();
    }

    /// <summary>
    /// Attempts to extract spatial filter from OData expression
    /// </summary>
    public static bool TryExtractSpatialFilter(string filter, out SpatialFilter? spatialFilter, out string? nonSpatialFilter, out string? error)
    {
        spatialFilter = null;
        nonSpatialFilter = filter;
        error = null;

        if (string.IsNullOrWhiteSpace(filter))
        {
            return false;
        }

        var extractor = new SpatialFilterExtractor(filter.Trim());
        return extractor.TryExtract(out spatialFilter, out nonSpatialFilter, out error);
    }

    private sealed class ODataFilterProcessor
    {
        private string _sql;
        private readonly List<object?> _parameters;
        private int _paramIndex;

        public ODataFilterProcessor(string filter)
        {
            _sql = filter;
            _parameters = new List<object?>();
            _paramIndex = 0;
        }

        public (SqlFragment?, string?) Process()
        {
            ProcessSpatialFunctions();
            ProcessStringFunctions();
            ProcessOperators();
            ProcessFieldReferences();

            if (_parameters.Count > 0)
            {
                return (new SqlFragment(_sql, _parameters), null);
            }

            return (null, _sql);
        }

        private void ProcessSpatialFunctions()
        {
            // Process geo.distance functions
            _sql = Regex.Replace(
                _sql,
                @"geo\.distance\(\s*(?<field>\w+)\s*,\s*geography'(?<geometry>[^']+)'\s*\)\s*(?<op>lt|gt|le|ge|eq|ne)\s*(?<distance>\d+(?:\.\d+)?)",
                ProcessDistanceMatch,
                RegexOptions.IgnoreCase);

            // Process geo.intersects functions
            _sql = Regex.Replace(
                _sql,
                @"geo\.intersects\(\s*(?<field>\w+)\s*,\s*geography'(?<geometry>[^']+)'\s*\)",
                ProcessIntersectsMatch,
                RegexOptions.IgnoreCase);

            // Process geometry-specific spatial functions
            ProcessGeometryDistanceFunctions();
            ProcessGeometryIntersectsFunctions();
        }

        private void ProcessGeometryDistanceFunctions()
        {
            _sql = Regex.Replace(
                _sql,
                @"geo\.distance\s*\(\s*geometry\s*,\s*geography'POINT\s*\(\s*(?<lon>-?\d+\.?\d*)\s+(?<lat>-?\d+\.?\d*)\s*\)'\s*\)\s*(?<op>lt|le|gt|ge|eq|ne)\s+(?<dist>\d+\.?\d*)",
                match =>
                {
                    var lon = AddParameter(double.Parse(match.Groups["lon"].Value, CultureInfo.InvariantCulture));
                    var lat = AddParameter(double.Parse(match.Groups["lat"].Value, CultureInfo.InvariantCulture));
                    var dist = AddParameter(double.Parse(match.Groups["dist"].Value, CultureInfo.InvariantCulture));
                    var op = MapODataOperatorToSql(match.Groups["op"].Value);

                    return $"ST_Distance(geometry::geography, ST_SetSRID(ST_MakePoint(@p{lon}, @p{lat}), 4326)::geography) {op} @p{dist}";
                },
                RegexOptions.IgnoreCase);
        }

        private void ProcessGeometryIntersectsFunctions()
        {
            _sql = Regex.Replace(
                _sql,
                @"geo\.intersects\s*\(\s*geometry\s*,\s*geography'(?<wkt>POLYGON\s*\([^)]+\)\s*)'?\s*\)",
                match =>
                {
                    var wktParam = AddParameter(match.Groups["wkt"].Value);
                    return $"ST_Intersects(geometry, ST_SetSRID(ST_GeomFromText(@p{wktParam}), 4326))";
                },
                RegexOptions.IgnoreCase);

            _sql = Regex.Replace(
                _sql,
                @"geo\.intersects\s*\(\s*geometry\s*,\s*geography'(?<wkt>POINT\s*\([^)]+\))'?\s*\)",
                match =>
                {
                    var wktParam = AddParameter(match.Groups["wkt"].Value);
                    return $"ST_Intersects(geometry, ST_SetSRID(ST_GeomFromText(@p{wktParam}), 4326))";
                },
                RegexOptions.IgnoreCase);
        }

        private void ProcessStringFunctions()
        {
            ProcessStringFunction("contains", "%{0}%");
            ProcessStringFunction("startswith", "{0}%");
            ProcessStringFunction("endswith", "%{0}");
        }

        private void ProcessStringFunction(string functionName, string likePattern)
        {
            _sql = Regex.Replace(
                _sql,
                $@"{functionName}\(\s*(?<field>\w+)\s*,\s*'(?<value>[^']*)'\s*\)",
                match =>
                {
                    var field = match.Groups["field"].Value;
                    var value = match.Groups["value"].Value;
                    var fieldSql = MapODataField(field);
                    var valueParam = AddParameter(string.Format(likePattern, value));
                    return $"{fieldSql} LIKE @p{valueParam}";
                },
                RegexOptions.IgnoreCase);
        }

        private void ProcessOperators()
        {
            _sql = _sql
                .Replace(" eq ", " = ", StringComparison.OrdinalIgnoreCase)
                .Replace(" ne ", " <> ", StringComparison.OrdinalIgnoreCase)
                .Replace(" gt ", " > ", StringComparison.OrdinalIgnoreCase)
                .Replace(" lt ", " < ", StringComparison.OrdinalIgnoreCase)
                .Replace(" ge ", " >= ", StringComparison.OrdinalIgnoreCase)
                .Replace(" le ", " <= ", StringComparison.OrdinalIgnoreCase)
                .Replace(" and ", " AND ", StringComparison.OrdinalIgnoreCase)
                .Replace(" or ", " OR ", StringComparison.OrdinalIgnoreCase);
        }

        private void ProcessFieldReferences()
        {
            _sql = Regex.Replace(
                _sql,
                @"\b(?<field>\w+)\s*(?<op>=|<>|>|<|>=|<=)\s*(?<value>('([^']*)')|(-?\d+(?:\.\d+)?)|true|false|null)",
                ProcessFieldReferenceMatch,
                RegexOptions.IgnoreCase);
        }

        private string ProcessDistanceMatch(Match match)
        {
            var field = match.Groups["field"].Value;
            var geometry = match.Groups["geometry"].Value;
            var op = match.Groups["op"].Value;
            var distance = match.Groups["distance"].Value;

            var fieldSql = MapODataField(field);
            var sqlOp = MapODataOperatorToSql(op);
            var geometryParam = AddParameter(geometry);
            var distanceParam = AddParameter(double.Parse(distance, CultureInfo.InvariantCulture));

            return $"ST_Distance({fieldSql}::geography, ST_GeomFromText(@p{geometryParam})::geography) {sqlOp} @p{distanceParam}";
        }

        private string ProcessIntersectsMatch(Match match)
        {
            var field = match.Groups["field"].Value;
            var geometry = match.Groups["geometry"].Value;
            var fieldSql = MapODataField(field);
            var geometryParam = AddParameter(geometry);

            return $"ST_Intersects({fieldSql}, ST_GeomFromText(@p{geometryParam}))";
        }

        private string ProcessFieldReferenceMatch(Match match)
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
                return op == "<>" ? $"{fieldSql} IS NOT NULL" : $"{fieldSql} IS NULL";
            }

            var valueParam = AddParameter(ExtractValue(value, isCoreField));

            if (valueLower is "true" or "false")
            {
                var castedField = isCoreField ? fieldSql : $"({fieldSql})::boolean";
                return $"{castedField} {op} @p{valueParam}";
            }

            if (IsNumericValue(value))
            {
                var castedField = isCoreField ? fieldSql : $"({fieldSql})::double precision";
                return $"{castedField} {op} @p{valueParam}";
            }

            return $"{fieldSql} {op} @p{valueParam}";
        }

        private static object? ExtractValue(string value, bool isCoreField)
        {
            var valueLower = value.ToLowerInvariant();

            if (valueLower is "true" or "false")
            {
                return bool.Parse(valueLower);
            }

            if (value.StartsWith('\'') && value.EndsWith('\''))
            {
                return value.Substring(1, value.Length - 2);
            }

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numValue))
            {
                return numValue;
            }

            return value;
        }

        private static bool IsNumericValue(string value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }

        private int AddParameter(object? value)
        {
            var index = _paramIndex++;
            _parameters.Add(value);
            return index;
        }

        private static string MapODataField(string field)
        {
            var fieldName = field.Trim();
            var fieldLower = fieldName.ToLowerInvariant();

            return fieldLower switch
            {
                "objectid" => "objectid",
                "layerid" => "layer_id",
                "geometry" => "geometry",
                _ => $"attributes->>'{fieldName}'"
            };
        }

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
    }

    private sealed class SpatialFilterExtractor
    {
        private readonly string _filter;

        public SpatialFilterExtractor(string filter)
        {
            _filter = filter;
        }

        public bool TryExtract(out SpatialFilter? spatialFilter, out string? nonSpatialFilter, out string? error)
        {
            spatialFilter = null;
            nonSpatialFilter = _filter;
            error = null;

            if (TryParseODataSpatialFilter(_filter, out var parsedSpatialFilter, out error))
            {
                spatialFilter = parsedSpatialFilter;
                nonSpatialFilter = null;
                return true;
            }

            // Try splitting on AND and check each part
            var parts = Regex.Split(_filter, @"\s+and\s+", RegexOptions.IgnoreCase);
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

            if (_filter.Contains("geo.", StringComparison.OrdinalIgnoreCase))
            {
                error ??= "Unsupported spatial filter format.";
            }

            return false;
        }

        private static bool TryParseODataSpatialFilter(string filter, out SpatialFilter spatialFilter, out string? error)
        {
            spatialFilter = default;
            error = null;

            var intersectsMatch = Regex.Match(filter,
                @"^geo\.intersects\(\s*(?<field>\w+)\s*,\s*geography'(?<wkt>[^']+)'\s*\)\s*$",
                RegexOptions.IgnoreCase);

            if (intersectsMatch.Success)
            {
                return ProcessIntersectsFilter(intersectsMatch, out spatialFilter, out error);
            }

            var distanceMatch = Regex.Match(filter,
                @"^geo\.distance\(\s*(?<field>\w+)\s*,\s*geography'(?<wkt>[^']+)'\s*\)\s*(?<op>lt|le|gt|ge|eq|ne)\s*(?<distance>-?\d+(?:\.\d+)?)\s*$",
                RegexOptions.IgnoreCase);

            if (distanceMatch.Success)
            {
                return ProcessDistanceFilter(distanceMatch, out spatialFilter, out error);
            }

            if (filter.Contains("geo.", StringComparison.OrdinalIgnoreCase))
            {
                error = "Unsupported spatial filter format.";
            }

            return false;
        }

        private static bool ProcessIntersectsFilter(Match match, out SpatialFilter spatialFilter, out string? error)
        {
            spatialFilter = default;
            error = null;

            var field = match.Groups["field"].Value;
            if (!field.Equals("geometry", StringComparison.OrdinalIgnoreCase))
            {
                error = "Spatial filters are only supported on Geometry.";
                return false;
            }

            if (!TryCreateWkbFromWkt(match.Groups["wkt"].Value, out var geometryWkb, out error))
            {
                return false;
            }

            spatialFilter = SpatialFilter.Create(geometryWkb, SpatialRelationship.Intersects, 4326);
            return true;
        }

        private static bool ProcessDistanceFilter(Match match, out SpatialFilter spatialFilter, out string? error)
        {
            spatialFilter = default;
            error = null;

            var field = match.Groups["field"].Value;
            if (!field.Equals("geometry", StringComparison.OrdinalIgnoreCase))
            {
                error = "Spatial filters are only supported on Geometry.";
                return false;
            }

            if (!TryCreateWkbFromWkt(match.Groups["wkt"].Value, out var geometryWkb, out error))
            {
                return false;
            }

            if (!double.TryParse(match.Groups["distance"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var distanceValue) ||
                distanceValue <= 0)
            {
                error = "Distance must be a positive number.";
                return false;
            }

            var op = match.Groups["op"].Value.ToLowerInvariant();
            var withinDistance = op is "lt" or "le" or "eq";

            spatialFilter = SpatialFilter.CreateDistanceFilter(
                geometryWkb,
                distanceValue,
                DistanceUnit.Meters,
                withinDistance,
                4326);

            return true;
        }

        private static bool TryCreateWkbFromWkt(string wkt, out byte[] geometryWkb, out string? error)
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

                var writer = new NetTopologySuite.IO.WKBWriter();
                geometryWkb = writer.Write(geometry);
                return true;
            }
            catch
            {
                error = "Invalid spatial filter geometry.";
                return false;
            }
        }
    }
}
