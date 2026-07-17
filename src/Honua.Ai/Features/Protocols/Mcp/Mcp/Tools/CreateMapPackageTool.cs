// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Geoprocessing;
using Honua.Ai.MapGeneration;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that authors a <c>MapPackage</c> from a natural-language prompt
/// through the canonical <see cref="IMapGenerationService"/> pipeline (#1951).
/// It does NOT reimplement map composition: it adapts the tool arguments onto a
/// <see cref="MapGenerationRequest"/> and routes them through the shared
/// generation service, which grounds the prompt, runs the configured workflow
/// generation provider, and applies the generation-lenient structural validator.
/// The standard geospatial-mcp <c>create_map_package</c> composition fields
/// (<c>templateId</c>, <c>styleId</c>, <c>themeId</c>) are accepted and woven
/// into the generation prompt as explicit guidance, so a standard-conformant
/// client and a Honua-native prompt client both drive the same canonical
/// pipeline. The returned <see cref="MapGenerationResult"/> is projected
/// verbatim (status, package, rationale, clarifications, validation,
/// capabilityState) so the agent can branch on a generated package, a
/// clarification round-trip, or an unavailable capability.
/// </summary>
internal sealed class CreateMapPackageTool : IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_create_map_package";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<CreateMapPackageTool> _logger;

    /// <summary>Initializes a new instance of the <see cref="CreateMapPackageTool"/> class.</summary>
    public CreateMapPackageTool(
        IGeoprocessingJobService jobService,
        ILogger<CreateMapPackageTool> logger)
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
        Title = "Create map package",
        Description = "Author a MapPackage from a natural-language prompt through the canonical map-generation pipeline. "
            + "Accepts the geospatial-mcp composition fields (templateId, styleId, themeId, initialView) and an optional prompt; "
            + "returns a structured result: a generated package, a clarification envelope when the prompt is ambiguous, "
            + "validation findings, or a capability-unavailable state when AI map generation is not configured.",
        InputSchema = McpToolSchemas.CreateMapPackageArgumentSchema,
        OutputSchema = McpToolOutputSchemas.CreateMapPackageOutputSchema,
        // Write tool: it authors a new MapPackage draft. Not destructive (it
        // creates rather than destroys state) and not idempotent (generation is
        // AI-assisted, so a replay can produce a different package).
        Annotations = McpToolAnnotationSets.Write("Create map package", destructive: false, idempotent: false)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("CreateMapPackage");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Package, OperatorOperation.Create, cancellationToken)
            .ConfigureAwait(false);

        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpCreateMapPackageArgument);
        if (string.IsNullOrWhiteSpace(argument.Prompt))
        {
            throw new GeoprocessingValidationException(
                "Map-package authoring requires a non-empty prompt describing the map to build.");
        }

        // Resolve the generation service per-request: it is registered in the
        // host composition root after AddMcpDataAccessSurface, so a descriptor-list
        // gate at registration time would miss it. When no service is composed
        // (e.g. an isolated test container) return a structured
        // capability-unavailable result rather than failing the call.
        var service = httpContext.RequestServices.GetService<IMapGenerationService>();
        if (service is null)
        {
            return McpToolHelpers.SuccessResult(
                CapabilityUnavailable("AI map generation is not configured in this composition."),
                MapGenerationApiJsonContext.Default.MapGenerationResult);
        }

        var request = new MapGenerationRequest
        {
            Prompt = ComposePrompt(argument),
            Provider = argument.Provider,
            Model = argument.Model
        };

        var result = await service.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
        return McpToolHelpers.SuccessResult(result, MapGenerationApiJsonContext.Default.MapGenerationResult);
    }

    /// <summary>
    /// Folds the standard composition selectors into the generation prompt as
    /// explicit guidance so the prompt-driven generation service honors them.
    /// </summary>
    private static string ComposePrompt(McpCreateMapPackageArgument argument)
    {
        var builder = new StringBuilder(argument.Prompt);

        if (!string.IsNullOrWhiteSpace(argument.TemplateId))
        {
            builder.Append("\nUse the map template '").Append(argument.TemplateId).Append("'.");
        }

        if (!string.IsNullOrWhiteSpace(argument.StyleId))
        {
            builder.Append("\nApply the style '").Append(argument.StyleId).Append("'.");
        }

        if (!string.IsNullOrWhiteSpace(argument.ThemeId))
        {
            builder.Append("\nApply the theme '").Append(argument.ThemeId).Append("'.");
        }

        return builder.ToString();
    }

    private static MapGenerationResult CapabilityUnavailable(string reason) => new()
    {
        Status = "capability_unavailable",
        CapabilityState = new MapGenerationCapabilityState
        {
            Name = "map_generation",
            State = "unavailable",
            Reason = reason
        }
    };
}
