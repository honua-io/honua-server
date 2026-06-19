// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;

namespace Honua.Ai.Protocols.Mcp.MapTools;

/// <summary>
/// MCP tool that lists the published services and layers an AI client can act
/// on. Thin adapter over the canonical
/// <see cref="IMetadataV2GraphProvider"/> graph — the same metadata snapshot
/// that backs the GeoServices <c>/rest/services</c> directory and FeatureServer
/// layer metadata — so the discovery surface never grows its own catalog model.
/// Discovery is free (no Pro entitlement); the caller still authenticates and
/// passes the operator read grant.
/// </summary>
internal sealed class ListLayersTool : IMcpTool
{
    public const string ToolName = "honua_list_layers";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<ListLayersTool> _logger;

    public ListLayersTool(IGeoprocessingJobService jobService, ILogger<ListLayersTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Description = "List the published services and layers available to query or render, with geometry type, extent, and a short description. Use the returned serviceId/layerId with honua_query_features and honua_render_map.",
        InputSchema = MapToolSchemas.ListLayersArgumentSchema
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ListLayers");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Process, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        string? filter = null;
        if (arguments is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined })
        {
            var argument = McpToolHelpers.ParseArguments(arguments, MapToolJsonContext.Default.McpListLayersArgument);
            filter = string.IsNullOrWhiteSpace(argument.Filter) ? null : argument.Filter.Trim();
        }

        var graphProvider = httpContext.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        var layers = new List<McpLayerSummary>();
        foreach (var service in snapshot.Graph.Services)
        {
            var publications = snapshot.PublicationsForService(service.Metadata.Id);
            foreach (var publication in publications)
            {
                var resource = snapshot.ResolveResource(publication);
                if (resource is null || publication.LayerIndex is not { } layerIndex)
                {
                    continue;
                }

                var summary = ToSummary(service, publication, resource, layerIndex);
                if (filter is null || Matches(summary, service, filter))
                {
                    layers.Add(summary);
                }
            }
        }

        layers.Sort(static (a, b) =>
        {
            var byService = string.CompareOrdinal(a.ServiceName, b.ServiceName);
            return byService != 0 ? byService : a.LayerId.CompareTo(b.LayerId);
        });

        var output = new McpListLayersOutput
        {
            LayerCount = layers.Count,
            Layers = layers
        };

        return McpToolHelpers.SuccessResult(output, MapToolJsonContext.Default.McpListLayersOutput);
    }

    private static McpLayerSummary ToSummary(
        MetadataV2Service service,
        MetadataV2Publication publication,
        MetadataV2Resource resource,
        int layerIndex)
    {
        var spatial = resource.Spatial;
        var bbox = spatial?.Bbox;
        var name = !string.IsNullOrWhiteSpace(publication.TitleOverride)
            ? publication.TitleOverride!
            : (!string.IsNullOrWhiteSpace(resource.Metadata.Title)
                ? resource.Metadata.Title!
                : resource.Metadata.Name);

        return new McpLayerSummary
        {
            ServiceId = service.Metadata.Id,
            ServiceName = service.Metadata.Name,
            LayerId = layerIndex,
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
            Description = resource.Metadata.Description
        };
    }

    private static bool Matches(McpLayerSummary summary, MetadataV2Service service, string filter)
        => summary.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || summary.ServiceName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || service.Metadata.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (summary.Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
}
