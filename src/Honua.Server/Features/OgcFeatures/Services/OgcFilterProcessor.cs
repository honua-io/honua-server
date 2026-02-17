// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Parsing;
using Honua.Server.Features.Ogc.Common;
using Microsoft.Extensions.Logging;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.OgcFeatures.Services;

/// <summary>
/// Simple bounding box representation.
/// </summary>
internal sealed record BoundingBox(double MinX, double MinY, double MaxX, double MaxY);

/// <summary>
/// Processes and combines CQL2, spatial, and temporal filters for OGC Features queries.
/// </summary>
internal sealed partial class OgcFilterProcessor
{
    private const string InvalidCqlFilterPrefix = "Invalid CQL filter";
    private const string FilterLangCql2Text = "cql2-text";
    private const string FilterLangCql2Json = "cql2-json";

    /// <summary>
    /// Result of filter processing operation.
    /// </summary>
    public sealed class FilterProcessingResult
    {
        public bool IsSuccess { get; init; }
        public string? ErrorMessage { get; init; }
        public string? CombinedFilter { get; init; }
        public SqlFragment? SqlFilter { get; init; }
        public SpatialFilter? SpatialFilter { get; init; }
        public TemporalFilter? TemporalFilter { get; init; }
        public bool IncludeNullGeometry { get; init; }
        public CrsDefinition CrsDefinition { get; init; }

        public static FilterProcessingResult Success(
            string? combinedFilter,
            SqlFragment? sqlFilter,
            SpatialFilter? spatialFilter,
            TemporalFilter? temporalFilter,
            bool includeNullGeometry,
            CrsDefinition crsDefinition)
            => new()
            {
                IsSuccess = true,
                CombinedFilter = combinedFilter,
                SqlFilter = sqlFilter,
                SpatialFilter = spatialFilter,
                TemporalFilter = temporalFilter,
                IncludeNullGeometry = includeNullGeometry,
                CrsDefinition = crsDefinition
            };

        public static FilterProcessingResult Failure(string errorMessage)
            => new()
            {
                IsSuccess = false,
                ErrorMessage = errorMessage
            };
    }

    private readonly IFilterExpressionService _filterExpressionService;
    private readonly ICrsRegistry _crsRegistry;
    private readonly ILogger<OgcFilterProcessor> _logger;

    public OgcFilterProcessor(
        IFilterExpressionService filterExpressionService,
        ICrsRegistry crsRegistry,
        ILogger<OgcFilterProcessor> logger)
    {
        _filterExpressionService = filterExpressionService ?? throw new ArgumentNullException(nameof(filterExpressionService));
        _crsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Processes all filter types and combines them into a unified query structure.
    /// </summary>
    public async Task<FilterProcessingResult> ProcessFiltersAsync(
        HttpRequest request,
        LayerDefinition layer,
        string? filter,
        string? bbox,
        string? datetime,
        string? crs,
        CancellationToken cancellationToken)
    {
        try
        {
            var filterLang = OgcCommonUtilities.GetQueryValue(request, "filter-lang");
            var filterCrs = OgcCommonUtilities.GetQueryValue(request, "filter-crs");
            var bboxCrs = OgcCommonUtilities.GetQueryValue(request, "bbox-crs");

            var filterLangResult = TryResolveFilterLanguage(filterLang);
            if (!filterLangResult.IsSuccess)
            {
                return FilterProcessingResult.Failure(filterLangResult.ErrorMessage!);
            }

            var supportedCrs = await OgcFeaturesUtilities.GetSupportedCrsDefinitionsAsync(
                layer,
                _crsRegistry,
                cancellationToken).ConfigureAwait(false);
            if (!OgcFeaturesUtilities.TryResolveCrs(crs, supportedCrs, out var crsDefinition, out var crsError))
            {
                return FilterProcessingResult.Failure(crsError!);
            }

            if (!string.IsNullOrWhiteSpace(filterCrs) && string.IsNullOrWhiteSpace(filter))
            {
                return FilterProcessingResult.Failure("filter-crs requires a filter parameter.");
            }

            if (!OgcFeaturesUtilities.TryResolveCrs(
                    OgcFeaturesUtilities.Crs84Uri,
                    supportedCrs,
                    out var defaultFilterCrsDefinition,
                    out var defaultFilterCrsError))
            {
                return FilterProcessingResult.Failure(
                    defaultFilterCrsError ?? "Unable to resolve default filter CRS.");
            }

            var filterCrsDefinition = defaultFilterCrsDefinition;
            if (!string.IsNullOrWhiteSpace(filterCrs))
            {
                if (!OgcFeaturesUtilities.TryResolveCrs(filterCrs, supportedCrs, out filterCrsDefinition, out var filterCrsError))
                {
                    return FilterProcessingResult.Failure(filterCrsError!);
                }
            }

            var bboxCrsDefinition = defaultFilterCrsDefinition;
            if (!string.IsNullOrWhiteSpace(bboxCrs))
            {
                if (!OgcFeaturesUtilities.TryResolveCrs(bboxCrs, supportedCrs, out bboxCrsDefinition, out var bboxCrsError))
                {
                    return FilterProcessingResult.Failure(bboxCrsError!);
                }
            }

            // Process CQL filters
            FilterExpression? filterExpression = null;
            string? combinedFilter = null;

            if (string.Equals(filterLangResult.ResolvedLanguage, FilterLangCql2Json, StringComparison.OrdinalIgnoreCase))
            {
                var jsonResult = ProcessCql2JsonFilter(filter, request, layer);
                if (!jsonResult.IsSuccess)
                {
                    return FilterProcessingResult.Failure(jsonResult.ErrorMessage!);
                }
                filterExpression = jsonResult.FilterExpression;
                combinedFilter = jsonResult.CombinedFilter;
            }
            else
            {
                var textResult = ProcessCql2TextFilter(filter, request, layer);
                if (!textResult.IsSuccess)
                {
                    return FilterProcessingResult.Failure(textResult.ErrorMessage!);
                }
                filterExpression = textResult.FilterExpression;
                combinedFilter = textResult.CombinedFilter;
            }

            if (filterExpression != null && !string.IsNullOrWhiteSpace(filterCrs))
            {
                filterExpression = ApplyFilterCrs(filterExpression, filterCrsDefinition);
            }

            if (filterExpression != null)
            {
                filterExpression = NormalizeFilterAxisOrder(filterExpression, filterCrsDefinition.AxisOrder);
            }

            // Translate to SQL
            SqlFragment? sqlFilter = null;
            if (filterExpression != null)
            {
                var translationResult = _filterExpressionService.Translate(filterExpression, layer);
                if (!translationResult.IsSuccess)
                {
                    return FilterProcessingResult.Failure(
                        $"{InvalidCqlFilterPrefix}: {SanitizeCqlErrorMessage(translationResult.ErrorMessage ?? "Invalid filter.")}");
                }

                sqlFilter = translationResult.SqlFilter;
            }

            // Process spatial filter (bbox)
            var bboxResult = ProcessBboxFilter(bbox, bboxCrsDefinition);
            if (!bboxResult.IsSuccess)
            {
                return FilterProcessingResult.Failure(bboxResult.ErrorMessage!);
            }

            // Process temporal filter
            var temporalResult = ProcessTemporalFilter(datetime, layer);
            if (!temporalResult.IsSuccess)
            {
                return FilterProcessingResult.Failure(temporalResult.ErrorMessage!);
            }

            // OGC API Features should retain null-geometry records even when spatial filters are present.
            var includeNullGeometry = true;

            return FilterProcessingResult.Success(
                combinedFilter,
                sqlFilter,
                bboxResult.SpatialFilter,
                temporalResult.TemporalFilter,
                includeNullGeometry,
                crsDefinition);
        }
        catch (Exception ex)
        {
            return FilterProcessingResult.Failure($"Error processing filters: {ex.Message}");
        }
    }

    private CqlFilterResult ProcessCql2JsonFilter(
        string? filter,
        HttpRequest request,
        LayerDefinition layer)
    {
        FilterExpression? jsonFilterExpression = null;
        var jsonParseResult = _filterExpressionService.Parse(FilterLanguage.Cql2Json, filter);
        if (!jsonParseResult.IsSuccess)
        {
            return CqlFilterResult.Failure($"{InvalidCqlFilterPrefix}: {SanitizeCqlErrorMessage(jsonParseResult.ErrorMessage ?? "Invalid filter.")}");
        }

        jsonFilterExpression = jsonParseResult.Expression;

        var queryableResult = TryBuildCombinedFilter(null, request, layer);
        if (!queryableResult.IsSuccess)
        {
            return CqlFilterResult.Failure(queryableResult.ErrorMessage!);
        }

        FilterExpression? queryableExpression = null;
        var combinedFilter = queryableResult.CombinedFilter;

        var queryableParseResult = _filterExpressionService.Parse(FilterLanguage.Cql2Text, combinedFilter);
        if (!queryableParseResult.IsSuccess)
        {
            return CqlFilterResult.Failure($"{InvalidCqlFilterPrefix}: {SanitizeCqlErrorMessage(queryableParseResult.ErrorMessage ?? "Invalid filter.")}");
        }

        queryableExpression = queryableParseResult.Expression;

        var finalExpression = CombineFilters(jsonFilterExpression, queryableExpression);
        return CqlFilterResult.Success(finalExpression, combinedFilter);
    }

    private CqlFilterResult ProcessCql2TextFilter(
        string? filter,
        HttpRequest request,
        LayerDefinition layer)
    {
        var combinedResult = TryBuildCombinedFilter(filter, request, layer);
        if (!combinedResult.IsSuccess)
        {
            return CqlFilterResult.Failure(combinedResult.ErrorMessage!);
        }

        var combinedFilter = combinedResult.CombinedFilter;
        FilterExpression? filterExpression = null;

        var parseResult = _filterExpressionService.Parse(FilterLanguage.Cql2Text, combinedFilter);
        if (!parseResult.IsSuccess)
        {
            return CqlFilterResult.Failure($"{InvalidCqlFilterPrefix}: {SanitizeCqlErrorMessage(parseResult.ErrorMessage ?? "Invalid filter.")}");
        }

        filterExpression = parseResult.Expression;

        return CqlFilterResult.Success(filterExpression, combinedFilter);
    }

    private FilterLanguageResult TryResolveFilterLanguage(string? filterLang)
    {
        var resolved = FilterLangCql2Text;

        if (string.IsNullOrWhiteSpace(filterLang))
        {
            return FilterLanguageResult.Success(resolved);
        }

        if (string.Equals(filterLang, FilterLangCql2Text, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(filterLang, FilterLangCql2Json, StringComparison.OrdinalIgnoreCase))
        {
            resolved = filterLang.Trim();
            return FilterLanguageResult.Success(resolved);
        }

        return FilterLanguageResult.Failure($"Unsupported filter language '{filterLang}'.");
    }

    private CombinedFilterResult TryBuildCombinedFilter(
        string? filter,
        HttpRequest request,
        LayerDefinition layer)
    {
        var fragments = new List<string>();

        if (!string.IsNullOrWhiteSpace(filter))
        {
            fragments.Add(filter.Trim());
        }

        foreach (var (key, values) in request.Query)
        {
            if (OgcFeaturesUtilities.AllowedQueryParameters.Items.Contains(key))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(values))
            {
                continue;
            }

            var field = layer.AttributeFields.FirstOrDefault(f => f.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (field == null)
            {
                return CombinedFilterResult.Failure($"Unknown query parameter: {key}");
            }

            if (!OgcFeaturesUtilities.IsSimpleQueryableField(field))
            {
                return CombinedFilterResult.Failure($"Field '{field.Name}' is not queryable.");
            }

            var formatResult = TryFormatQueryableValue(field, values.ToString());
            if (!formatResult.IsSuccess)
            {
                return CombinedFilterResult.Failure(formatResult.ErrorMessage!);
            }

            fragments.Add($"{field.Name} = {formatResult.Literal}");
        }

        var combinedFilter = fragments.Count == 0 ? null : string.Join(" AND ", fragments);
        return CombinedFilterResult.Success(combinedFilter);
    }

    private BboxFilterResult ProcessBboxFilter(string? bboxValue, CrsDefinition crsDefinition)
    {
        var bboxResult = TryParseBbox(bboxValue, crsDefinition);
        if (!bboxResult.IsSuccess)
        {
            return BboxFilterResult.Failure(bboxResult.ErrorMessage!);
        }

        if (bboxResult.BoundingBox == null)
        {
            return BboxFilterResult.Success(null);
        }

        var spatialFilter = CreateBboxSpatialFilter(bboxResult.BoundingBox, crsDefinition.Srid);
        return BboxFilterResult.Success(spatialFilter);
    }

    private TemporalFilterResult ProcessTemporalFilter(string? datetime, LayerDefinition layer)
    {
        var result = TryParseTemporalFilter(datetime, layer);
        if (!result.IsSuccess)
        {
            return TemporalFilterResult.Failure(result.ErrorMessage!);
        }

        return TemporalFilterResult.Success(result.TemporalFilter);
    }

    private static FilterExpression? CombineFilters(FilterExpression? left, FilterExpression? right)
    {
        if (left == null)
        {
            return right;
        }

        if (right == null)
        {
            return left;
        }

        return new BinaryExpression(left, BinaryOperator.And, right);
    }

    private static FilterExpression NormalizeFilterAxisOrder(
        FilterExpression filterExpression,
        AxisOrder axisOrder)
    {
        if (axisOrder == AxisOrder.EastNorth)
        {
            return filterExpression;
        }

        // Explicitly-tagged geometry literals (for example CQL2-JSON geometry objects with a "crs" member)
        // define their own CRS semantics
        // and should not be rewritten based on a request-level filter CRS axis order.
        return SwapAxisOrder(filterExpression, preserveExplicitGeometryCrs: true);
    }

    private static FilterExpression ApplyFilterCrs(
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
        if (HasExplicitCrs(geometry))
        {
            return geometry;
        }

        return geometry with { Srid = crsDefinition.Srid };
    }

    private static bool HasExplicitCrs(GeometryLiteral geometry)
    {
        if (string.IsNullOrWhiteSpace(geometry.OriginalFormat))
        {
            return false;
        }

        if (geometry.OriginalFormat.Contains("SRID=", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var trimmed = geometry.OriginalFormat.TrimStart();
        return trimmed.StartsWith('{') &&
               geometry.OriginalFormat.Contains("\"crs\"", StringComparison.OrdinalIgnoreCase);
    }

    private static FilterExpression SwapAxisOrder(
        FilterExpression filterExpression,
        bool preserveExplicitGeometryCrs)
    {
        return filterExpression switch
        {
            GeometryLiteral geometry => preserveExplicitGeometryCrs && HasExplicitCrs(geometry)
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
            clone.Apply(new AxisSwapFilter());
            clone.GeometryChanged();

            var (hasZ, hasM) = OgcFeaturesGeometryServices.GetHasZandM(clone);
            var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: hasZ, emitM: hasM);
            var wkb = writer.Write(clone);

            return geometry with { Wkb = wkb };
        }
        catch (Exception)
        {
            // Failed to swap geometry axis order, return original
            return geometry;
        }
    }

    private QueryableValueResult TryFormatQueryableValue(FieldDefinition field, string value)
    {
        switch (field.Type)
        {
            case FieldType.Integer:
            case FieldType.BigInteger:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                {
                    return QueryableValueResult.Failure($"Invalid value for '{field.Name}'.");
                }
                return QueryableValueResult.Success(FormattableString.Invariant($"{longValue}"));

            case FieldType.Double:
            case FieldType.Float:
                if (!double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
                {
                    return QueryableValueResult.Failure($"Invalid value for '{field.Name}'.");
                }
                return QueryableValueResult.Success(FormattableString.Invariant($"{doubleValue}"));

            case FieldType.Boolean:
                if (!bool.TryParse(value, out var boolValue))
                {
                    return QueryableValueResult.Failure($"Invalid value for '{field.Name}'.");
                }
                return QueryableValueResult.Success(boolValue ? "true" : "false");

            default:
                var escaped = value.Replace("'", "''", StringComparison.Ordinal);
                return QueryableValueResult.Success($"'{escaped}'");
        }
    }

    private BboxParseResult TryParseBbox(string? bboxValue, CrsDefinition crsDefinition)
    {
        if (string.IsNullOrWhiteSpace(bboxValue))
        {
            return BboxParseResult.Success(null);
        }

        Span<double> parts = stackalloc double[6];
        if (!bboxValue.AsSpan().TryParseDoubles(parts, ",".AsSpan(), out var partCount, out var parseError))
        {
            if (partCount == 0 && parseError == "Value list is empty.")
            {
                return BboxParseResult.Failure("Bounding box must contain 4 or 6 comma-separated values.");
            }

            return BboxParseResult.Failure("Bounding box coordinates must be valid numbers.");
        }

        if (partCount is not (4 or 6))
        {
            return BboxParseResult.Failure("Bounding box must contain 4 or 6 comma-separated values.");
        }

        var hasZ = partCount == 6;
        double minX;
        double minY;
        double maxX;
        double maxY;

        if (hasZ)
        {
            if (parts[2] > parts[5])
            {
                return BboxParseResult.Failure("Bounding box minimum Z must be less than or equal to maximum Z.");
            }

            if (crsDefinition.AxisOrder == AxisOrder.NorthEast)
            {
                minX = parts[1];
                minY = parts[0];
                maxX = parts[4];
                maxY = parts[3];
            }
            else
            {
                minX = parts[0];
                minY = parts[1];
                maxX = parts[3];
                maxY = parts[4];
            }
        }
        else if (crsDefinition.AxisOrder == AxisOrder.NorthEast)
        {
            minX = parts[1];
            minY = parts[0];
            maxX = parts[3];
            maxY = parts[2];
        }
        else
        {
            minX = parts[0];
            minY = parts[1];
            maxX = parts[2];
            maxY = parts[3];
        }

        if (minY > maxY)
        {
            return BboxParseResult.Failure("Bounding box minimum latitude must be less than or equal to maximum latitude.");
        }

        if (!crsDefinition.IsGeographic && minX > maxX)
        {
            return BboxParseResult.Failure("Bounding box minimum X must be less than or equal to maximum X for projected CRS.");
        }

        if (crsDefinition.IsGeographic)
        {
            if (minX < -180 || maxX > 180 || minY < -90 || maxY > 90)
            {
                return BboxParseResult.Failure("Bounding box coordinates are out of valid range.");
            }
        }

        var bbox = new BoundingBox(minX, minY, maxX, maxY);
        return BboxParseResult.Success(bbox);
    }

    private static SpatialFilter CreateBboxSpatialFilter(BoundingBox bbox, int srid)
    {
        var factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid);
        Geometry geometry;
        if (bbox.MinX <= bbox.MaxX)
        {
            var envelope = new Envelope(bbox.MinX, bbox.MaxX, bbox.MinY, bbox.MaxY);
            geometry = factory.ToGeometry(envelope);
        }
        else
        {
            // Handle anti-meridian crossing by splitting into two envelopes.
            var leftEnvelope = new Envelope(bbox.MinX, 180, bbox.MinY, bbox.MaxY);
            var rightEnvelope = new Envelope(-180, bbox.MaxX, bbox.MinY, bbox.MaxY);
            var leftPolygon = (Polygon)factory.ToGeometry(leftEnvelope);
            var rightPolygon = (Polygon)factory.ToGeometry(rightEnvelope);
            geometry = factory.CreateMultiPolygon(new[] { leftPolygon, rightPolygon });
        }

        var (hasZ, hasM) = OgcFeaturesGeometryServices.GetHasZandM(geometry);
        var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: srid > 0, emitZ: hasZ, emitM: hasM);
        var wkb = writer.Write(geometry);

        return new SpatialFilter
        {
            Geometry = wkb,
            Srid = srid,
            SpatialRelationship = SpatialRelationship.Intersects
        };
    }

    private TemporalParseResult TryParseTemporalFilter(string? datetime, LayerDefinition layer)
    {
        if (OgcTemporalFilterParser.TryParse(datetime, layer, out var temporalFilter, out var errorMessage))
        {
            return TemporalParseResult.Success(temporalFilter);
        }

        return TemporalParseResult.Failure(errorMessage ?? "Invalid datetime parameter.");
    }

    private static string SanitizeCqlErrorMessage(string exceptionMessage)
    {
        // Limit message length to prevent overly detailed exposure
        const int maxLength = 200;

        // Remove any potential internal details after common delimiters
        var message = exceptionMessage;

        // Remove stack trace info if accidentally included
        var stackTraceIndex = message.IndexOf("   at ", StringComparison.Ordinal);
        if (stackTraceIndex > 0)
        {
            message = message[..stackTraceIndex].Trim();
        }

        // Truncate if too long
        if (message.Length > maxLength)
        {
            message = string.Concat(message.AsSpan(0, maxLength), "...");
        }

        return message;
    }

    // Result classes
    private sealed class FilterLanguageResult
    {
        public bool IsSuccess { get; init; }
        public string? ErrorMessage { get; init; }
        public string ResolvedLanguage { get; init; } = FilterLangCql2Text;

        public static FilterLanguageResult Success(string resolvedLanguage) => new() { IsSuccess = true, ResolvedLanguage = resolvedLanguage };
        public static FilterLanguageResult Failure(string errorMessage) => new() { IsSuccess = false, ErrorMessage = errorMessage };
    }

    private sealed class CqlFilterResult
    {
        public bool IsSuccess { get; init; }
        public string? ErrorMessage { get; init; }
        public FilterExpression? FilterExpression { get; init; }
        public string? CombinedFilter { get; init; }

        public static CqlFilterResult Success(FilterExpression? filterExpression, string? combinedFilter) => new()
        {
            IsSuccess = true,
            FilterExpression = filterExpression,
            CombinedFilter = combinedFilter
        };
        public static CqlFilterResult Failure(string errorMessage) => new() { IsSuccess = false, ErrorMessage = errorMessage };
    }

    private sealed class CombinedFilterResult
    {
        public bool IsSuccess { get; init; }
        public string? ErrorMessage { get; init; }
        public string? CombinedFilter { get; init; }

        public static CombinedFilterResult Success(string? combinedFilter) => new() { IsSuccess = true, CombinedFilter = combinedFilter };
        public static CombinedFilterResult Failure(string errorMessage) => new() { IsSuccess = false, ErrorMessage = errorMessage };
    }

    private sealed class QueryableValueResult
    {
        public bool IsSuccess { get; init; }
        public string? ErrorMessage { get; init; }
        public string Literal { get; init; } = string.Empty;

        public static QueryableValueResult Success(string literal) => new() { IsSuccess = true, Literal = literal };
        public static QueryableValueResult Failure(string errorMessage) => new() { IsSuccess = false, ErrorMessage = errorMessage };
    }

    private sealed class BboxParseResult
    {
        public bool IsSuccess { get; init; }
        public string? ErrorMessage { get; init; }
        public BoundingBox? BoundingBox { get; init; }

        public static BboxParseResult Success(BoundingBox? boundingBox) => new() { IsSuccess = true, BoundingBox = boundingBox };
        public static BboxParseResult Failure(string errorMessage) => new() { IsSuccess = false, ErrorMessage = errorMessage };
    }

    private sealed class BboxFilterResult
    {
        public bool IsSuccess { get; init; }
        public string? ErrorMessage { get; init; }
        public SpatialFilter? SpatialFilter { get; init; }

        public static BboxFilterResult Success(SpatialFilter? spatialFilter) => new() { IsSuccess = true, SpatialFilter = spatialFilter };
        public static BboxFilterResult Failure(string errorMessage) => new() { IsSuccess = false, ErrorMessage = errorMessage };
    }

    private sealed class TemporalParseResult
    {
        public bool IsSuccess { get; init; }
        public string? ErrorMessage { get; init; }
        public TemporalFilter? TemporalFilter { get; init; }

        public static TemporalParseResult Success(TemporalFilter? temporalFilter) => new() { IsSuccess = true, TemporalFilter = temporalFilter };
        public static TemporalParseResult Failure(string errorMessage) => new() { IsSuccess = false, ErrorMessage = errorMessage };
    }

    private sealed class TemporalFilterResult
    {
        public bool IsSuccess { get; init; }
        public string? ErrorMessage { get; init; }
        public TemporalFilter? TemporalFilter { get; init; }

        public static TemporalFilterResult Success(TemporalFilter? temporalFilter) => new() { IsSuccess = true, TemporalFilter = temporalFilter };
        public static TemporalFilterResult Failure(string errorMessage) => new() { IsSuccess = false, ErrorMessage = errorMessage };
    }

    private static partial class FilterLog
    {
        [LoggerMessage(EventId = 5470, Level = LogLevel.Warning,
            Message = "Axis swap failed for spatial filter geometry, returning original geometry")]
        public static partial void AxisSwapFailed(ILogger logger, Exception exception);
    }
}
