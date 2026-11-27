// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;

namespace Honua.Ai.Protocols.Mcp.SceneTools;

internal sealed class SceneListTool : IMcpTool
{
    public const string ToolName = "honua_scene_list";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<SceneListTool> _logger;

    public SceneListTool(IGeoprocessingJobService jobService, ILogger<SceneListTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "List 3D scenes",
        Description = "List active 3D scenes from the Honua scene catalog with separated servingFormat and contentKind metadata, bounds, and authentication posture. Use honua_scene_get for full metadata and honua_scene_resolve for runtime endpoint selection.",
        InputSchema = SceneToolSchemas.SceneListArgumentSchema,
        OutputSchema = SceneToolSchemas.SceneListOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("List 3D scenes")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("SceneList");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Process, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        var (servingFormat, contentKind, filter, limit, offset) = ParseArguments(arguments);

        var catalogReader = httpContext.RequestServices.GetRequiredService<ISceneCatalogReader>();
        var entries = await catalogReader.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        var baseUrl = SceneToolProjection.ResolveBaseUrl(httpContext);

        var matched = new List<McpSceneSummary>();
        foreach (var entry in entries)
        {
            if (!SceneReadAccess.IsReadable(httpContext, entry.AccessPolicy))
            {
                continue;
            }

            if (servingFormat is { } fmt && entry.ServingFormat != fmt)
            {
                continue;
            }

            if (contentKind is { } kind && entry.ContentKind != kind)
            {
                continue;
            }

            if (filter is not null && !MatchesFilter(entry, filter))
            {
                continue;
            }

            var resolution = catalogReader.ResolveEndpoints(entry, baseUrl);
            matched.Add(SceneToolProjection.ToSummary(entry, resolution));
        }

        var totalCount = matched.Count;
        var page = offset >= totalCount
            ? []
            : matched.GetRange(offset, Math.Min(limit, totalCount - offset));
        var hasMore = offset + page.Count < totalCount;

        var output = new McpSceneListOutput
        {
            SceneCount = page.Count,
            TotalCount = totalCount,
            Offset = offset,
            HasMore = hasMore,
            NextOffset = hasMore ? offset + page.Count : null,
            Scenes = page
        };

        return McpToolHelpers.SuccessUntrustedResult(output, SceneToolJsonContext.Default.McpSceneListOutput);
    }

    private static (SceneServingFormat? Format, SceneContentKind? Kind, string? Filter, int Limit, int Offset)
        ParseArguments(JsonElement? arguments)
    {
        SceneServingFormat? servingFormat = null;
        SceneContentKind? contentKind = null;
        string? filter = null;
        var limit = SceneToolSchemas.DefaultSceneLimit;
        var offset = 0;

        if (arguments is not { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined })
        {
            return (servingFormat, contentKind, filter, limit, offset);
        }

        var arg = McpToolHelpers.ParseArguments(arguments, SceneToolJsonContext.Default.McpSceneListArgument);
        if (!string.IsNullOrWhiteSpace(arg.ServingFormat))
        {
            if (!SceneServingProjection.TryParseServingFormat(arg.ServingFormat, out var parsedFormat))
            {
                throw new GeoprocessingValidationException($"Unknown servingFormat '{arg.ServingFormat}'.");
            }
            servingFormat = parsedFormat;
        }

        if (!string.IsNullOrWhiteSpace(arg.ContentKind))
        {
            if (!SceneServingProjection.TryParseContentKind(arg.ContentKind, out var parsedKind))
            {
                throw new GeoprocessingValidationException($"Unknown contentKind '{arg.ContentKind}'.");
            }
            contentKind = parsedKind;
        }

        filter = string.IsNullOrWhiteSpace(arg.Filter) ? null : arg.Filter.Trim();
        if (arg.Limit is { } requestedLimit)
        {
            if (requestedLimit < 1)
            {
                throw new GeoprocessingValidationException("'limit' must be a positive integer.");
            }
            limit = Math.Min(requestedLimit, SceneToolSchemas.MaxSceneLimit);
        }

        if (arg.Offset is { } requestedOffset)
        {
            if (requestedOffset < 0)
            {
                throw new GeoprocessingValidationException("'offset' must be a non-negative integer.");
            }
            offset = requestedOffset;
        }

        return (servingFormat, contentKind, filter, limit, offset);
    }

    private static bool MatchesFilter(SceneCatalogEntry entry, string filter)
        => entry.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (entry.Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
}
