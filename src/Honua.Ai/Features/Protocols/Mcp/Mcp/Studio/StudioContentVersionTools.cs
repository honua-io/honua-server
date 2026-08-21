// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Studio.Domain;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.Studio;

/// <summary>
/// Saves the current generation of a mutable Studio draft as an immutable,
/// durable content version. This is the lifecycle boundary agents must cross
/// before asking a human to publish or before reopening an earlier state.
/// </summary>
internal sealed class SaveStudioVersionTool : StudioDraftToolBase, IMcpTool
{
    public const string ToolName = "honua_studio_save_version";

    private readonly ILogger<SaveStudioVersionTool> _typedLogger;

    public SaveStudioVersionTool(IGeoprocessingJobService jobService, ILogger<SaveStudioVersionTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Save Studio version",
        Description =
            "Save exactly the requested generation of a Studio map, app, dashboard, or other package draft as an immutable durable content version. "
            + "Capture itemId and versionId from the result; publication and reopen operations address that immutable pair, never the draft id.",
        InputSchema = StudioMcpSchemas.SaveVersionArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioContentVersionOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Save Studio version", destructive: false, idempotent: false)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioSaveVersion");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);
        var argument = McpToolHelpers.ParseArguments(
            arguments,
            StudioMcpJsonContext.Default.McpStudioSaveVersionArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = RequireGeneration(argument.Generation);
        if (argument.ChangeNote?.Length > StudioMcpSchemas.MaxNoteLength)
        {
            throw new GeoprocessingValidationException(
                $"'changeNote' must be at most {StudioMcpSchemas.MaxNoteLength} characters.");
        }

        var draft = await RequireDraftAsync(lifecycleService, draftId, cancellationToken).ConfigureAwait(false);
        try
        {
            var version = await lifecycleService.SaveDraftAsVersionAsync(
                draftId,
                argument.ChangeNote,
                ActorIdFor(principal),
                expectedGeneration: generation,
                cancellationToken).ConfigureAwait(false);
            if (version is null)
            {
                throw new GeoprocessingNotFoundException($"Studio package draft '{draftId:D}' was not found.");
            }

            Audit(principal, ToolName, draftId, generationBefore: draft.Generation, generationAfter: draft.Generation);
            return McpToolHelpers.SuccessResult(version, StudioJsonContext.Default.StudioContentVersion);
        }
        catch (InvalidOperationException ex)
        {
            throw new GeoprocessingPreconditionFailedException(ex.Message);
        }
        catch (KeyNotFoundException)
        {
            throw new GeoprocessingNotFoundException($"Studio package draft '{draftId:D}' was not found.");
        }
        catch (ArgumentException ex)
        {
            throw new GeoprocessingValidationException(ex.Message);
        }
    }

    private static long RequireGeneration(long? generation)
    {
        if (generation is null or < 1)
        {
            throw new GeoprocessingValidationException("'generation' must be a positive integer.");
        }

        return generation.Value;
    }
}

/// <summary>Reads one immutable Studio content version by its stable item/version identity.</summary>
internal sealed class GetStudioVersionTool : StudioDraftToolBase, IMcpTool
{
    public const string ToolName = "honua_studio_get_version";

    private readonly ILogger<GetStudioVersionTool> _typedLogger;

    public GetStudioVersionTool(IGeoprocessingJobService jobService, ILogger<GetStudioVersionTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Get Studio version",
        Description = "Read an immutable Studio content version by itemId and versionId, including its family-bearing envelope and content hash.",
        InputSchema = StudioMcpSchemas.VersionIdArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioContentVersionOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Get Studio version")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioGetVersion");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);
        var argument = McpToolHelpers.ParseArguments(
            arguments,
            StudioMcpJsonContext.Default.McpStudioVersionIdArgument);
        var (itemId, versionId) = RequireVersionIdentity(argument);
        var version = await lifecycleService.GetVersionAsync(itemId, versionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new GeoprocessingNotFoundException(
                $"Studio content version '{itemId:D}/{versionId:D}' was not found.");

        Audit(principal, ToolName, draftId: null, generationBefore: null, generationAfter: null);
        return McpToolHelpers.SuccessResult(version, StudioJsonContext.Default.StudioContentVersion);
    }

    internal static (Guid ItemId, Guid VersionId) RequireVersionIdentity(McpStudioVersionIdArgument argument)
    {
        var itemId = argument.ItemId
            ?? throw new GeoprocessingValidationException("'itemId' is required.");
        var versionId = argument.VersionId
            ?? throw new GeoprocessingValidationException("'versionId' is required.");
        return (itemId, versionId);
    }
}

/// <summary>
/// Reopens one immutable Studio content version as a new mutable draft whose
/// baseVersionId records the exact source version.
/// </summary>
internal sealed class ReopenStudioVersionTool : StudioDraftToolBase, IMcpTool
{
    public const string ToolName = "honua_studio_reopen_version";

    private readonly ILogger<ReopenStudioVersionTool> _typedLogger;

    public ReopenStudioVersionTool(IGeoprocessingJobService jobService, ILogger<ReopenStudioVersionTool> logger)
        : base(jobService, logger)
    {
        _typedLogger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Reopen Studio version",
        Description =
            "Reopen an immutable Studio map, app, dashboard, or other content version as a new mutable draft. "
            + "The returned draft has a new draftId and baseVersionId equal to the requested versionId.",
        InputSchema = StudioMcpSchemas.VersionIdArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        Annotations = McpToolAnnotationSets.Write("Reopen Studio version", destructive: false, idempotent: false)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioReopenVersion");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);
        var argument = McpToolHelpers.ParseArguments(
            arguments,
            StudioMcpJsonContext.Default.McpStudioVersionIdArgument);
        var (itemId, versionId) = GetStudioVersionTool.RequireVersionIdentity(argument);

        try
        {
            var draft = await lifecycleService.ReopenVersionAsync(
                itemId,
                versionId,
                ActorIdFor(principal),
                cancellationToken).ConfigureAwait(false)
                ?? throw new GeoprocessingNotFoundException(
                    $"Studio content version '{itemId:D}/{versionId:D}' was not found.");

            Audit(principal, ToolName, draft.DraftId, generationBefore: null, generationAfter: draft.Generation);
            return McpToolHelpers.SuccessResult(draft, StudioJsonContext.Default.StudioPackageDraft);
        }
        catch (InvalidOperationException ex)
        {
            throw new GeoprocessingPreconditionFailedException(ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw new GeoprocessingValidationException(ex.Message);
        }
    }
}
