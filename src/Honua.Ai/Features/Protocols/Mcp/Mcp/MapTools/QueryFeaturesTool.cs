// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;

namespace Honua.Ai.Protocols.Mcp.MapTools;

/// <summary>
/// MCP tool that queries features from a published layer and returns GeoJSON.
/// Thin adapter over the canonical feature-query pipeline: it resolves the
/// layer through the same Metadata v2 snapshot the FeatureServer uses, parses
/// the optional attribute filter through the shared
/// <see cref="IFilterExpressionService"/> (FeatureServer's <c>where</c> path),
/// executes through <see cref="IFeatureReader"/>, and serializes geometry via
/// the shared <see cref="IGeometryService"/> WKB→GeoJSON converter. No query,
/// filter, or geometry logic is reimplemented here.
/// </summary>
internal sealed class QueryFeaturesTool : IMcpTool
{
    public const string ToolName = "honua_query_features";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<QueryFeaturesTool> _logger;

    public QueryFeaturesTool(IGeoprocessingJobService jobService, ILogger<QueryFeaturesTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Description = "Query features from a published layer (by serviceId/layerId) with an optional attribute WHERE clause, bbox, outFields, and result limit. Returns a GeoJSON FeatureCollection.",
        InputSchema = MapToolSchemas.QueryFeaturesArgumentSchema
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("QueryFeatures");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Process, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        var argument = McpToolHelpers.ParseArguments(arguments, MapToolJsonContext.Default.McpQueryFeaturesArgument);

        var graphProvider = httpContext.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var layer = MapToolLayerResolver.Resolve(snapshot, argument.ServiceId, argument.LayerId);

        var limit = ResolveLimit(argument.Limit);
        var outSrid = argument.OutSrid ?? 4326;
        if (outSrid <= 0)
        {
            throw new GeoprocessingValidationException("'outSrid' must be a positive SRID/WKID.");
        }

        var geometryService = httpContext.RequestServices.GetRequiredService<IGeometryService>();
        var filterService = httpContext.RequestServices.GetRequiredService<IFilterExpressionService>();

        var query = new FeatureQuery
        {
            OutFields = ToOutFields(argument.OutFields),
            Limit = limit,
            OutputSrid = outSrid,
            SqlFilter = BuildAttributeFilter(filterService, argument.Where, layer),
            SpatialFilter = BuildBboxFilter(geometryService, argument.Bbox, argument.BboxSrid)
        };

        var reader = httpContext.RequestServices.GetRequiredService<IFeatureReader>();
        var result = await reader.QueryAsync(layer.StorageLayerId, query, cancellationToken).ConfigureAwait(false);

        var features = new List<JsonNode>(result.Items.Length);
        foreach (var feature in result.Items)
        {
            features.Add(ToGeoJsonFeature(feature, geometryService));
        }

        var output = new McpQueryFeaturesOutput
        {
            ServiceId = layer.Service.Metadata.Id,
            LayerId = argument.LayerId!.Value,
            ReturnedCount = features.Count,
            Limit = limit,
            ExceededTransferLimit = result.HasMoreResults,
            GeoJson = new McpGeoJsonFeatureCollection { Features = features }
        };

        return McpToolHelpers.SuccessResult(output, MapToolJsonContext.Default.McpQueryFeaturesOutput);
    }

    private static int ResolveLimit(int? requested)
    {
        var limit = requested ?? MapToolSchemas.DefaultFeatureLimit;
        if (limit < 1)
        {
            throw new GeoprocessingValidationException("'limit' must be a positive integer.");
        }

        return Math.Min(limit, MapToolSchemas.MaxFeatureLimit);
    }

    private static ImmutableArray<string>? ToOutFields(IReadOnlyList<string>? outFields)
    {
        if (outFields is null || outFields.Count == 0)
        {
            return null;
        }

        var builder = ImmutableArray.CreateBuilder<string>(outFields.Count);
        foreach (var field in outFields)
        {
            if (!string.IsNullOrWhiteSpace(field))
            {
                builder.Add(field.Trim());
            }
        }

        return builder.Count == 0 ? null : builder.ToImmutable();
    }

    private static SqlFragment? BuildAttributeFilter(
        IFilterExpressionService filterService,
        string? where,
        in MapToolLayerContext layer)
    {
        if (string.IsNullOrWhiteSpace(where))
        {
            return null;
        }

        var parse = filterService.Parse(FilterLanguage.ArcGisSql, where);
        if (!parse.IsSuccess)
        {
            throw new GeoprocessingValidationException(
                $"Invalid 'where' clause: {parse.ErrorMessage ?? "could not be parsed."}");
        }

        var translation = filterService.Translate(parse.Expression, layer.Resource);
        if (!translation.IsSuccess)
        {
            throw new GeoprocessingValidationException(
                $"Invalid 'where' clause: {translation.ErrorMessage ?? "could not be translated."}");
        }

        return translation.SqlFilter;
    }

    private static SpatialFilter? BuildBboxFilter(
        IGeometryService geometryService,
        IReadOnlyList<double>? bbox,
        int? bboxSrid)
    {
        if (bbox is null)
        {
            return null;
        }

        if (bbox.Count != 4)
        {
            throw new GeoprocessingValidationException("'bbox' must contain exactly four numbers: [minX, minY, maxX, maxY].");
        }

        var minX = bbox[0];
        var minY = bbox[1];
        var maxX = bbox[2];
        var maxY = bbox[3];
        if (maxX < minX || maxY < minY)
        {
            throw new GeoprocessingValidationException("'bbox' max ordinates must be greater than or equal to the min ordinates.");
        }

        var srid = bboxSrid ?? 4326;
        var wkt = string.Format(
            CultureInfo.InvariantCulture,
            "POLYGON(({0} {1},{2} {1},{2} {3},{0} {3},{0} {1}))",
            minX,
            minY,
            maxX,
            maxY);

        var wkb = geometryService.ConvertWktToWkb(wkt, srid)
            ?? throw new GeoprocessingValidationException("'bbox' could not be converted to a geometry envelope.");

        return SpatialFilter.Create(
            wkb,
            SpatialRelationship.EnvelopeIntersects,
            srid: srid,
            isSimpleEnvelope: true,
            allowEnvelopeOnly: true,
            envelopeMinX: minX,
            envelopeMinY: minY,
            envelopeMaxX: maxX,
            envelopeMaxY: maxY);
    }

    private static JsonObject ToGeoJsonFeature(Feature feature, IGeometryService geometryService)
    {
        var geometryJson = geometryService.ConvertWkbToGeoJson(feature.Geometry);
        JsonNode? geometryNode = geometryJson is null ? null : JsonNode.Parse(geometryJson);

        var properties = new JsonObject();
        foreach (var pair in feature.Attributes)
        {
            properties[pair.Key] = ToJsonValue(pair.Value);
        }

        return new JsonObject
        {
            ["type"] = "Feature",
            ["id"] = feature.Id,
            ["geometry"] = geometryNode,
            ["properties"] = properties
        };
    }

    private static JsonValue? ToJsonValue(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        short sh => JsonValue.Create(sh),
        byte bt => JsonValue.Create(bt),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create(f),
        decimal m => JsonValue.Create(m),
        DateTime dt => JsonValue.Create(dt.ToString("O", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => JsonValue.Create(dto.ToString("O", CultureInfo.InvariantCulture)),
        Guid g => JsonValue.Create(g.ToString()),
        _ => JsonValue.Create(value.ToString())
    };
}
