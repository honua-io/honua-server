// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Geoprocessing;
using Honua.Ai.AppGeneration;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that authors an <c>AppPackage</c> (studio-app/v1) from a
/// natural-language prompt through the canonical <see cref="IAppGenerationService"/>
/// pipeline (#1951). Like <see cref="CreateMapPackageTool"/> it adapts the tool
/// arguments onto an <see cref="AppGenerationRequest"/> rather than
/// reimplementing app composition. The standard geospatial-mcp
/// <c>create_app_package</c> fields (<c>templateId</c>, <c>targetSdk</c>,
/// <c>mapPackageId</c>, <c>boundArtifactIds</c>) are accepted and woven into the
/// generation prompt as explicit guidance. The returned
/// <see cref="AppGenerationResult"/> is projected verbatim so the agent can
/// branch on a generated package, a clarification round-trip, or an unavailable
/// capability.
/// </summary>
internal sealed class CreateAppPackageTool : IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_create_app_package";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<CreateAppPackageTool> _logger;

    /// <summary>Initializes a new instance of the <see cref="CreateAppPackageTool"/> class.</summary>
    public CreateAppPackageTool(
        IGeoprocessingJobService jobService,
        ILogger<CreateAppPackageTool> logger)
    {
        _jobService = jobService;
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
        Description = "Author an SDK-native AppPackage from a natural-language prompt through the canonical app-generation pipeline. "
            + "Accepts the geospatial-mcp composition fields (templateId, targetSdk, mapPackageId, boundArtifactIds) and an optional prompt; "
            + "returns a structured result: a generated package, a clarification envelope when the prompt is ambiguous, "
            + "validation findings, or a capability-unavailable state when AI app generation is not configured.",
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
        if (string.IsNullOrWhiteSpace(argument.Prompt))
        {
            throw new GeoprocessingValidationException(
                "App-package authoring requires a non-empty prompt describing the application to build.");
        }

        var service = httpContext.RequestServices.GetService<IAppGenerationService>();
        if (service is null)
        {
            return McpToolHelpers.SuccessResult(
                CapabilityUnavailable("AI app generation is not configured in this composition."),
                AppGenerationApiJsonContext.Default.AppGenerationResult);
        }

        var request = new AppGenerationRequest
        {
            Prompt = ComposePrompt(argument),
            Provider = argument.Provider,
            Model = argument.Model
        };

        var result = await service.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
        return McpToolHelpers.SuccessResult(result, AppGenerationApiJsonContext.Default.AppGenerationResult);
    }

    /// <summary>
    /// Folds the standard composition selectors into the generation prompt as
    /// explicit guidance so the prompt-driven generation service honors them.
    /// </summary>
    private static string ComposePrompt(McpCreateAppPackageArgument argument)
    {
        var builder = new StringBuilder(argument.Prompt);

        if (!string.IsNullOrWhiteSpace(argument.TemplateId))
        {
            builder.Append("\nUse the app template '").Append(argument.TemplateId).Append("'.");
        }

        if (!string.IsNullOrWhiteSpace(argument.TargetSdk))
        {
            builder.Append("\nTarget the SDK '").Append(argument.TargetSdk).Append("'.");
        }

        if (!string.IsNullOrWhiteSpace(argument.MapPackageId))
        {
            builder.Append("\nBind the map package '").Append(argument.MapPackageId).Append("'.");
        }

        if (argument.BoundArtifactIds is { Count: > 0 })
        {
            builder.Append("\nBind the artifacts: ").Append(string.Join(", ", argument.BoundArtifactIds)).Append('.');
        }

        return builder.ToString();
    }

    private static AppGenerationResult CapabilityUnavailable(string reason) => new()
    {
        Status = "capability_unavailable",
        CapabilityState = new AppGenerationCapabilityState
        {
            Name = "app_generation",
            State = "unavailable",
            Reason = reason
        }
    };
}
