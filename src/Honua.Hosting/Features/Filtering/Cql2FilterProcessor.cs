// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Exceptions;
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

        /// <summary>
        /// True when the failure is "this resource does not declare a property the
        /// filter references" rather than "this filter is invalid". A caller that
        /// applies one filter to several resources (STAC item search across
        /// collections) uses this to let the non-matching resource contribute no
        /// rows instead of failing the whole request (honua-server#3392).
        /// </summary>
        public bool IsUnknownField { get; init; }

        public static ProcessingResult Success(SqlFragment? sqlFilter, FilterExpression? expression = null)
            => new()
            {
                IsSuccess = true,
                SqlFilter = sqlFilter,
                Expression = expression
            };

        public static ProcessingResult Failure(string errorMessage, bool isUnknownField = false)
            => new()
            {
                IsSuccess = false,
                ErrorMessage = errorMessage,
                IsUnknownField = isUnknownField
            };
    }

    public async Task<ProcessingResult> ProcessFilterAsync(
        MetadataV2Resource resource,
        JsonElement? filter,
        string? filterLang,
        string? filterCrs,
        bool defaultFilterLangIsText,
        CancellationToken cancellationToken,
        string? collectionId = null,
        IReadOnlyList<MetadataV2Resource>? crossCollectionResources = null)
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
        if (crossCollectionResources is { Count: > 1 })
        {
            parsedExpression = ApplyCrossCollectionNullSemantics(
                parsedExpression,
                resource,
                crossCollectionResources);
        }

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
            return ProcessingResult.Failure(ex.Message, ex is UnknownFilterFieldException);
        }

        var translationResult = _filterExpressionService.Translate(parsedExpression, resource);
        return translationResult.IsSuccess
            ? ProcessingResult.Success(translationResult.SqlFilter, parsedExpression)
            : ProcessingResult.Failure(
                translationResult.ErrorMessage ?? "Invalid filter expression.",
                translationResult.IsUnknownField);
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

    /// <summary>
    /// Replaces a property that is absent from the current collection, but declared by
    /// another selected collection, with SQL <c>NULL</c>. Typed predicates that cannot
    /// accept a null operand collapse to a null Boolean result, while the surrounding
    /// expression is preserved so the provider evaluates AND/OR/NOT with SQL three-valued
    /// logic. Properties absent from every selected collection remain references and
    /// continue through the existing unknown-field error path.
    /// </summary>
    internal static FilterExpression ApplyCrossCollectionNullSemantics(
        FilterExpression expression,
        MetadataV2Resource resource,
        IReadOnlyList<MetadataV2Resource> crossCollectionResources)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(crossCollectionResources);

        var localSchema = FilterFieldSchema.From(resource);
        return RewriteMissingNode(expression, localSchema, crossCollectionResources).Expression;
    }

    private static MissingNodeRewrite RewriteMissingNode(
        FilterExpression expression,
        FilterFieldSchema localSchema,
        IReadOnlyList<MetadataV2Resource> crossCollectionResources)
    {
        switch (expression)
        {
            case PropertyReference property:
                if (localSchema.TryGetFieldType(property.PropertyName, out var localFieldType))
                {
                    return new MissingNodeRewrite(
                        property,
                        ValueKind: GetValueKind(localFieldType));
                }

                return TryGetCrossCollectionFieldType(
                    property.PropertyName,
                    crossCollectionResources,
                    out var crossCollectionFieldType)
                    ? new MissingNodeRewrite(
                        new Literal(null, LiteralType.Null),
                        NullOrigin: SubstitutedNullOrigin.DirectProperty,
                        ValueKind: GetValueKind(crossCollectionFieldType))
                    : new MissingNodeRewrite(property, HasGloballyUnknownProperty: true);
            case Literal literal:
                return new MissingNodeRewrite(
                    literal,
                    ValueKind: GetValueKind(literal),
                    IsDirectAuthoredNull: literal.Type == LiteralType.Null);
            case BinaryExpression binary:
                var binaryLeft = RewriteMissingNode(binary.Left, localSchema, crossCollectionResources);
                var binaryRight = RewriteMissingNode(binary.Right, localSchema, crossCollectionResources);
                var binaryAnalysis = AnalyzeBinary(binary.Operator, binaryLeft, binaryRight);
                return new MissingNodeRewrite(
                    binary with { Left = binaryLeft.Expression, Right = binaryRight.Expression },
                    NullOrigin: binaryAnalysis.NullOrigin,
                    ValueKind: binaryAnalysis.ValueKind,
                    HasGloballyUnknownProperty:
                        binaryLeft.HasGloballyUnknownProperty || binaryRight.HasGloballyUnknownProperty,
                    HasValidationRisk: binaryAnalysis.HasValidationRisk ||
                        binaryLeft.HasValidationRisk || binaryRight.HasValidationRisk);
            case UnaryExpression unary:
                var unaryOperand = RewriteMissingNode(unary.Operand, localSchema, crossCollectionResources);
                var unaryAnalysis = AnalyzeUnary(unary.Operator, unaryOperand);
                return new MissingNodeRewrite(
                    unary with { Operand = unaryOperand.Expression },
                    NullOrigin: unaryAnalysis.NullOrigin,
                    ValueKind: unaryAnalysis.ValueKind,
                    HasGloballyUnknownProperty: unaryOperand.HasGloballyUnknownProperty,
                    HasValidationRisk: unaryAnalysis.HasValidationRisk || unaryOperand.HasValidationRisk);
            case SpatialPredicate spatial:
                var spatialLeft = RewriteMissingNode(spatial.Left, localSchema, crossCollectionResources);
                var spatialRight = RewriteMissingNode(spatial.Right, localSchema, crossCollectionResources);
                return CollapseTypedPredicate(
                    spatial with { Left = spatialLeft.Expression, Right = spatialRight.Expression },
                    (spatialLeft, RewrittenValueKind.Geometry),
                    (spatialRight, RewrittenValueKind.Geometry));
            case SpatialDistancePredicate spatialDistance:
                var distanceLeft = RewriteMissingNode(spatialDistance.Left, localSchema, crossCollectionResources);
                var distanceRight = RewriteMissingNode(spatialDistance.Right, localSchema, crossCollectionResources);
                var distanceValue = RewriteMissingNode(spatialDistance.Distance, localSchema, crossCollectionResources);
                return CollapseTypedPredicate(
                    spatialDistance with
                    {
                        Left = distanceLeft.Expression,
                        Right = distanceRight.Expression,
                        Distance = distanceValue.Expression
                    },
                    (distanceLeft, RewrittenValueKind.Geometry),
                    (distanceRight, RewrittenValueKind.Geometry),
                    (distanceValue, RewrittenValueKind.Numeric));
            case TemporalPredicate temporal:
                var temporalLeft = RewriteMissingNode(temporal.Left, localSchema, crossCollectionResources);
                var temporalRight = RewriteMissingNode(temporal.Right, localSchema, crossCollectionResources);
                return CollapseTypedPredicate(
                    temporal with { Left = temporalLeft.Expression, Right = temporalRight.Expression },
                    (temporalLeft, RewrittenValueKind.Temporal),
                    (temporalRight, RewrittenValueKind.Temporal));
            case ArrayPredicate array:
                var arrayLeft = RewriteMissingNode(array.Left, localSchema, crossCollectionResources);
                var arrayRight = RewriteMissingNode(array.Right, localSchema, crossCollectionResources);
                return CollapseTypedPredicate(
                    array with { Left = arrayLeft.Expression, Right = arrayRight.Expression },
                    (arrayLeft, RewrittenValueKind.Array),
                    (arrayRight, RewrittenValueKind.Array));
            case FunctionCall functionCall:
                var arguments = functionCall.Arguments
                    .Select(argument => RewriteMissingNode(argument, localSchema, crossCollectionResources))
                    .ToArray();
                var functionAnalysis = AnalyzeFunction(functionCall.FunctionName, arguments);
                return new MissingNodeRewrite(
                    functionCall with { Arguments = arguments.Select(static argument => argument.Expression).ToArray() },
                    NullOrigin: functionAnalysis.NullOrigin,
                    ValueKind: functionAnalysis.ValueKind,
                    HasGloballyUnknownProperty: arguments.Any(static argument => argument.HasGloballyUnknownProperty),
                    HasValidationRisk: functionAnalysis.HasValidationRisk ||
                        arguments.Any(static argument => argument.HasValidationRisk));
            case GeometryLiteral geometryLiteral:
                return new MissingNodeRewrite(
                    geometryLiteral,
                    ValueKind: RewrittenValueKind.Geometry);
            case IntervalLiteral intervalLiteral:
                return new MissingNodeRewrite(
                    intervalLiteral,
                    ValueKind: RewrittenValueKind.Temporal);
            case ArrayLiteral arrayLiteral:
                var elements = arrayLiteral.Elements
                    .Select(element => RewriteMissingNode(element, localSchema, crossCollectionResources))
                    .ToArray();
                return new MissingNodeRewrite(
                    arrayLiteral with { Elements = elements.Select(static element => element.Expression).ToArray() },
                    ValueKind: RewrittenValueKind.Array,
                    ElementKind: GetCommonValueKind(elements),
                    HasGloballyUnknownProperty: elements.Any(static element => element.HasGloballyUnknownProperty),
                    HasValidationRisk: elements.Any(static element => element.HasValidationRisk));
            case ValueList valueList:
                var values = valueList.Values
                    .Select(value => RewriteMissingNode(value, localSchema, crossCollectionResources))
                    .ToArray();
                return new MissingNodeRewrite(
                    valueList with { Values = values.Select(static value => value.Expression).ToArray() },
                    ValueKind: RewrittenValueKind.Array,
                    ElementKind: GetCommonValueKind(values),
                    HasGloballyUnknownProperty: values.Any(static value => value.HasGloballyUnknownProperty),
                    HasValidationRisk: values.Any(static value => value.HasValidationRisk));
            default:
                return new MissingNodeRewrite(expression, HasValidationRisk: true);
        }
    }

    private static MissingNodeRewrite CollapseTypedPredicate(
        FilterExpression expression,
        params (MissingNodeRewrite Rewrite, RewrittenValueKind ExpectedKind)[] operands)
    {
        // Collapse only a scalar NULL produced directly by this rewrite or propagated through a
        // known strict function. A non-strict function such as COALESCE is not necessarily null,
        // and a collapsed typed predicate is Boolean UNKNOWN rather than a reusable typed value.
        // Unresolved properties and authored NULL operands must remain visible to validation.
        var hasGloballyUnknownProperty = operands.Any(static operand => operand.Rewrite.HasGloballyUnknownProperty);
        var hasDirectAuthoredNull = operands.Any(static operand => operand.Rewrite.IsDirectAuthoredNull);
        var hasValidationRisk = operands.Any(static operand => operand.Rewrite.HasValidationRisk);
        var operandKindsAreValid = operands.All(static operand =>
            operand.Rewrite.ValueKind == operand.ExpectedKind ||
            operand.Rewrite.ValueKind == RewrittenValueKind.Null &&
            operand.Rewrite.NullOrigin == SubstitutedNullOrigin.NullPropagatingFunction);
        var canCollapse = operands.Any(static operand => operand.Rewrite.NullOrigin is
                SubstitutedNullOrigin.DirectProperty or
                SubstitutedNullOrigin.NullPropagatingFunction) &&
            operandKindsAreValid &&
            !hasGloballyUnknownProperty &&
            !hasDirectAuthoredNull &&
            !hasValidationRisk;

        return canCollapse
            ? new MissingNodeRewrite(
                new Literal(null, LiteralType.Null),
                NullOrigin: SubstitutedNullOrigin.CollapsedTypedPredicate,
                ValueKind: RewrittenValueKind.Boolean)
            : new MissingNodeRewrite(
                expression,
                ValueKind: RewrittenValueKind.Boolean,
                HasGloballyUnknownProperty: hasGloballyUnknownProperty,
                HasValidationRisk: hasValidationRisk || !operandKindsAreValid || hasDirectAuthoredNull);
    }

    private static FunctionAnalysis AnalyzeBinary(
        BinaryOperator binaryOperator,
        MissingNodeRewrite left,
        MissingNodeRewrite right)
    {
        if (binaryOperator is BinaryOperator.Add or BinaryOperator.Subtract or
            BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo or
            BinaryOperator.Div or BinaryOperator.Power)
        {
            var isValid = IsNumericOrNull(left.ValueKind) && IsNumericOrNull(right.ValueKind) &&
                (left.ValueKind == RewrittenValueKind.Numeric || right.ValueKind == RewrittenValueKind.Numeric);
            var nullOrigin = isValid &&
                (IsEligibleSubstitutedNull(left.NullOrigin) || IsEligibleSubstitutedNull(right.NullOrigin))
                ? SubstitutedNullOrigin.NullPropagatingFunction
                : SubstitutedNullOrigin.None;
            return isValid
                ? new FunctionAnalysis(nullOrigin, RewrittenValueKind.Numeric)
                : InvalidAnalysis();
        }

        if (binaryOperator is BinaryOperator.And or BinaryOperator.Or)
        {
            return IsBooleanOrNull(left.ValueKind) && IsBooleanOrNull(right.ValueKind)
                ? new FunctionAnalysis(SubstitutedNullOrigin.None, RewrittenValueKind.Boolean)
                : InvalidAnalysis();
        }

        if (binaryOperator is BinaryOperator.Like or BinaryOperator.NotLike)
        {
            var isValid = IsScalarOrNull(left.ValueKind) && IsScalarOrNull(right.ValueKind) &&
                (left.ValueKind == RewrittenValueKind.Scalar || right.ValueKind == RewrittenValueKind.Scalar);
            return isValid
                ? new FunctionAnalysis(SubstitutedNullOrigin.None, RewrittenValueKind.Boolean)
                : InvalidAnalysis();
        }

        if (binaryOperator is BinaryOperator.In or BinaryOperator.NotIn)
        {
            var isEmptyList = right.Expression is ValueList { Values.Count: 0 };
            var isValid = right.ValueKind == RewrittenValueKind.Array &&
                (isEmptyList || AreCompatibleValueKinds(left.ValueKind, right.ElementKind));
            return isValid
                ? new FunctionAnalysis(SubstitutedNullOrigin.None, RewrittenValueKind.Boolean)
                : InvalidAnalysis();
        }

        var kindsAreCompatible = AreCompatibleValueKinds(left.ValueKind, right.ValueKind);
        if (binaryOperator is BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual or
            BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual)
        {
            kindsAreCompatible = kindsAreCompatible &&
                (IsOrderableOrNull(left.ValueKind) && IsOrderableOrNull(right.ValueKind));
        }

        return kindsAreCompatible
            ? new FunctionAnalysis(SubstitutedNullOrigin.None, RewrittenValueKind.Boolean)
            : InvalidAnalysis();
    }

    private static FunctionAnalysis AnalyzeUnary(UnaryOperator unaryOperator, MissingNodeRewrite operand)
    {
        return unaryOperator switch
        {
            UnaryOperator.Negate when operand.ValueKind == RewrittenValueKind.Numeric =>
                new FunctionAnalysis(
                    IsEligibleSubstitutedNull(operand.NullOrigin)
                        ? SubstitutedNullOrigin.NullPropagatingFunction
                        : SubstitutedNullOrigin.None,
                    RewrittenValueKind.Numeric),
            UnaryOperator.Negate => InvalidAnalysis(),
            UnaryOperator.Not when IsBooleanOrNull(operand.ValueKind) =>
                new FunctionAnalysis(SubstitutedNullOrigin.None, RewrittenValueKind.Boolean),
            UnaryOperator.Not => InvalidAnalysis(),
            UnaryOperator.IsNull or UnaryOperator.IsNotNull =>
                new FunctionAnalysis(SubstitutedNullOrigin.None, RewrittenValueKind.Boolean),
            _ => InvalidAnalysis()
        };
    }

    private static FunctionAnalysis AnalyzeFunction(
        string functionName,
        IReadOnlyList<MissingNodeRewrite> arguments)
    {
        if (arguments.Any(static argument => argument.HasValidationRisk))
        {
            return InvalidAnalysis();
        }

        var normalizedName = functionName.ToUpperInvariant();
        if (normalizedName is "NOW" or "CURRENT_DATE" or "CURRENT_TIMESTAMP" or "CURRENT_TIME")
        {
            return arguments.Count == 0
                ? new FunctionAnalysis(SubstitutedNullOrigin.None, RewrittenValueKind.Temporal)
                : InvalidAnalysis();
        }

        if (normalizedName == "COALESCE")
        {
            if (arguments.Count == 0 || arguments.Any(static argument =>
                    argument.ValueKind == RewrittenValueKind.Unknown))
            {
                return InvalidAnalysis();
            }

            var concreteKinds = arguments
                .Select(static argument => argument.ValueKind)
                .Where(static kind => kind != RewrittenValueKind.Null)
                .Distinct()
                .ToArray();
            var allResultsAreNull = arguments.All(IsGuaranteedNullResult);
            var hasSubstitutedNull = arguments.Any(static argument =>
                IsEligibleSubstitutedNull(argument.NullOrigin));
            if (concreteKinds.Length == 0 && allResultsAreNull && !hasSubstitutedNull)
            {
                return InvalidAnalysis();
            }

            var nullOrigin = allResultsAreNull && hasSubstitutedNull
                ? SubstitutedNullOrigin.NullPropagatingFunction
                : SubstitutedNullOrigin.None;
            return new FunctionAnalysis(
                nullOrigin,
                concreteKinds.Length switch
                {
                    0 => RewrittenValueKind.Null,
                    1 => concreteKinds[0],
                    _ => RewrittenValueKind.Unknown
                },
                HasValidationRisk: concreteKinds.Length > 1);
        }

        if (normalizedName == "CONCAT")
        {
            return new FunctionAnalysis(SubstitutedNullOrigin.None, RewrittenValueKind.Scalar);
        }

        if (normalizedName == "CAST")
        {
            return AnalyzeCast(arguments);
        }

        if (normalizedName == "NULLIF")
        {
            return AnalyzeNullIf(arguments);
        }

        if (normalizedName == "CASE")
        {
            return AnalyzeCase(arguments);
        }

        return normalizedName switch
        {
            "ABS" or "CEIL" or "CEILING" or "FLOOR" or "SQRT" or "SIN" or "COS" or "TAN" or
                "LOG" or "EXP" => AnalyzeStrictFunction(
                    arguments,
                    RewrittenValueKind.Numeric,
                    RewrittenValueKind.Numeric),
            "POWER" or "MOD" => AnalyzeStrictFunction(
                arguments,
                RewrittenValueKind.Numeric,
                RewrittenValueKind.Numeric,
                RewrittenValueKind.Numeric),
            "ROUND" => arguments.Count switch
            {
                1 => AnalyzeStrictFunction(
                    arguments,
                    RewrittenValueKind.Numeric,
                    RewrittenValueKind.Numeric),
                2 => AnalyzeStrictFunction(
                    arguments,
                    RewrittenValueKind.Numeric,
                    RewrittenValueKind.Numeric,
                    RewrittenValueKind.Numeric),
                _ => InvalidAnalysis()
            },
            "YEAR" or "MONTH" or "DAY" or "HOUR" or "MINUTE" or "SECOND" or
                "EXTRACT_DOW" or "EXTRACT_DOY" or "EXTRACT_QUARTER" or "EXTRACT_WEEK" or
                "EXTRACT_EPOCH" => AnalyzeStrictFunction(
                    arguments,
                    RewrittenValueKind.Numeric,
                    RewrittenValueKind.Temporal),
            "GEOLENGTH" or "ST_AREA" or "ST_LENGTH" or "ST_PERIMETER" => AnalyzeStrictFunction(
                arguments,
                RewrittenValueKind.Numeric,
                RewrittenValueKind.Geometry),
            "GEODISTANCE" or "ST_DISTANCE" => AnalyzeStrictFunction(
                arguments,
                RewrittenValueKind.Numeric,
                RewrittenValueKind.Geometry,
                RewrittenValueKind.Geometry),
            "ST_NUMGEOMETRIES" or "ST_SRID" => AnalyzeStrictFunction(
                arguments,
                RewrittenValueKind.Numeric,
                RewrittenValueKind.Geometry),
            "ST_CENTROID" or "ST_ENVELOPE" or "ST_CONVEXHULL" or "ST_BOUNDARY" => AnalyzeStrictFunction(
                arguments,
                RewrittenValueKind.Geometry,
                RewrittenValueKind.Geometry),
            "ST_BUFFER" => AnalyzeStrictFunction(
                arguments,
                RewrittenValueKind.Geometry,
                RewrittenValueKind.Geometry,
                RewrittenValueKind.Numeric),
            _ => InvalidAnalysis()
        };
    }

    private static FunctionAnalysis AnalyzeStrictFunction(
        IReadOnlyList<MissingNodeRewrite> arguments,
        RewrittenValueKind resultKind,
        params RewrittenValueKind[] expectedArgumentKinds)
    {
        if (arguments.Count != expectedArgumentKinds.Length)
        {
            return InvalidAnalysis();
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].ValueKind != expectedArgumentKinds[index] ||
                arguments[index].IsDirectAuthoredNull)
            {
                return InvalidAnalysis();
            }
        }

        var nullOrigin = arguments.Any(static argument => argument.NullOrigin is
            SubstitutedNullOrigin.DirectProperty or
            SubstitutedNullOrigin.NullPropagatingFunction)
            ? SubstitutedNullOrigin.NullPropagatingFunction
            : SubstitutedNullOrigin.None;
        return new FunctionAnalysis(nullOrigin, resultKind);
    }

    private static FunctionAnalysis AnalyzeCast(IReadOnlyList<MissingNodeRewrite> arguments)
    {
        if (arguments.Count != 2 ||
            !TryGetCastTargetValueKind(arguments[1].Expression, out var targetKind))
        {
            return InvalidAnalysis();
        }

        var nullOrigin = IsEligibleSubstitutedNull(arguments[0].NullOrigin)
            ? SubstitutedNullOrigin.NullPropagatingFunction
            : SubstitutedNullOrigin.None;
        return new FunctionAnalysis(nullOrigin, targetKind);
    }

    private static FunctionAnalysis AnalyzeNullIf(IReadOnlyList<MissingNodeRewrite> arguments)
    {
        if (arguments.Count != 2)
        {
            return InvalidAnalysis();
        }

        var first = arguments[0];
        var second = arguments[1];
        var firstIsNullWithKnownContext = first.ValueKind is
                (RewrittenValueKind.Null or RewrittenValueKind.Unknown) &&
            (first.IsDirectAuthoredNull || IsEligibleSubstitutedNull(first.NullOrigin)) &&
            second.ValueKind is not RewrittenValueKind.Unknown and not RewrittenValueKind.Null;
        var resultKind = firstIsNullWithKnownContext ? second.ValueKind : first.ValueKind;
        var kindsAreValid = firstIsNullWithKnownContext ||
            first.ValueKind is not RewrittenValueKind.Unknown and not RewrittenValueKind.Null &&
            (second.ValueKind == first.ValueKind || second.ValueKind == RewrittenValueKind.Null);
        if (!kindsAreValid)
        {
            return InvalidAnalysis();
        }

        var nullOrigin = IsEligibleSubstitutedNull(first.NullOrigin)
            ? SubstitutedNullOrigin.NullPropagatingFunction
            : SubstitutedNullOrigin.None;
        return new FunctionAnalysis(nullOrigin, resultKind);
    }

    private static FunctionAnalysis AnalyzeCase(IReadOnlyList<MissingNodeRewrite> arguments)
    {
        if (arguments.Count < 2)
        {
            return InvalidAnalysis();
        }

        var branchCount = arguments.Count / 2;
        for (var index = 0; index < branchCount; index++)
        {
            if (!IsBooleanOrNull(arguments[index * 2].ValueKind))
            {
                return InvalidAnalysis();
            }
        }

        var resultKinds = Enumerable.Range(0, branchCount)
            .Select(index => arguments[(index * 2) + 1].ValueKind)
            .Concat(arguments.Count % 2 == 1
                ? [arguments[^1].ValueKind]
                : [])
            .ToArray();
        if (resultKinds.Any(static kind => kind == RewrittenValueKind.Unknown))
        {
            return InvalidAnalysis();
        }

        var concreteKinds = resultKinds
            .Where(static kind => kind != RewrittenValueKind.Null)
            .Distinct()
            .ToArray();
        var resultExpressions = Enumerable.Range(0, branchCount)
            .Select(index => arguments[(index * 2) + 1])
            .Concat(arguments.Count % 2 == 1
                ? [arguments[^1]]
                : [])
            .ToArray();
        var allResultsAreNull = resultExpressions.All(IsGuaranteedNullResult);
        var hasSubstitutedNull = resultExpressions.Any(static result =>
            IsEligibleSubstitutedNull(result.NullOrigin));
        if (concreteKinds.Length == 0 && allResultsAreNull && !hasSubstitutedNull)
        {
            return InvalidAnalysis();
        }

        var nullOrigin = allResultsAreNull && hasSubstitutedNull
            ? SubstitutedNullOrigin.NullPropagatingFunction
            : SubstitutedNullOrigin.None;
        return new FunctionAnalysis(
            nullOrigin,
            concreteKinds.Length switch
            {
                0 => RewrittenValueKind.Null,
                1 => concreteKinds[0],
                _ => RewrittenValueKind.Unknown
            },
            HasValidationRisk: concreteKinds.Length > 1);
    }

    private static bool TryGetCastTargetValueKind(
        FilterExpression targetExpression,
        out RewrittenValueKind valueKind)
    {
        valueKind = RewrittenValueKind.Unknown;
        if (targetExpression is not Literal { Type: LiteralType.Text, Value: string rawTarget })
        {
            return false;
        }

        var target = rawTarget.Trim('\'', '"').ToUpperInvariant();
        var open = target.IndexOf('(', StringComparison.Ordinal);
        var hasTypeArguments = open >= 0;
        var baseType = target;
        if (hasTypeArguments)
        {
            if (!target.EndsWith(')'))
            {
                return false;
            }

            baseType = target[..open].TrimEnd();
            var inner = target[(open + 1)..^1];
            var commaCount = 0;
            var hasDigit = false;
            foreach (var character in inner)
            {
                if (char.IsWhiteSpace(character))
                {
                    continue;
                }

                if (character == ',')
                {
                    if (++commaCount > 1 || !hasDigit)
                    {
                        return false;
                    }

                    continue;
                }

                if (!char.IsAsciiDigit(character))
                {
                    return false;
                }

                hasDigit = true;
            }

            if (!hasDigit)
            {
                return false;
            }
        }

        valueKind = baseType switch
        {
            "INTEGER" or "INT" or "NUMERIC" or "DECIMAL" or "REAL" or "FLOAT" or
                "DOUBLE" or "DOUBLE PRECISION" => RewrittenValueKind.Numeric,
            "TEXT" or "STRING" or "VARCHAR" => RewrittenValueKind.Scalar,
            "CHAR" when hasTypeArguments => RewrittenValueKind.Scalar,
            "BOOLEAN" or "BOOL" => RewrittenValueKind.Boolean,
            "DATE" or "TIMESTAMP" => RewrittenValueKind.Temporal,
            "GEOMETRY" or "GEOGRAPHY" => RewrittenValueKind.Geometry,
            _ => RewrittenValueKind.Unknown
        };
        return valueKind != RewrittenValueKind.Unknown;
    }

    private static bool IsEligibleSubstitutedNull(SubstitutedNullOrigin origin) => origin is
        SubstitutedNullOrigin.DirectProperty or SubstitutedNullOrigin.NullPropagatingFunction;

    private static bool IsGuaranteedNullResult(MissingNodeRewrite result) =>
        result.IsDirectAuthoredNull || result.NullOrigin != SubstitutedNullOrigin.None;

    private static FunctionAnalysis InvalidAnalysis() => new(
        SubstitutedNullOrigin.None,
        RewrittenValueKind.Unknown,
        HasValidationRisk: true);

    private static RewrittenValueKind GetCommonValueKind(IReadOnlyList<MissingNodeRewrite> values)
    {
        var concreteKinds = values
            .Select(static value => value.ValueKind)
            .Where(static kind => kind != RewrittenValueKind.Null)
            .Distinct()
            .ToArray();
        return concreteKinds.Length switch
        {
            0 when values.Count > 0 => RewrittenValueKind.Null,
            1 => concreteKinds[0],
            _ => RewrittenValueKind.Unknown
        };
    }

    private static bool AreCompatibleValueKinds(
        RewrittenValueKind left,
        RewrittenValueKind right) =>
        left != RewrittenValueKind.Unknown &&
        right != RewrittenValueKind.Unknown &&
        (left == right || left == RewrittenValueKind.Null || right == RewrittenValueKind.Null);

    private static bool IsBooleanOrNull(RewrittenValueKind valueKind) => valueKind is
        RewrittenValueKind.Boolean or RewrittenValueKind.Null;

    private static bool IsNumericOrNull(RewrittenValueKind valueKind) => valueKind is
        RewrittenValueKind.Numeric or RewrittenValueKind.Null;

    private static bool IsScalarOrNull(RewrittenValueKind valueKind) => valueKind is
        RewrittenValueKind.Scalar or RewrittenValueKind.Null;

    private static bool IsOrderableOrNull(RewrittenValueKind valueKind) => valueKind is
        RewrittenValueKind.Scalar or RewrittenValueKind.Numeric or RewrittenValueKind.Temporal or
        RewrittenValueKind.Null;

    private static RewrittenValueKind GetValueKind(MetadataV2FieldType fieldType) => fieldType switch
    {
        MetadataV2FieldType.Unknown => RewrittenValueKind.Unknown,
        MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography => RewrittenValueKind.Geometry,
        MetadataV2FieldType.Integer or MetadataV2FieldType.BigInteger or MetadataV2FieldType.Double or
            MetadataV2FieldType.Float => RewrittenValueKind.Numeric,
        MetadataV2FieldType.Date or MetadataV2FieldType.DateTime or MetadataV2FieldType.Time =>
            RewrittenValueKind.Temporal,
        MetadataV2FieldType.Json => RewrittenValueKind.Array,
        MetadataV2FieldType.Boolean => RewrittenValueKind.Boolean,
        _ => RewrittenValueKind.Scalar
    };

    private static RewrittenValueKind GetValueKind(Literal literal) => literal.Type switch
    {
        LiteralType.Number => RewrittenValueKind.Numeric,
        LiteralType.Date or LiteralType.DateTime => RewrittenValueKind.Temporal,
        LiteralType.Boolean => RewrittenValueKind.Boolean,
        LiteralType.Null => RewrittenValueKind.Null,
        _ => RewrittenValueKind.Scalar
    };

    private readonly record struct MissingNodeRewrite(
        FilterExpression Expression,
        SubstitutedNullOrigin NullOrigin = SubstitutedNullOrigin.None,
        RewrittenValueKind ValueKind = RewrittenValueKind.Unknown,
        RewrittenValueKind ElementKind = RewrittenValueKind.Unknown,
        bool HasGloballyUnknownProperty = false,
        bool IsDirectAuthoredNull = false,
        bool HasValidationRisk = false);

    private readonly record struct FunctionAnalysis(
        SubstitutedNullOrigin NullOrigin,
        RewrittenValueKind ValueKind,
        bool HasValidationRisk = false);

    private enum SubstitutedNullOrigin
    {
        None,
        DirectProperty,
        NullPropagatingFunction,
        CollapsedTypedPredicate
    }

    private enum RewrittenValueKind
    {
        Unknown,
        Null,
        Scalar,
        Numeric,
        Boolean,
        Geometry,
        Temporal,
        Array
    }

    private static bool TryGetCrossCollectionFieldType(
        string propertyName,
        IReadOnlyList<MetadataV2Resource> crossCollectionResources,
        out MetadataV2FieldType fieldType)
    {
        MetadataV2FieldType? resolvedFieldType = null;
        foreach (var candidate in crossCollectionResources)
        {
            var found = FilterFieldSchema.From(candidate).TryGetFieldType(propertyName, out var candidateFieldType);

            // A target without a temporal field retains the STAC `datetime` name
            // after core-queryable rewriting. It is still catalog-valid when another
            // selected collection resolves `datetime` to its own temporal field.
            if (!found &&
                string.Equals(propertyName, "datetime", StringComparison.OrdinalIgnoreCase) &&
                ResolveTemporalFieldName(candidate) is not null)
            {
                candidateFieldType = MetadataV2FieldType.DateTime;
                found = true;
            }

            if (!found)
            {
                continue;
            }

            if (resolvedFieldType is null)
            {
                resolvedFieldType = candidateFieldType;
                continue;
            }

            if (GetValueKind(resolvedFieldType.Value) != GetValueKind(candidateFieldType))
            {
                // The property is catalog-valid, but an order-independent typed value kind
                // cannot be inferred. Preserve the substituted NULL without authorizing a
                // typed-predicate collapse that could mask validation for another collection.
                fieldType = MetadataV2FieldType.Unknown;
                return true;
            }
        }

        if (resolvedFieldType is { } resolved)
        {
            fieldType = resolved;
            return true;
        }

        fieldType = MetadataV2FieldType.Unknown;
        return false;
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
