// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Core.Features.SensorThings.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Queries.Filters;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.SensorThings;

internal sealed partial class PostgresObservationStore
{
    public SqlFragment TranslateFilter(string entitySet, FilterExpression expression)
    {
        var context = FilterTranslationContext.FromColumns([], "id", "location", 4326, GeometryType.GeometryCollection, entitySet, true);
        return new SensingFilterVisitor(this).Translate(expression, context);
    }

    public SqlFragment TranslateOrderExpression(string entitySet, FilterExpression expression)
    {
        if (expression is PropertyReference { PropertyName: "unitOfMeasurement" or "properties" or "parameters" or "location" or "feature" or "observedArea" or "resultQuality" })
            throw new SensorThingsValidationException("$orderby requires a primitive property or expression.");
        var context = FilterTranslationContext.FromColumns([], "id", "location", 4326, GeometryType.GeometryCollection, entitySet, true);
        return new SensingFilterVisitor(this, expression is PropertyReference).Translate(expression, context);
    }

    // The shared Postgres visitor owns the full arithmetic/function/temporal/spatial
    // translation. This adapter only resolves sensing properties and associations.
    private sealed class SensingFilterVisitor(PostgresObservationStore store, bool rawJsonProperties = false) : PostgresSqlFilterTranslator
    {
        private string _alias = "d";
        private int _scopeIndex;
        private readonly Dictionary<string, (string Set, string Alias, string Path)> _outerReferences = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _typedReferences = new(StringComparer.Ordinal);

        protected override string TranslateProperty(PropertyReference property, FilterTranslationContext context) =>
            _typedReferences.TryGetValue(property.PropertyName, out var typed) ? typed : _outerReferences.TryGetValue(property.PropertyName, out var outer)
                ? Resolve(outer.Set, outer.Path, outer.Alias, 0)
                : Resolve(context.ResourceName, property.PropertyName, _alias, 0);

        private static string JsonScalar(string json, LiteralType? type) => type switch
        {
            LiteralType.Text => $"CASE WHEN jsonb_typeof({json})='string' THEN {json} #>> '{{}}' END",
            LiteralType.Boolean => $"CASE WHEN jsonb_typeof({json})='boolean' THEN ({json} #>> '{{}}')::boolean END",
            _ => $"CASE WHEN jsonb_typeof({json})='number' THEN ({json} #>> '{{}}')::double precision END"
        };

        private string Resolve(string set, string name, string alias, int depth, bool rawResult = false)
        {
            if (depth > 10) throw new SensorThingsValidationException("Navigation filter exceeds maximum depth.");
            if (name is "id" or "@iot.id") return alias + ".id";
            if (set == "Datastreams" && name is "phenomenonTime" or "resultTime")
            {
                var start = name == "phenomenonTime" ? "o.phenomenon_time" : "o.result_time";
                var end = name == "phenomenonTime" ? "COALESCE(o.phenomenon_time_end,o.phenomenon_time)" : "o.result_time";
                return $"(SELECT (to_jsonb(min({start})) #>> '{{}}') || '/' || (to_jsonb(max({end})) #>> '{{}}') FROM {store.EntityTable("Observations")} o WHERE o.datastream_reference_id={alias}.id)";
            }
            if (set == "Datastreams" && name == "observedArea")
                return GeoJsonGeometrySql($"(({store.EntityJsonSql(set, alias)}) -> 'computed_observed_area')");
            var column = Columns(set).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (column is not null)
            {
                var sql = alias + "." + column.Column;
                if (rawResult && column.Type == NpgsqlDbType.Jsonb) return $"NULLIF({sql},'null'::jsonb)";
                if (column.Type == NpgsqlDbType.Jsonb && column.Name is not ("location" or "feature" or "observedArea")) return rawJsonProperties ? $"NULLIF({sql},'null'::jsonb)" : JsonScalar(sql, null);
                if (column.Name is "location" or "feature" or "observedArea")
                {
                    return GeoJsonGeometrySql(sql, column.Name is "location" or "feature" ? alias + ".encoding_type" : null);
                }
                return sql;
            }
            var jsonParts = name.Split('/');
            var jsonColumn = jsonParts.Length > 1 ? Columns(set).FirstOrDefault(column => column.Type == NpgsqlDbType.Jsonb && column.Name.Equals(jsonParts[0], StringComparison.OrdinalIgnoreCase)) : null;
            if (jsonColumn is not null)
            {
                var parts = jsonParts;
                if (parts.Any(p => p.Length == 0)) throw new SensorThingsValidationException("Invalid JSON property path.");
                var root = set == "Datastreams" && jsonColumn.Name == "observedArea"
                    ? $"(({store.EntityJsonSql(set, alias)}) -> 'computed_observed_area')"
                    : alias + "." + jsonColumn.Column;
                var json = root + " #> ARRAY[" + string.Join(",", parts.Skip(1).Select(p => "'" + p.Replace("'", "''", StringComparison.Ordinal) + "'")) + "]";
                return rawResult || rawJsonProperties ? $"NULLIF({json},'null'::jsonb)" : JsonScalar(json, null);
            }
            var slash = name.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0) throw new SensorThingsValidationException($"Unknown property '{name}' on {set}.");
            var navigation = name[..slash];
            if (!SensorThingsRelationships.For(set).TryGetValue(navigation, out var relationship)) throw new SensorThingsValidationException("Unknown navigation filter.");
            var targetAlias = "n" + depth.ToString(CultureInfo.InvariantCulture);
            var expression = Resolve(relationship.Target, name[(slash + 1)..], targetAlias, depth + 1, rawResult);
            var foreign = ForeignKeys(set).FirstOrDefault(f => f.Navigation == navigation);
            if (foreign.Column is not null)
            {
                var reference = set == "Observations" ? navigation == "Datastream" ? "datastream_reference_id" : "feature_of_interest_reference_id" : foreign.Column;
                return $"(SELECT {expression} FROM {store.EntityTable(relationship.Target)} {targetAlias} WHERE {targetAlias}.id={alias}.{reference})";
            }
            throw new SensorThingsValidationException("Collection navigation must occur inside a predicate.");
        }

        private static IEnumerable<PropertyReference> Properties(FilterExpression expression, int depth = 0)
        {
            if (depth > MaxExpressionDepth) throw new SensorThingsValidationException("Filter expression exceeds the maximum depth.");
            if (expression is PropertyReference property) { yield return property; yield break; }
            var children = expression switch
            {
                BinaryExpression binary => new[] { binary.Left, binary.Right },
                UnaryExpression unary => new[] { unary.Operand },
                FunctionCall function => function.Arguments,
                SpatialPredicate spatial => new[] { spatial.Left, spatial.Right },
                SpatialDistancePredicate distance => new[] { distance.Left, distance.Right, distance.Distance },
                TemporalPredicate temporal => new[] { temporal.Left, temporal.Right },
                ArrayPredicate array => new[] { array.Left, array.Right },
                ValueList list => list.Values,
                _ => Array.Empty<FilterExpression>()
            };
            foreach (var child in children) foreach (var reference in Properties(child, depth + 1)) yield return reference;
        }

        private string? WrapCollectionPredicate(FilterExpression expression, FilterTranslationContext context)
        {
            string? navigation = null;
            foreach (var property in Properties(expression))
            {
                if (_outerReferences.ContainsKey(property.PropertyName)) continue;
                var parts = property.PropertyName.Split('/');
                var set = context.ResourceName;
                foreach (var part in parts.Take(parts.Length - 1))
                {
                    if (!SensorThingsRelationships.For(set).TryGetValue(part, out var relation)) break;
                    if (relation.Many) { navigation = parts[0]; break; }
                    set = relation.Target;
                }
                if (navigation is not null) break;
            }
            if (navigation is null) return null;
            var target = SensorThingsRelationships.For(context.ResourceName)[navigation].Target;
            var prefix = navigation + "/";
            var outerAlias = _alias;
            var innerAlias = "q" + (++_scopeIndex).ToString(CultureInfo.InvariantCulture);
            var predicate = store.RelationshipPredicate(context.ResourceName, navigation, innerAlias, outerAlias + ".id");
            var rewritten = Rewrite(expression, property =>
            {
                if (_outerReferences.ContainsKey(property.PropertyName)) return property;
                if (property.PropertyName.StartsWith(prefix, StringComparison.Ordinal)) return new PropertyReference(property.PropertyName[prefix.Length..]);
                var symbol = "__sta_outer_" + _outerReferences.Count.ToString(CultureInfo.InvariantCulture);
                _outerReferences[symbol] = (context.ResourceName, outerAlias, property.PropertyName);
                return new PropertyReference(symbol);
            });
            _alias = innerAlias;
            try
            {
                var innerContext = FilterTranslationContext.FromColumns([], "id", "location", 4326, GeometryType.GeometryCollection, target, true);
                return $"EXISTS (SELECT 1 FROM {store.EntityTable(target)} {innerAlias} WHERE {predicate} AND ({TranslateExpression(rewritten, innerContext)}))";
            }
            finally { _alias = outerAlias; }
        }

        private static FilterExpression Rewrite(FilterExpression expression, Func<PropertyReference, PropertyReference> property) => expression switch
        {
            PropertyReference reference => property(reference),
            BinaryExpression binary => binary with { Left = Rewrite(binary.Left, property), Right = Rewrite(binary.Right, property) },
            UnaryExpression unary => unary with { Operand = Rewrite(unary.Operand, property) },
            FunctionCall function => function with { Arguments = function.Arguments.Select(argument => Rewrite(argument, property)).ToArray() },
            SpatialPredicate spatial => spatial with { Left = Rewrite(spatial.Left, property), Right = Rewrite(spatial.Right, property) },
            SpatialDistancePredicate distance => distance with { Left = Rewrite(distance.Left, property), Right = Rewrite(distance.Right, property), Distance = Rewrite(distance.Distance, property) },
            TemporalPredicate temporal => temporal with { Left = Rewrite(temporal.Left, property), Right = Rewrite(temporal.Right, property) },
            ArrayPredicate array => array with { Left = Rewrite(array.Left, property), Right = Rewrite(array.Right, property) },
            ValueList list => list with { Values = list.Values.Select(value => Rewrite(value, property)).ToArray() },
            _ => expression
        };

        private static bool IsJsonProperty(string set, string path)
        {
            var slash = path.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0 && SensorThingsRelationships.For(set).TryGetValue(path[..slash], out var relation)) return IsJsonProperty(relation.Target, path[(slash + 1)..]);
            var root = slash < 0 ? path : path[..slash];
            return Columns(set).Any(column => column.Type == NpgsqlDbType.Jsonb && column.Name.Equals(root, StringComparison.OrdinalIgnoreCase));
        }

        private static EntityColumn? LeafColumn(string set, string path)
        {
            var column = Columns(set).FirstOrDefault(column => column.Name.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (column is not null) return column;
            var slash = path.IndexOf('/', StringComparison.Ordinal);
            return slash > 0 && SensorThingsRelationships.For(set).TryGetValue(path[..slash], out var relation)
                ? LeafColumn(relation.Target, path[(slash + 1)..]) : null;
        }

        protected override string TranslateUnary(UnaryExpression unary, FilterTranslationContext context)
        {
            if (unary.Operator is UnaryOperator.IsNull or UnaryOperator.IsNotNull && WrapCollectionPredicate(unary, context) is { } collection) return collection;
            if (unary.Operator is UnaryOperator.IsNull or UnaryOperator.IsNotNull && unary.Operand is PropertyReference property
                && IsJsonProperty(context.ResourceName, property.PropertyName))
            {
                var value = Resolve(context.ResourceName, property.PropertyName, _alias, 0, true);
                return $"({value}) IS {(unary.Operator == UnaryOperator.IsNotNull ? "NOT " : string.Empty)}NULL";
            }
            return base.TranslateUnary(unary, context);
        }

        protected override string TranslateBinary(BinaryExpression binary, FilterTranslationContext context)
        {
            if (binary.Operator is not (BinaryOperator.And or BinaryOperator.Or or BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo or BinaryOperator.Div or BinaryOperator.Power)
                && WrapCollectionPredicate(binary, context) is { } collection) return collection;
            var property = binary.Left as PropertyReference ?? binary.Right as PropertyReference;
            var literal = binary.Right as Literal ?? binary.Left as Literal;
            if (property is not null && literal is not null)
            {
                var column = LeafColumn(context.ResourceName, property.PropertyName);
                if (column?.Type == NpgsqlDbType.TimestampTz && literal.Value is string text)
                {
                    if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)) throw new SensorThingsValidationException("Time filter requires an ISO 8601 instant.");
                    binary = binary.Left is Literal ? binary with { Left = new Literal(instant, LiteralType.DateTime) } : binary with { Right = new Literal(instant, LiteralType.DateTime) };
                }
                if (column?.Type == NpgsqlDbType.Text && literal.Type != LiteralType.Text && literal.Type != LiteralType.Null) throw new SensorThingsValidationException("Text property comparisons require a quoted string.");
                if (property.PropertyName is "id" or "@iot.id" && literal.Type != LiteralType.Number) throw new SensorThingsValidationException("Identifier comparisons require an integer.");
                if (IsJsonProperty(context.ResourceName, property.PropertyName) && literal.Type is LiteralType.Text or LiteralType.Boolean)
                {
                    var json = Resolve(context.ResourceName, property.PropertyName, _alias, 0, true);
                    var result = JsonScalar(json, literal.Type);
                    var bound = TranslateExpression(literal, context);
                    var op = binary.Operator switch { BinaryOperator.Equal => "=", BinaryOperator.NotEqual => "<>", BinaryOperator.LessThan => "<", BinaryOperator.LessThanOrEqual => "<=", BinaryOperator.GreaterThan => ">", BinaryOperator.GreaterThanOrEqual => ">=", _ => throw new SensorThingsValidationException("Invalid result comparison.") };
                    return binary.Left is PropertyReference ? $"({result} {op} {bound})" : $"({bound} {op} {result})";
                }
            }
            return base.TranslateBinary(binary, context);
        }

        protected override string TranslateFunction(FunctionCall function, FilterTranslationContext context)
        {
            var name = function.FunctionName.ToLowerInvariant();
            if (name is "contains" or "startswith" or "endswith" or "st_intersects" or "st_contains" or "st_within" or "st_equals" or "st_disjoint" or "st_touches" or "st_crosses" or "st_overlaps" or "st_relate"
                && WrapCollectionPredicate(function, context) is { } collection) return collection;
            if (name is "lower" or "upper" or "length" or "char_length" or "position" or "substring" or "substr" or "trim" or "ltrim" or "rtrim" or "concat" or "replace")
            {
                function = function with { Arguments = function.Arguments.Select((argument, index) =>
                    name is "substring" or "substr" && index > 0 ? argument : Rewrite(argument, property =>
                    {
                        if (!IsJsonProperty(context.ResourceName, property.PropertyName)) return property;
                        var symbol = "__sta_typed_" + _typedReferences.Count.ToString(CultureInfo.InvariantCulture);
                        _typedReferences[symbol] = JsonScalar(Resolve(context.ResourceName, property.PropertyName, _alias, 0, true), LiteralType.Text);
                        return new PropertyReference(symbol);
                    })).ToArray() };
            }
            return base.TranslateFunction(function, context);
        }

        protected override string TranslateSpatial(SpatialPredicate spatial, FilterTranslationContext context) =>
            WrapCollectionPredicate(spatial, context) ?? base.TranslateSpatial(spatial, context);

        protected override string TranslateSpatialDistance(SpatialDistancePredicate spatial, FilterTranslationContext context) =>
            WrapCollectionPredicate(spatial, context) ?? base.TranslateSpatialDistance(spatial, context);

        private static bool IsSpatialProperty(string set, string path)
        {
            var slash = path.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0 && SensorThingsRelationships.For(set).TryGetValue(path[..slash], out var relation)) return IsSpatialProperty(relation.Target, path[(slash + 1)..]);
            return path is "location" or "feature" or "observedArea";
        }

        protected override string TranslateGeometryExpression(FilterExpression expression, FilterTranslationContext context)
        {
            if (expression is not PropertyReference property) return base.TranslateGeometryExpression(expression, context);
            if (!IsSpatialProperty(context.ResourceName, property.PropertyName)) throw new SensorThingsValidationException("Spatial operations require a geometry property.");
            return Resolve(context.ResourceName, property.PropertyName, _alias, 0);
        }

        protected override string TranslateGeographyExpression(FilterExpression expression, FilterTranslationContext context) =>
            expression is PropertyReference ? $"({TranslateGeometryExpression(expression, context)})::geography" : base.TranslateGeographyExpression(expression, context);
    }
}
