// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Geometries;
using Honua.Infrastructure.Validation;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Infrastructure.Filtering;

internal sealed class Cql2FilterProcessor(
    IFilterExpressionService filterExpressionService,
    ICrsRegistry crsRegistry)
{
    private const string FilterLangCql2Text = "cql2-text";
    private const string FilterLangCql2Json = "cql2-json";

    private readonly IFilterExpressionService _filterExpressionService = filterExpressionService
        ?? throw new ArgumentNullException(nameof(filterExpressionService));
    private readonly ICrsRegistry _crsRegistry = crsRegistry
        ?? throw new ArgumentNullException(nameof(crsRegistry));

    public sealed class ProcessingResult
    {
        public bool IsSuccess { get; init; }
        public string? ErrorMessage { get; init; }
        public SqlFragment? SqlFilter { get; init; }
        public FilterExpression? Expression { get; init; }

        public static ProcessingResult Success(SqlFragment? sqlFilter, FilterExpression? expression = null)
            => new()
            {
                IsSuccess = true,
                SqlFilter = sqlFilter,
                Expression = expression
            };

        public static ProcessingResult Failure(string errorMessage)
            => new()
            {
                IsSuccess = false,
                ErrorMessage = errorMessage
            };
    }

    public async Task<ProcessingResult> ProcessFilterAsync(
        MetadataV2Resource resource,
        JsonElement? filter,
        string? filterLang,
        string? filterCrs,
        bool defaultFilterLangIsText,
        CancellationToken cancellationToken,
        string? collectionId = null)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var hasFilter = filter.HasValue;
        var hasFilterLang = !string.IsNullOrWhiteSpace(filterLang);
        var hasFilterCrs = !string.IsNullOrWhiteSpace(filterCrs);
        if (!hasFilter && !hasFilterLang && !hasFilterCrs)
        {
            return ProcessingResult.Success(null);
        }

        if (!hasFilter)
        {
            return ProcessingResult.Failure("filter requires a filter expression.");
        }

        var filterLanguage = ResolveFilterLanguage(filterLang, filter, defaultFilterLangIsText);
        if (filterLanguage is null)
        {
            return ProcessingResult.Failure("Invalid filter-lang parameter.");
        }

        var filterElement = filter.GetValueOrDefault();
        var filterText = filterLanguage.Value == FilterLanguage.Cql2Json
            ? filterElement.GetRawText()
            : filterElement.ValueKind == JsonValueKind.String
                ? filterElement.GetString()
                : null;

        if (filterLanguage.Value == FilterLanguage.Cql2Text && filterElement.ValueKind != JsonValueKind.String)
        {
            return ProcessingResult.Failure("filter must be a string when filter-lang is cql2-text.");
        }

        if (filterLanguage.Value == FilterLanguage.Cql2Json &&
            filterElement.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
        {
            return ProcessingResult.Failure("filter must be a JSON object when filter-lang is cql2-json.");
        }

        var parseResult = _filterExpressionService.Parse(filterLanguage.Value, filterText);
        if (!parseResult.IsSuccess)
        {
            return ProcessingResult.Failure(parseResult.ErrorMessage ?? "Invalid filter expression.");
        }

        var parsedExpression = parseResult.Expression;
        if (parsedExpression is null)
        {
            return ProcessingResult.Failure("Invalid filter expression.");
        }

        // STAC Item Search Filter Extension exposes the STAC core queryables (id, collection,
        // datetime) and nests item attributes under a "properties." prefix, none of which are
        // physical storage fields. Rewrite those references to the resource's storage columns
        // before translation so a spec-compliant CQL2 filter (e.g. `collection = '0'`,
        // `id = '1'`, `datetime < TIMESTAMP(...)`, `properties.count > 0`) resolves to 200
        // instead of being rejected as an unknown field (honua-server STAC filter conformance).
        parsedExpression = RewriteStacCoreQueryables(parsedExpression, resource, collectionId);

        if (hasFilterCrs)
        {
            var filterCrsDefinition = await _crsRegistry.ResolveAsync(filterCrs, cancellationToken).ConfigureAwait(false);
            if (!filterCrsDefinition.HasValue)
            {
                return ProcessingResult.Failure($"Unsupported filter-crs '{filterCrs}'.");
            }

            parsedExpression = ApplyFilterCrs(parsedExpression, filterCrsDefinition.Value);
            parsedExpression = NormalizeFilterAxisOrder(parsedExpression, filterCrsDefinition.Value.AxisOrder);
        }

        // Not converted to `.Where(...)`: the predicate is an awaited async lookup, and the
        // first unsupported SRID must short-circuit the enclosing method with a specific
        // error message, which a synchronous LINQ filter cannot express.
        foreach (var explicitGeometrySrid in FilterGeometryCrsValidator.GetExplicitGeometrySrids(parsedExpression))
        {
            switch (await _crsRegistry.IsSridSupportedAsync(explicitGeometrySrid, cancellationToken).ConfigureAwait(false))
            {
                case false:
                    return ProcessingResult.Failure($"Unsupported explicit geometry CRS 'EPSG:{explicitGeometrySrid}'.");
            }
        }

        try
        {
            parsedExpression = _filterExpressionService.Normalize(parsedExpression, resource);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return ProcessingResult.Failure(ex.Message);
        }

        var translationResult = _filterExpressionService.Translate(parsedExpression, resource);
        return translationResult.IsSuccess
            ? ProcessingResult.Success(translationResult.SqlFilter, parsedExpression)
            : ProcessingResult.Failure(translationResult.ErrorMessage ?? "Invalid filter expression.");
    }

    /// <summary>
    /// Rewrites STAC core queryable property references (<c>id</c>, <c>collection</c>,
    /// <c>datetime</c>) and the STAC <c>properties.</c> attribute prefix in a parsed CQL2
    /// expression to the resource's storage fields, so the shared filter translator can resolve
    /// them. <c>collection</c> comparisons are evaluated against the target collection id and
    /// folded to an always-true/always-false predicate (the search is already scoped to the
    /// target collection). This is STAC-only: <see cref="ProcessFilterAsync"/> is consumed
    /// exclusively by the STAC search endpoints.
    /// </summary>
    internal static FilterExpression RewriteStacCoreQueryables(
        FilterExpression expression,
        MetadataV2Resource resource,
        string? collectionId)
    {
        var idField = resource.FindPrimaryIdField();
        var idFieldName = idField?.Name ?? "objectid";
        var idFieldIsNumeric = idField?.Type is MetadataV2FieldType.Integer
            or MetadataV2FieldType.BigInteger
            or MetadataV2FieldType.Double
            or MetadataV2FieldType.Float
            or null; // default objectid is integer-keyed
        var temporalFieldName = ResolveTemporalFieldName(resource);
        return RewriteNode(expression, idFieldName, idFieldIsNumeric, temporalFieldName, collectionId);
    }

    private static string? ResolveTemporalFieldName(MetadataV2Resource resource)
    {
        var configured = resource.ReadTemporalFields().StartTimeField;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        foreach (var candidate in TemporalFieldDefaults.TemporalFallbackFieldNames)
        {
            foreach (var field in resource.SchemaFields.Where(field =>
                string.Equals(field.Name, candidate, StringComparison.OrdinalIgnoreCase) &&
                field.Type is MetadataV2FieldType.Date
                    or MetadataV2FieldType.DateTime
                    or MetadataV2FieldType.Time))
            {
                return field.Name;
            }
        }

        return null;
    }

    private static FilterExpression RewriteNode(
        FilterExpression expression,
        string idFieldName,
        bool idFieldIsNumeric,
        string? temporalFieldName,
        string? collectionId)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                // Fold a `collection <op> '<literal>'` comparison against the known target
                // collection id, since `collection` is not a per-row storage column.
                if (TryFoldCollectionComparison(binary, collectionId, out var folded))
                {
                    return folded;
                }

                // STAC item `id` is always a string, but it maps to a numeric storage column
                // (objectid) by default; coerce the comparison's string literal to a number so
                // `id = '1'` resolves against the integer key instead of failing type validation.
                if (idFieldIsNumeric && TryCoerceIdComparison(binary, idFieldName, out var coerced))
                {
                    return coerced;
                }

                return binary with
                {
                    Left = RewriteNode(binary.Left, idFieldName, idFieldIsNumeric, temporalFieldName, collectionId),
                    Right = RewriteNode(binary.Right, idFieldName, idFieldIsNumeric, temporalFieldName, collectionId)
                };
            case UnaryExpression unary:
                return unary with { Operand = RewriteNode(unary.Operand, idFieldName, idFieldIsNumeric, temporalFieldName, collectionId) };
            case SpatialPredicate spatial:
                return spatial with
                {
                    Left = RewriteNode(spatial.Left, idFieldName, idFieldIsNumeric, temporalFieldName, collectionId),
                    Right = RewriteNode(spatial.Right, idFieldName, idFieldIsNumeric, temporalFieldName, collectionId)
                };
            case SpatialDistancePredicate spatialDistance:
                return spatialDistance with
                {
                    Left = RewriteNode(spatialDistance.Left, idFieldName, idFieldIsNumeric, temporalFieldName, collectionId),
                    Right = RewriteNode(spatialDistance.Right, idFieldName, idFieldIsNumeric, temporalFieldName, collectionId),
                    Distance = RewriteNode(spatialDistance.Distance, idFieldName, idFieldIsNumeric, temporalFieldName, collectionId)
                };
            case TemporalPredicate temporal:
                return temporal with
                {
                    Left = RewriteNode(temporal.Left, idFieldName, idFieldIsNumeric, temporalFieldName, collectionId),
                    Right = RewriteNode(temporal.Right, idFieldName, idFieldIsNumeric, temporalFieldName, collectionId)
                };
            case ArrayPredicate array:
                return array with
                {
                    Left = RewriteNode(array.Left, idFieldName, idFieldIsNumeric, temporalFieldName, collectionId),
                    Right = RewriteNode(array.Right, idFieldName, idFieldIsNumeric, temporalFieldName, collectionId)
                };
            case FunctionCall functionCall:
                return functionCall with
                {
                    Arguments = functionCall.Arguments
                        .Select(argument => RewriteNode(argument, idFieldName, idFieldIsNumeric, temporalFieldName, collectionId))
                        .ToArray()
                };
            case ArrayLiteral arrayLiteral:
                return arrayLiteral with
                {
                    Elements = arrayLiteral.Elements
                        .Select(element => RewriteNode(element, idFieldName, idFieldIsNumeric, temporalFieldName, collectionId))
                        .ToArray()
                };
            case ValueList valueList:
                return valueList with
                {
                    Values = valueList.Values
                        .Select(value => RewriteNode(value, idFieldName, idFieldIsNumeric, temporalFieldName, collectionId))
                        .ToArray()
                };
            case PropertyReference property:
                return new PropertyReference(MapStacProperty(property.PropertyName, idFieldName, temporalFieldName));
            default:
                return expression;
        }
    }

    private static string MapStacProperty(string propertyName, string idFieldName, string? temporalFieldName)
    {
        if (string.Equals(propertyName, "id", StringComparison.OrdinalIgnoreCase))
        {
            return idFieldName;
        }

        if (string.Equals(propertyName, "datetime", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(temporalFieldName))
        {
            return temporalFieldName;
        }

        // STAC nests item attributes under "properties."; the storage column is the bare name.
        if (propertyName.StartsWith("properties.", StringComparison.OrdinalIgnoreCase))
        {
            return propertyName["properties.".Length..];
        }

        return propertyName;
    }

    private static bool TryCoerceIdComparison(BinaryExpression binary, string idFieldName, out FilterExpression coerced)
    {
        coerced = binary;

        var isComparison = binary.Operator is BinaryOperator.Equal
            or BinaryOperator.NotEqual
            or BinaryOperator.LessThan
            or BinaryOperator.LessThanOrEqual
            or BinaryOperator.GreaterThan
            or BinaryOperator.GreaterThanOrEqual;
        if (!isComparison)
        {
            return false;
        }

        if (IsIdReference(binary.Left) && binary.Right is Literal rightLiteral)
        {
            if (!TryCoerceLiteralToNumber(rightLiteral, out var numericRight))
            {
                return false;
            }

            coerced = binary with { Left = new PropertyReference(idFieldName), Right = numericRight };
            return true;
        }

        if (IsIdReference(binary.Right) && binary.Left is Literal leftLiteral)
        {
            if (!TryCoerceLiteralToNumber(leftLiteral, out var numericLeft))
            {
                return false;
            }

            coerced = binary with { Left = numericLeft, Right = new PropertyReference(idFieldName) };
            return true;
        }

        return false;
    }

    private static bool TryCoerceLiteralToNumber(Literal literal, out Literal numeric)
    {
        numeric = literal;
        if (literal.Type == LiteralType.Number)
        {
            return true;
        }

        if (literal.Value is string text &&
            long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            numeric = new Literal(parsed, LiteralType.Number);
            return true;
        }

        return false;
    }

    private static bool IsIdReference(FilterExpression expression)
        => expression is PropertyReference property
           && string.Equals(property.PropertyName, "id", StringComparison.OrdinalIgnoreCase);

    private static bool TryFoldCollectionComparison(
        BinaryExpression binary,
        string? collectionId,
        out FilterExpression folded)
    {
        folded = binary;

        // Only fold direct comparison operators that pair the `collection` queryable with a
        // string literal. Logical AND/OR keep recursing through their operands.
        var isComparison = binary.Operator is BinaryOperator.Equal
            or BinaryOperator.NotEqual
            or BinaryOperator.LessThan
            or BinaryOperator.LessThanOrEqual
            or BinaryOperator.GreaterThan
            or BinaryOperator.GreaterThanOrEqual;
        if (!isComparison)
        {
            return false;
        }

        string? literalValue = null;
        if (IsCollectionReference(binary.Left) && binary.Right is Literal { Value: { } rightValue })
        {
            literalValue = Convert.ToString(rightValue, System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (IsCollectionReference(binary.Right) && binary.Left is Literal { Value: { } leftValue })
        {
            literalValue = Convert.ToString(leftValue, System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            return false;
        }

        var comparison = string.CompareOrdinal(collectionId ?? string.Empty, literalValue ?? string.Empty);
        var result = binary.Operator switch
        {
            BinaryOperator.Equal => comparison == 0,
            BinaryOperator.NotEqual => comparison != 0,
            BinaryOperator.LessThan => comparison < 0,
            BinaryOperator.LessThanOrEqual => comparison <= 0,
            BinaryOperator.GreaterThan => comparison > 0,
            BinaryOperator.GreaterThanOrEqual => comparison >= 0,
            _ => true
        };

        // Fold to a constant predicate the translator can emit (1=1 / 1=0).
        folded = new BinaryExpression(
            new Literal(1L, LiteralType.Number),
            BinaryOperator.Equal,
            new Literal(result ? 1L : 0L, LiteralType.Number));
        return true;
    }

    private static bool IsCollectionReference(FilterExpression expression)
        => expression is PropertyReference property
           && string.Equals(property.PropertyName, "collection", StringComparison.OrdinalIgnoreCase);

    internal static FilterLanguage? ResolveFilterLanguage(
        string? filterLang,
        JsonElement? filter,
        bool defaultFilterLangIsText)
    {
        if (!string.IsNullOrWhiteSpace(filterLang))
        {
            return filterLang.Trim().ToLowerInvariant() switch
            {
                FilterLangCql2Text => FilterLanguage.Cql2Text,
                FilterLangCql2Json => FilterLanguage.Cql2Json,
                _ => null
            };
        }

        if (!filter.HasValue)
        {
            return null;
        }

        return defaultFilterLangIsText || filter.Value.ValueKind == JsonValueKind.String
            ? FilterLanguage.Cql2Text
            : FilterLanguage.Cql2Json;
    }

    internal static FilterExpression NormalizeFilterAxisOrder(
        FilterExpression filterExpression,
        AxisOrder axisOrder)
    {
        if (axisOrder == AxisOrder.EastNorth)
        {
            return filterExpression;
        }

        return SwapAxisOrder(filterExpression, preserveExplicitGeometryCrs: true);
    }

    internal static FilterExpression ApplyFilterCrs(
        FilterExpression filterExpression,
        CrsDefinition crsDefinition)
    {
        return filterExpression switch
        {
            GeometryLiteral geometry => ApplyGeometryCrs(geometry, crsDefinition),
            BinaryExpression binary => binary with
            {
                Left = ApplyFilterCrs(binary.Left, crsDefinition),
                Right = ApplyFilterCrs(binary.Right, crsDefinition)
            },
            UnaryExpression unary => unary with { Operand = ApplyFilterCrs(unary.Operand, crsDefinition) },
            SpatialPredicate spatial => spatial with
            {
                Left = ApplyFilterCrs(spatial.Left, crsDefinition),
                Right = ApplyFilterCrs(spatial.Right, crsDefinition)
            },
            SpatialDistancePredicate spatialDistance => spatialDistance with
            {
                Left = ApplyFilterCrs(spatialDistance.Left, crsDefinition),
                Right = ApplyFilterCrs(spatialDistance.Right, crsDefinition),
                Distance = ApplyFilterCrs(spatialDistance.Distance, crsDefinition)
            },
            TemporalPredicate temporal => temporal with
            {
                Left = ApplyFilterCrs(temporal.Left, crsDefinition),
                Right = ApplyFilterCrs(temporal.Right, crsDefinition)
            },
            ArrayPredicate array => array with
            {
                Left = ApplyFilterCrs(array.Left, crsDefinition),
                Right = ApplyFilterCrs(array.Right, crsDefinition)
            },
            FunctionCall functionCall => functionCall with
            {
                Arguments = functionCall.Arguments.Select(argument => ApplyFilterCrs(argument, crsDefinition)).ToArray()
            },
            ArrayLiteral arrayLiteral => arrayLiteral with
            {
                Elements = arrayLiteral.Elements.Select(element => ApplyFilterCrs(element, crsDefinition)).ToArray()
            },
            ValueList valueList => valueList with
            {
                Values = valueList.Values.Select(value => ApplyFilterCrs(value, crsDefinition)).ToArray()
            },
            _ => filterExpression
        };
    }

    private static GeometryLiteral ApplyGeometryCrs(
        GeometryLiteral geometry,
        CrsDefinition crsDefinition)
    {
        if (FilterGeometryCrsValidator.HasExplicitCrs(geometry))
        {
            return geometry;
        }

        return geometry with { Srid = crsDefinition.Srid };
    }

    private static FilterExpression SwapAxisOrder(
        FilterExpression filterExpression,
        bool preserveExplicitGeometryCrs)
    {
        return filterExpression switch
        {
            GeometryLiteral geometry => preserveExplicitGeometryCrs && FilterGeometryCrsValidator.HasExplicitCrs(geometry)
                ? geometry
                : SwapGeometryLiteral(geometry),
            BinaryExpression binary => binary with
            {
                Left = SwapAxisOrder(binary.Left, preserveExplicitGeometryCrs),
                Right = SwapAxisOrder(binary.Right, preserveExplicitGeometryCrs)
            },
            UnaryExpression unary => unary with { Operand = SwapAxisOrder(unary.Operand, preserveExplicitGeometryCrs) },
            SpatialPredicate spatial => spatial with
            {
                Left = SwapAxisOrder(spatial.Left, preserveExplicitGeometryCrs),
                Right = SwapAxisOrder(spatial.Right, preserveExplicitGeometryCrs)
            },
            SpatialDistancePredicate spatialDistance => spatialDistance with
            {
                Left = SwapAxisOrder(spatialDistance.Left, preserveExplicitGeometryCrs),
                Right = SwapAxisOrder(spatialDistance.Right, preserveExplicitGeometryCrs),
                Distance = SwapAxisOrder(spatialDistance.Distance, preserveExplicitGeometryCrs)
            },
            TemporalPredicate temporal => temporal with
            {
                Left = SwapAxisOrder(temporal.Left, preserveExplicitGeometryCrs),
                Right = SwapAxisOrder(temporal.Right, preserveExplicitGeometryCrs)
            },
            ArrayPredicate array => array with
            {
                Left = SwapAxisOrder(array.Left, preserveExplicitGeometryCrs),
                Right = SwapAxisOrder(array.Right, preserveExplicitGeometryCrs)
            },
            FunctionCall functionCall => functionCall with
            {
                Arguments = functionCall.Arguments
                    .Select(argument => SwapAxisOrder(argument, preserveExplicitGeometryCrs))
                    .ToArray()
            },
            ArrayLiteral arrayLiteral => arrayLiteral with
            {
                Elements = arrayLiteral.Elements
                    .Select(element => SwapAxisOrder(element, preserveExplicitGeometryCrs))
                    .ToArray()
            },
            ValueList valueList => valueList with
            {
                Values = valueList.Values
                    .Select(value => SwapAxisOrder(value, preserveExplicitGeometryCrs))
                    .ToArray()
            },
            _ => filterExpression
        };
    }

    private static GeometryLiteral SwapGeometryLiteral(GeometryLiteral geometry)
    {
        if (geometry.Wkb.Length == 0)
        {
            return geometry;
        }

        try
        {
            var reader = new WKBReader();
            var parsed = reader.Read(geometry.Wkb);
            if (parsed == null)
            {
                return geometry;
            }

            var clone = (Geometry)parsed.Copy();
            clone.Apply(new AxisSwapCoordinateFilter());
            clone.GeometryChanged();

            var (hasZ, hasM) = Honua.Infrastructure.Services.GeometryService.DetectZMFromGeometry(clone);
            var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: hasZ, emitM: hasM);
            var wkb = writer.Write(clone);

            return geometry with { Wkb = wkb };
        }
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            // Intentional: axis-swap is a best-effort CRS remapping step; a malformed or
            // unsupported WKB literal must fall back to the original geometry unmodified
            // rather than fail filter translation. This is a static helper with no logger.
            return geometry;
        }
    }
}
