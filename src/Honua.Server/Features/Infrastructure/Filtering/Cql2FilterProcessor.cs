// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Geometries;
using Honua.Server.Features.Infrastructure.Validation;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Infrastructure.Filtering;

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

        public static ProcessingResult Success(SqlFragment? sqlFilter)
            => new()
            {
                IsSuccess = true,
                SqlFilter = sqlFilter
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
        CancellationToken cancellationToken)
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

        foreach (var explicitGeometrySrid in FilterGeometryCrsValidator.GetExplicitGeometrySrids(parsedExpression))
        {
            if (!await _crsRegistry.IsSridSupportedAsync(explicitGeometrySrid, cancellationToken).ConfigureAwait(false))
            {
                return ProcessingResult.Failure($"Unsupported explicit geometry CRS 'EPSG:{explicitGeometrySrid}'.");
            }
        }

        var translationResult = _filterExpressionService.Translate(parsedExpression, resource);
        return translationResult.IsSuccess
            ? ProcessingResult.Success(translationResult.SqlFilter)
            : ProcessingResult.Failure(translationResult.ErrorMessage ?? "Invalid filter expression.");
    }

    /// <summary>
    /// V2 overload of <see cref="ProcessFilterAsync(LayerDefinition, JsonElement?, string?, string?, bool, CancellationToken)"/>
    /// that drives filter translation off a <see cref="MetadataV2Resource"/> instead of a v1 layer.
    /// Mirrors the v1 path step-for-step; the only difference is the final
    /// <see cref="IFilterExpressionService.Translate(FilterExpression?, MetadataV2Resource)"/> call.
    /// </summary>
    public async Task<ProcessingResult> ProcessFilterAsync(
        MetadataV2Resource resource,
        JsonElement? filter,
        string? filterLang,
        string? filterCrs,
        bool defaultFilterLangIsText,
        CancellationToken cancellationToken)
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

        foreach (var explicitGeometrySrid in FilterGeometryCrsValidator.GetExplicitGeometrySrids(parsedExpression))
        {
            if (!await _crsRegistry.IsSridSupportedAsync(explicitGeometrySrid, cancellationToken).ConfigureAwait(false))
            {
                return ProcessingResult.Failure($"Unsupported explicit geometry CRS 'EPSG:{explicitGeometrySrid}'.");
            }
        }

        var translationResult = _filterExpressionService.Translate(parsedExpression, resource);
        return translationResult.IsSuccess
            ? ProcessingResult.Success(translationResult.SqlFilter)
            : ProcessingResult.Failure(translationResult.ErrorMessage ?? "Invalid filter expression.");
    }

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

            var (hasZ, hasM) = Honua.Server.Features.Infrastructure.Services.GeometryService.DetectZMFromGeometry(clone);
            var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: hasZ, emitM: hasM);
            var wkb = writer.Write(clone);

            return geometry with { Wkb = wkb };
        }
        catch (Exception)
        {
            return geometry;
        }
    }
}
