// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Studio.Drafts;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal sealed class CreateAppTool : IMcpTool
{
    public const string ToolName = "honua_create_app";

    private readonly IGeoprocessingJobService _jobService;
    private readonly AppDraftFactory _drafts;
    private readonly IPackageDraftStore _draftStore;
    private readonly ILogger<CreateAppTool> _logger;

    public CreateAppTool(
        IGeoprocessingJobService jobService,
        AppDraftFactory drafts,
        IPackageDraftStore draftStore,
        ILogger<CreateAppTool> logger)
    {
        _jobService = jobService;
        _drafts = drafts;
        _draftStore = draftStore;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Create App",
        Description = "Create an SDK-native App draft deterministically from structured input "
            + "(templateId, targetSdk, mapId, boundArtifactIds, runtimeConfig). Performs no model inference and "
            + "takes no natural-language input; returns a draft App with a stable app_… identifier, its "
            + "honua://apps/{id} resource URI, and a closed-by-default share policy.",
        InputSchema = McpToolSchemas.CreateAppArgumentSchema,
        OutputSchema = McpToolOutputSchemas.CreateAppOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Create App", destructive: false, idempotent: false)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("CreateApp");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Package, OperatorOperation.Create, cancellationToken)
            .ConfigureAwait(false);
        var ownerId = PackageDraftCaller.RequireOwnerId(principal);

        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpCreateAppArgument);

        var result = _drafts.CreateDraft(new AppDraftRequest
        {
            TemplateId = argument.TemplateId,
            TargetSdk = argument.TargetSdk,
            MapId = argument.MapId,
            BoundArtifactIds = argument.BoundArtifactIds ?? [],
            RuntimeConfig = argument.RuntimeConfig
        });

        if (result.App is null)
        {
            throw new GeoprocessingValidationException(McpPackageDraftProjection.DescribeErrors(
                "App draft input is invalid", result.Errors));
        }

        // Persist before returning, for the same reason CreateMapTool does:
        // the honua://apps/{id} URI in this response is only honourable if
        // AppResource can find the draft without a deployment.
        await _draftStore.SaveAppDraftAsync(result.App, ownerId, cancellationToken).ConfigureAwait(false);

        var output = new McpAppDraftOutput
        {
            AppId = result.App.AppId,
            ResourceUri = McpResourceUris.AppUri(result.App.AppId),
            App = JsonSerializer.SerializeToElement(result.App, PackagingJsonContext.Default.App),
            Warnings = McpPackageDraftProjection.MapFindings(result.Warnings)
        };

        return McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpAppDraftOutput);
    }
}
