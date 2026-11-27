// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.SceneTools;

internal sealed class SceneGetTool : IMcpTool
{
    public const string ToolName = "honua_scene_get";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<SceneGetTool> _logger;

    public SceneGetTool(IGeoprocessingJobService jobService, ILogger<SceneGetTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Get 3D scene metadata",
        Description = "Retrieve canonical metadata, servingFormat, contentKind, bounds, authentication requirements, and discovery links for a single 3D scene.",
        InputSchema = SceneToolSchemas.SceneGetArgumentSchema,
        OutputSchema = SceneToolSchemas.SceneGetOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Get 3D scene metadata")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("SceneGet");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Process, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        var arg = McpToolHelpers.ParseArguments(arguments, SceneToolJsonContext.Default.McpSceneGetArgument);
        if (string.IsNullOrWhiteSpace(arg.SceneId))
        {
            throw new GeoprocessingValidationException("'sceneId' is required.");
        }

        var catalogReader = httpContext.RequestServices.GetRequiredService<ISceneCatalogReader>();
        var entry = await catalogReader.FindActiveAsync(arg.SceneId.Trim(), cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            throw new GeoprocessingNotFoundException($"Scene '{arg.SceneId}' was not found.");
        }

        SceneToolProjection.EnsureReadable(httpContext, entry);

        var baseUrl = SceneToolProjection.ResolveBaseUrl(httpContext);
        var resolution = catalogReader.ResolveEndpoints(entry, baseUrl);
        var links = catalogReader.BuildLinks(entry, baseUrl);
        var output = SceneToolProjection.ToMetadata(entry, resolution, links);

        return McpToolHelpers.SuccessUntrustedResult(output, SceneToolJsonContext.Default.McpSceneMetadataOutput);
    }
}
