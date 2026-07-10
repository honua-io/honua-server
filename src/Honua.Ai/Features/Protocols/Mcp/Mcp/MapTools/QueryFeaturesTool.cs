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
        Title = "Query features",
        Description = "Query features from a published layer (by serviceId/layerId) with an optional attribute WHERE clause, bbox, outFields, and result limit. Returns a GeoJSON FeatureCollection. "
            + "Paging: results are capped at 'limit' (default 100, max 1000). When the response reports exceededTransferLimit=true there are more matching features; page through them mechanically by re-issuing the SAME query with resultOffset set to the returned nextOffset, repeating until exceededTransferLimit=false. "
            + "Set returnCountOnly=true to get just the matching {count} (no features) for a cheap cardinality check, and returnGeometry=false to return attribute-only rows (geometry omitted) when scanning attributes. "
            + "Geometry coordinates are rounded to geometryPrecision decimal places (default 6, ~0.1 m) to keep responses compact; pass a higher value for finer precision or a negative value for full precision. "
            + "The full FeatureCollection is always in structuredContent; for large pages the text block is a one-line summary (counts, ids, paging hint), not a duplicate of the data.",
        InputSchema = MapToolSchemas.QueryFeaturesArgumentSchema,
        OutputSchema = McpToolOutputSchemas.QueryFeaturesOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Query features")
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
        var offset = ResolveOffset(argument.ResultOffset, argument.Cursor);
        var returnGeometry = argument.ReturnGeometry ?? true;
        var returnCountOnly = argument.ReturnCountOnly ?? false;
        var geometryPrecision = argument.GeometryPrecision ?? MapToolSchemas.DefaultGeometryPrecision;
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
            ResultOffset = offset,
            OutputSrid = outSrid,
            SqlFilter = BuildAttributeFilter(filterService, argument.Where, layer),
            SpatialFilter = BuildBboxFilter(geometryService, argument.Bbox, argument.BboxSrid)
        };

        var reader = httpContext.RequestServices.GetRequiredService<IFeatureReader>();

        // returnCountOnly: adapt to the canonical count seam and return {count}
        // with no features (a cheap cardinality check that never buffers geometry).
        if (returnCountOnly)
        {
            var count = await reader.CountAsync(layer.StorageLayerId, query, cancellationToken).ConfigureAwait(false);
            var countOutput = new McpQueryFeaturesOutput
            {
                ServiceId = layer.Service.Metadata.Id,
                LayerId = argument.LayerId!.Value,
                ReturnedCount = 0,
                Limit = limit,
                ResultOffset = offset,
                ExceededTransferLimit = false,
                Count = count
            };

            return McpToolHelpers.SuccessResult(
                countOutput,
                MapToolJsonContext.Default.McpQueryFeaturesOutput,
                SummarizeQueryResult);
        }

        var result = await reader.QueryAsync(layer.StorageLayerId, query, cancellationToken).ConfigureAwait(false);

        var features = new List<JsonNode>(result.Items.Length);
        var featureRows = new List<McpQueryFeatureRow>(result.Items.Length);
        foreach (var feature in result.Items)
        {
            features.Add(ToGeoJsonFeature(feature, geometryService, returnGeometry, geometryPrecision));
            featureRows.Add(ToFeatureRow(feature));
        }

        var nextOffset = result.HasMoreResults ? offset + features.Count : (int?)null;
        var output = new McpQueryFeaturesOutput
        {
            ServiceId = layer.Service.Metadata.Id,
            LayerId = argument.LayerId!.Value,
            ReturnedCount = features.Count,
            Limit = limit,
            ResultOffset = offset,
            ExceededTransferLimit = result.HasMoreResults,
            // When more results remain, hand the agent the exact offset to page
            // mechanically: the next page starts after everything returned so far.
            NextOffset = nextOffset,
            NextCursor = nextOffset?.ToString(CultureInfo.InvariantCulture),
            GeoJson = new McpGeoJsonFeatureCollection { Features = features },
            Features = featureRows
        };

        return McpToolHelpers.SuccessResult(
            output,
            MapToolJsonContext.Default.McpQueryFeaturesOutput,
            SummarizeQueryResult);
    }

    /// <summary>
    /// One-line, information-bearing summary of a query result for the <c>text</c>
    /// content block: feature count, layer address, offset, the paging next-step
    /// hint, and whether geometry was included — never a duplicate of the GeoJSON
    /// payload (which always rides in <c>structuredContent</c>).
    /// </summary>
    private static string SummarizeQueryResult(McpQueryFeaturesOutput output)
    {
        if (output.Count is { } count)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Matched {0} feature(s) in {1}/{2} (count only, no geometry). Count in structuredContent.count.",
                count,
                output.ServiceId,
                output.LayerId);
        }

        var geometryNote = output.GeoJson is { Features.Count: > 0 } collection
            && collection.Features[0] is JsonObject first
            && first.TryGetPropertyValue("geometry", out var geometryNode)
            && geometryNode is null
                ? "geometry omitted"
                : "geometry included";

        var pagingNote = output.ExceededTransferLimit && output.NextOffset is { } next
            ? string.Format(CultureInfo.InvariantCulture, "more available: re-query with resultOffset={0}", next)
            : "last page";

        return string.Format(
            CultureInfo.InvariantCulture,
            "Returned {0} feature(s) from {1}/{2} at offset {3} ({4}, {5}). GeoJSON FeatureCollection in structuredContent.geojson.",
            output.ReturnedCount,
            output.ServiceId,
            output.LayerId,
            output.ResultOffset,
            pagingNote,
            geometryNote);
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

    private static int ResolveOffset(int? requested, string? cursor)
    {
        if (requested is { } explicitOffset)
        {
            return ValidateOffset(explicitOffset);
        }

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        if (int.TryParse(cursor.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return ValidateOffset(parsed);
        }

        throw new GeoprocessingValidationException(
            "'cursor' must be the nextCursor value returned by a previous honua_query_features page.");
    }

    private static int ValidateOffset(int offset)
    {
        if (offset < 0)
        {
            throw new GeoprocessingValidationException("'resultOffset' must be zero or a positive integer.");
        }

        return offset;
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

    private static JsonObject ToGeoJsonFeature(
        Feature feature,
        IGeometryService geometryService,
        bool returnGeometry,
        int geometryPrecision)
    {
        JsonNode? geometryNode = null;
        if (returnGeometry)
        {
            var geometryJson = geometryService.ConvertWkbToGeoJson(feature.Geometry);
            geometryNode = geometryJson is null ? null : JsonNode.Parse(geometryJson);
            if (geometryNode is not null)
            {
                QuantizeCoordinates(geometryNode, geometryPrecision);
            }
        }

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

    private static McpQueryFeatureRow ToFeatureRow(Feature feature)
    {
        var attributes = new JsonObject();
        foreach (var pair in feature.Attributes)
        {
            attributes[pair.Key] = ToJsonValue(pair.Value);
        }

        return new McpQueryFeatureRow
        {
            Id = feature.Id,
            Attributes = attributes
        };
    }

    /// <summary>
    /// Rounds every coordinate in a GeoJSON geometry node to
    /// <paramref name="precision"/> decimal places in place. Walks the geometry
    /// tree uniformly so Point/LineString/Polygon/Multi*/GeometryCollection
    /// coordinates (and any <c>bbox</c>) are all quantized. A negative or
    /// out-of-range precision leaves coordinates untouched (full precision).
    /// </summary>
    private static void QuantizeCoordinates(JsonNode node, int precision)
    {
        if (precision < 0 || precision > MapToolSchemas.MaxGeometryPrecision)
        {
            return;
        }

        switch (node)
        {
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var element = array[i];
                    if (element is JsonValue value && value.TryGetValue<double>(out var coordinate))
                    {
                        array[i] = JsonValue.Create(Math.Round(coordinate, precision, MidpointRounding.AwayFromZero));
                    }
                    else if (element is not null)
                    {
                        QuantizeCoordinates(element, precision);
                    }
                }

                break;
            case JsonObject obj:
                foreach (var property in obj)
                {
                    if (property.Value is not null)
                    {
                        QuantizeCoordinates(property.Value, precision);
                    }
                }

                break;
        }
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
