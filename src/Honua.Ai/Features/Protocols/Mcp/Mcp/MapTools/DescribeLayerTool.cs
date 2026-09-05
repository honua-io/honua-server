// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;

namespace Honua.Ai.Protocols.Mcp.MapTools;

/// <summary>
/// MCP tool that describes a single published layer: its field schema (name,
/// type, nullability, alias), feature/row count, geometry type, spatial
/// reference, and extent. Thin adapter over the same canonical
/// <see cref="IMetadataV2GraphProvider"/> graph the GeoServices FeatureServer
/// layer metadata reads (for the schema) and the shared
/// <see cref="IFeatureReader"/> the query pipeline uses (for the row count), so
/// it never grows its own catalog or counting path. Read-only; the caller still
/// authenticates and passes the operator read grant.
/// </summary>
internal sealed class DescribeLayerTool : IMcpTool
{
    public const string ToolName = "honua_describe_layer";

    private const string OutputSchemaJson = """
        {
          "type": "object",
          "required": ["serviceId", "layerId", "name", "fields", "fieldCount"],
          "properties": {
            "serviceId": { "type": "string" },
            "serviceName": { "type": "string" },
            "layerId": { "type": "integer" },
            "name": { "type": "string" },
            "type": { "type": "string" },
            "geometryType": { "type": "string" },
            "srid": { "type": ["integer", "null"] },
            "extent": {
              "type": ["object", "null"],
              "properties": {
                "minX": { "type": "number" },
                "minY": { "type": "number" },
                "maxX": { "type": "number" },
                "maxY": { "type": "number" }
              }
            },
            "description": { "type": ["string", "null"] },
            "rowCount": { "type": ["integer", "null"] },
            "fieldCount": { "type": "integer" },
            "fields": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["name", "type", "nullable"],
                "properties": {
                  "name": { "type": "string" },
                  "type": { "type": "string" },
                  "alias": { "type": ["string", "null"] },
                  "nullable": { "type": "boolean" }
                }
              }
            }
          }
        }
        """;

    private const string InputSchemaJson = """
        {
          "type": "object",
          "required": ["serviceId", "layerId"],
          "properties": {
            "serviceId": {
              "type": "string",
              "minLength": 1,
              "description": "Published service identifier or name (from honua_list_layers.serviceId)."
            },
            "layerId": {
              "type": "integer",
              "minimum": 0,
              "description": "Service-local layer index (from honua_list_layers.layerId)."
            },
            "includeRowCount": {
              "type": "boolean",
              "default": true,
              "description": "When false, skip the row-count probe and omit rowCount. Use for a fast schema-only description."
            }
          }
        }
        """;

    private static readonly JsonElement InputSchema = Parse(InputSchemaJson);
    private static readonly JsonElement OutputSchema = Parse(OutputSchemaJson);

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<DescribeLayerTool> _logger;

    public DescribeLayerTool(IGeoprocessingJobService jobService, ILogger<DescribeLayerTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Describe layer",
        Description = "Describe a published layer's field schema (name, type, nullability, alias), row count, geometry type, spatial reference, and extent. "
            + "Call this before honua_query_features to learn the exact field names/types to use in where and outFields.",
        InputSchema = InputSchema,
        OutputSchema = OutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Describe layer")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("DescribeLayer");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Process, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        var argument = McpToolHelpers.ParseArguments(arguments, MapToolJsonContext.Default.McpDescribeLayerArgument);

        var graphProvider = httpContext.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        var context = MapToolLayerResolver.Resolve(snapshot, argument.ServiceId, argument.LayerId);

        long? rowCount = null;
        if (argument.IncludeRowCount ?? true)
        {
            rowCount = await TryCountRowsAsync(httpContext, context.StorageLayerId, cancellationToken).ConfigureAwait(false);
        }

        var output = BuildOutput(context, argument.LayerId!.Value, rowCount);
        return McpToolHelpers.SuccessResult(output, MapToolJsonContext.Default.McpDescribeLayerOutput);
    }

    private async Task<long?> TryCountRowsAsync(
        HttpContext httpContext,
        int storageLayerId,
        CancellationToken cancellationToken)
    {
        var reader = httpContext.RequestServices.GetRequiredService<IFeatureReader>();
        try
        {
            return await reader.CountAsync(storageLayerId, new FeatureQuery(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Row count is best-effort enrichment; a non-countable backing store
            // (e.g. a raster resource) still returns the schema description.
            McpLog.ToolCompleted(_logger, ToolName, "row-count-unavailable");
            return null;
        }
    }

    private static McpDescribeLayerOutput BuildOutput(
        MapToolLayerContext context,
        int layerId,
        long? rowCount)
    {
        var resource = context.Resource;
        var spatial = resource.Spatial;
        var bbox = spatial?.Bbox;

        var name = !string.IsNullOrWhiteSpace(context.Publication.TitleOverride)
            ? context.Publication.TitleOverride!
            : (!string.IsNullOrWhiteSpace(resource.Metadata.Title)
                ? resource.Metadata.Title!
                : resource.Metadata.Name);

        var fields = new List<McpLayerField>(resource.SchemaFields.Count);
        foreach (var field in resource.SchemaFields)
        {
            fields.Add(new McpLayerField
            {
                Name = field.Name,
                Type = field.Type.ToString(),
                Alias = field.Alias,
                Nullable = field.Nullable
            });
        }

        return new McpDescribeLayerOutput
        {
            ServiceId = context.Service.Metadata.Id,
            ServiceName = context.Service.Metadata.Name,
            LayerId = layerId,
            Name = name,
            Type = resource.Type.ToString(),
            GeometryType = (spatial?.GeometryType ?? MetadataV2GeometryType.None).ToString(),
            Srid = spatial?.SpatialReference?.ResolveSrid(),
            Extent = bbox is null
                ? null
                : new McpExtent
                {
                    MinX = bbox.West,
                    MinY = bbox.South,
                    MaxX = bbox.East,
                    MaxY = bbox.North
                },
            Description = resource.Metadata.Description,
            RowCount = rowCount,
            FieldCount = fields.Count,
            Fields = fields
        };
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
