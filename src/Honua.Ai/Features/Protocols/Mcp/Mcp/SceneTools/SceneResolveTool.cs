// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.SceneTools;

internal sealed class SceneResolveTool : IMcpTool
{
    public const string ToolName = "honua_scene_resolve";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<SceneResolveTool> _logger;

    public SceneResolveTool(IGeoprocessingJobService jobService, ILogger<SceneResolveTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Resolve 3D scene endpoints",
        Description = "Resolve a 3D scene to its runtime render endpoints and credential-free access-session POST affordance when protected. Never emits credentials or access tokens.",
        InputSchema = SceneToolSchemas.SceneResolveArgumentSchema,
        OutputSchema = SceneToolSchemas.SceneResolveOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Resolve 3D scene endpoints")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("SceneResolve");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Process, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        var arg = McpToolHelpers.ParseArguments(arguments, SceneToolJsonContext.Default.McpSceneResolveArgument);
        if (string.IsNullOrWhiteSpace(arg.SceneId))
        {
            throw new GeoprocessingValidationException("'sceneId' is required.");
        }

        var preferred = ParsePreferredFormats(arg.PreferredFormats);

        var catalogReader = httpContext.RequestServices.GetRequiredService<ISceneCatalogReader>();
        var entry = await catalogReader.FindActiveAsync(arg.SceneId.Trim(), cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            throw new GeoprocessingNotFoundException($"Scene '{arg.SceneId}' was not found.");
        }

        SceneToolProjection.EnsureReadable(httpContext, entry);

        var baseUrl = SceneToolProjection.ResolveBaseUrl(httpContext);
        var resolution = catalogReader.ResolveEndpoints(entry, baseUrl);
        var endpoints = OrderEndpoints(resolution.Endpoints, preferred);
        var output = SceneToolProjection.ToResolveOutput(resolution, endpoints);

        return McpToolHelpers.SuccessResult(output, SceneToolJsonContext.Default.McpSceneResolveOutput);
    }

    private static string[] ParsePreferredFormats(string[]? rawFormats)
    {
        if (rawFormats is null || rawFormats.Length == 0)
        {
            return [];
        }

        var normalized = new string[rawFormats.Length];
        for (var i = 0; i < rawFormats.Length; i++)
        {
            if (!SceneServingProjection.TryParseServingFormat(rawFormats[i], out var parsed))
            {
                throw new GeoprocessingValidationException($"Unknown preferredFormat '{rawFormats[i]}'.");
            }
            normalized[i] = SceneServingProjection.ToWireValue(parsed);
        }

        return normalized;
    }

    private static IReadOnlyList<SceneCatalogEndpoint> OrderEndpoints(
        IReadOnlyList<SceneCatalogEndpoint> endpoints,
        string[] preferredFormats)
    {
        if (preferredFormats.Length == 0 || endpoints.Count <= 1)
        {
            return endpoints;
        }

        return endpoints
            .OrderBy(endpoint =>
            {
                var idx = Array.FindIndex(
                    preferredFormats,
                    preferred => string.Equals(preferred, endpoint.EndpointFormat, StringComparison.OrdinalIgnoreCase));
                return idx < 0 ? int.MaxValue : idx;
            })
            .ToArray();
    }
}
