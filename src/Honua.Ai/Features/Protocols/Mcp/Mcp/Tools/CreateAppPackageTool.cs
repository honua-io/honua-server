// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Studio.Drafts;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that creates an <c>AppPackage</c> draft deterministically from the
/// structured geospatial-mcp <c>create_app_package</c> fields (ADR-0076,
/// honua-server#3255).
/// </summary>
/// <remarks>
/// The app-side counterpart to <see cref="CreateMapPackageTool"/>: it performs no
/// model inference, requires no prompt, and delegates every validation and
/// composition rule to the shared <see cref="IAppPackageDraftFactory"/> in
/// <c>Honua.Core</c>. It starts honoring <c>runtimeConfig</c>, which the standard
/// publishes and the previous implementation never parsed, and it inherits the
/// factory's closed-by-default sharing posture.
/// </remarks>
internal sealed class CreateAppPackageTool : IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_create_app_package";

    private readonly IGeoprocessingJobService _jobService;
    private readonly IAppPackageDraftFactory _drafts;
    private readonly IPackageDraftStore _draftStore;
    private readonly ILogger<CreateAppPackageTool> _logger;

    /// <summary>Initializes a new instance of the <see cref="CreateAppPackageTool"/> class.</summary>
    public CreateAppPackageTool(
        IGeoprocessingJobService jobService,
        IAppPackageDraftFactory drafts,
        IPackageDraftStore draftStore,
        ILogger<CreateAppPackageTool> logger)
    {
        _jobService = jobService;
        _drafts = drafts;
        _draftStore = draftStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Create app package",
        Description = "Create an SDK-native AppPackage draft deterministically from structured composition input "
            + "(templateId, targetSdk, mapPackageId, boundArtifactIds, runtimeConfig). Performs no model inference and "
            + "takes no natural-language input; returns a draft package with a stable app_… identifier, its "
            + "honua://app-packages/{id} resource URI, and a closed-by-default share policy.",
        InputSchema = McpToolSchemas.CreateAppPackageArgumentSchema,
        OutputSchema = McpToolOutputSchemas.CreateAppPackageOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Create app package", destructive: false, idempotent: false)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("CreateAppPackage");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Package, OperatorOperation.Create, cancellationToken)
            .ConfigureAwait(false);

        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpCreateAppPackageArgument);

        var result = _drafts.CreateDraft(new AppPackageDraftRequest
        {
            TemplateId = argument.TemplateId,
            TargetSdk = argument.TargetSdk,
            MapPackageId = argument.MapPackageId,
            BoundArtifactIds = argument.BoundArtifactIds ?? [],
            RuntimeConfig = argument.RuntimeConfig
        });

        if (result.Package is null)
        {
            throw new GeoprocessingValidationException(McpPackageDraftProjection.DescribeErrors(
                "App-package draft input is invalid", result.Errors));
        }

        // Persist before returning, for the same reason CreateMapPackageTool does:
        // the honua://app-packages/{id} URI in this response is only honourable if
        // AppPackageResource can find the draft without a deployment (honua-server#3262).
        await _draftStore.SaveAppDraftAsync(result.Package, cancellationToken).ConfigureAwait(false);

        var output = new McpPackageDraftOutput
        {
            PackageId = result.Package.AppPackageId,
            ResourceUri = McpResourceUris.AppPackageUri(result.Package.AppPackageId),
            Package = JsonSerializer.SerializeToElement(result.Package, PackagingJsonContext.Default.AppPackage),
            Warnings = McpPackageDraftProjection.MapFindings(result.Warnings)
        };

        return McpToolHelpers.SuccessResult(output, McpJsonContext.Default.McpPackageDraftOutput);
    }
}
