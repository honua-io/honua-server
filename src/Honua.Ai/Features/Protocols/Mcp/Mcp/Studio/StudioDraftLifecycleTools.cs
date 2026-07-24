// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;

namespace Honua.Ai.Protocols.Mcp.Studio;

/// <summary>
/// MCP tool that creates a mutable Studio package lifecycle draft
/// (honua-server#3002, REQ-001). Delegates entirely to
/// <see cref="IStudioPackageLifecycleService.CreateDraftAsync"/> — the same
/// canonical service <c>POST /api/v*/studio/package-drafts</c> uses — so an
/// external MCP client and the in-app Studio AI proxy compose against the
/// identical draft the Studio UI observes (AD-8).
/// </summary>
internal sealed class CreateStudioDraftTool : StudioDraftToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_create_draft";

    private readonly ILogger<CreateStudioDraftTool> _typedLogger;

    public CreateStudioDraftTool(
        IStudioPackageLifecycleService lifecycleService,
        IGeoprocessingJobService jobService,
        ILogger<CreateStudioDraftTool> logger)
        : base(lifecycleService, jobService, logger)
    {
        _typedLogger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Create Studio draft",
        Description =
            "Create a mutable Studio package lifecycle draft (query, analysis, map, dashboard, report, form, app, workflow, gp, or etl family). "
            + "The composition a user and agent build IS this draft (AD-8): the returned draft's generation must be passed as `generation` on "
            + "every subsequent honua_studio_update_draft / composition-mutation call for optimistic concurrency.",
        InputSchema = StudioMcpSchemas.CreateDraftArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        // Write tool: it authors a new draft. Not destructive; not idempotent
        // (a replay creates a second distinct draft/content item).
        Annotations = McpToolAnnotationSets.Write("Create Studio draft", destructive: false, idempotent: false)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioCreateDraft");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create, cancellationToken)
            .ConfigureAwait(false);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioCreateDraftArgument);
        if (string.IsNullOrWhiteSpace(argument.PackageKey))
        {
            throw new GeoprocessingValidationException("'packageKey' is required.");
        }

        if (string.IsNullOrWhiteSpace(argument.SchemaVersion))
        {
            throw new GeoprocessingValidationException("'schemaVersion' is required.");
        }

        var family = ParseFamily(argument.Family);
        var envelope = new StudioPackageEnvelope
        {
            Family = family,
            SchemaVersion = argument.SchemaVersion,
            Body = argument.Body,
        };

        var actorId = ActorIdFor(principal);
        var draft = await LifecycleService.CreateDraftAsync(
            new CreateStudioPackageDraftCommand
            {
                ItemId = argument.ItemId,
                PackageKey = argument.PackageKey,
                WorkspaceId = argument.WorkspaceId,
                OwnerId = argument.OwnerId,
                Envelope = envelope,
                ActorId = actorId,
                BaseVersionId = argument.BaseVersionId,
            },
            cancellationToken).ConfigureAwait(false);

        Audit(principal, ToolName, draft.DraftId, generationBefore: null, generationAfter: draft.Generation);
        return McpToolHelpers.SuccessResult(draft, StudioJsonContext.Default.StudioPackageDraft);
    }
}

/// <summary>
/// MCP tool that reads a Studio package lifecycle draft by id
/// (honua-server#3002, REQ-001).
/// </summary>
internal sealed class GetStudioDraftTool : StudioDraftToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_get_draft";

    private readonly ILogger<GetStudioDraftTool> _typedLogger;

    public GetStudioDraftTool(
        IStudioPackageLifecycleService lifecycleService,
        IGeoprocessingJobService jobService,
        ILogger<GetStudioDraftTool> logger)
        : base(lifecycleService, jobService, logger)
    {
        _typedLogger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Get Studio draft",
        Description = "Read a Studio package lifecycle draft by id, including its current generation for the next optimistic-concurrency call.",
        InputSchema = StudioMcpSchemas.DraftIdArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Get Studio draft")
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioGetDraft");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioDraftIdArgument);
        var draftId = RequireDraftId(argument.DraftId);
        var draft = await RequireDraftAsync(draftId, cancellationToken).ConfigureAwait(false);

        Audit(principal, ToolName, draft.DraftId, generationBefore: draft.Generation, generationAfter: draft.Generation);
        return McpToolHelpers.SuccessResult(draft, StudioJsonContext.Default.StudioPackageDraft);
    }

    internal static Guid RequireDraftId(Guid? draftId) => draftId
        ?? throw new GeoprocessingValidationException("'draftId' is required.");
}

/// <summary>
/// MCP tool that replaces a Studio package lifecycle draft's editable fields
/// with optimistic-generation checking (honua-server#3002, REQ-001). Scoped
/// narrower than the REST admin surface — see
/// <see cref="McpStudioUpdateDraftArgument"/> — it never accepts
/// <c>publicationIntent</c>, so publish signals can only ever be recorded
/// through <see cref="ProposeStudioPublicationTool"/> (REQ-003/REQ-009).
/// </summary>
internal sealed class UpdateStudioDraftTool : StudioDraftToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_update_draft";

    private readonly ILogger<UpdateStudioDraftTool> _typedLogger;

    public UpdateStudioDraftTool(
        IStudioPackageLifecycleService lifecycleService,
        IGeoprocessingJobService jobService,
        ILogger<UpdateStudioDraftTool> logger)
        : base(lifecycleService, jobService, logger)
    {
        _typedLogger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Update Studio draft",
        Description =
            "Replace a Studio package lifecycle draft's editable fields (packageKey, workspaceId, ownerId, schemaVersion, format, body) "
            + "with optimistic-generation checking. Pass the `generation` last read from honua_studio_get_draft/honua_studio_create_draft; "
            + "a stale generation returns a failed_precondition error — re-fetch and retry rather than blindly resubmitting. "
            + "Does not accept publicationIntent: use honua_studio_propose_publication to record publish intent.",
        InputSchema = StudioMcpSchemas.UpdateDraftArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftOutputSchema,
        // Write tool: mutates existing state (not destructive of the item —
        // the draft is edited, not removed). Not idempotent: two calls with the
        // same generation are only both accepted if the first one changed
        // nothing observable to the second's precondition, which optimistic
        // concurrency does not guarantee in general.
        Annotations = McpToolAnnotationSets.Write("Update Studio draft", destructive: false, idempotent: false)
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioUpdateDraft");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Create, cancellationToken)
            .ConfigureAwait(false);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioUpdateDraftArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var generation = argument.Generation
            ?? throw new GeoprocessingValidationException("'generation' is required.");
        if (string.IsNullOrWhiteSpace(argument.PackageKey))
        {
            throw new GeoprocessingValidationException("'packageKey' is required.");
        }

        if (string.IsNullOrWhiteSpace(argument.SchemaVersion))
        {
            throw new GeoprocessingValidationException("'schemaVersion' is required.");
        }

        var existing = await RequireDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        var envelope = existing.Envelope with
        {
            SchemaVersion = argument.SchemaVersion,
            Format = argument.Format ?? existing.Envelope.Format,
            Body = argument.Body ?? existing.Envelope.Body,
        };

        var actorId = ActorIdFor(principal);
        var updated = await ApplyUpdateAsync(
            draftId,
            new UpdateStudioPackageDraftCommand
            {
                PackageKey = argument.PackageKey,
                WorkspaceId = argument.WorkspaceId ?? existing.WorkspaceId,
                OwnerId = argument.OwnerId ?? existing.OwnerId,
                Envelope = envelope,
                Generation = generation,
                ActorId = actorId,
            },
            cancellationToken).ConfigureAwait(false);

        Audit(principal, ToolName, draftId, generationBefore: generation, generationAfter: updated.Generation);
        return McpToolHelpers.SuccessResult(updated, StudioJsonContext.Default.StudioPackageDraft);
    }
}

/// <summary>
/// MCP tool that validates a Studio package lifecycle draft and persists the
/// resulting validation summary (honua-server#3002, REQ-001).
/// </summary>
internal sealed class ValidateStudioDraftTool : StudioDraftToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_validate_draft";

    private readonly ILogger<ValidateStudioDraftTool> _typedLogger;

    public ValidateStudioDraftTool(
        IStudioPackageLifecycleService lifecycleService,
        IGeoprocessingJobService jobService,
        ILogger<ValidateStudioDraftTool> logger)
        : base(lifecycleService, jobService, logger)
    {
        _typedLogger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Validate Studio draft",
        Description = "Validate a Studio package lifecycle draft's current envelope and persist the resulting validation summary on the draft.",
        InputSchema = StudioMcpSchemas.DraftIdArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioValidationOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Validate Studio draft")
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioValidateDraft");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioDraftIdArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var beforeDraft = await RequireDraftAsync(draftId, cancellationToken).ConfigureAwait(false);

        var actorId = ActorIdFor(principal);
        var validation = await LifecycleService.ValidateDraftAsync(draftId, actorId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new GeoprocessingNotFoundException($"Studio package draft '{draftId:D}' was not found.");

        Audit(principal, ToolName, draftId, generationBefore: beforeDraft.Generation, generationAfter: beforeDraft.Generation + 1);
        return McpToolHelpers.SuccessResult(validation, StudioJsonContext.Default.StudioValidationSummary);
    }
}

/// <summary>
/// MCP tool that builds a preview plan for a Studio package lifecycle draft
/// (honua-server#3002, REQ-001). Preview is read-only planning: for
/// job-backed families (gp/etl/workflow) it returns the plan without
/// submitting a job; the agent submits execution through the existing
/// geoprocessing tools, not this one.
/// </summary>
internal sealed class PreviewStudioDraftTool : StudioDraftToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_preview_draft";

    private readonly ILogger<PreviewStudioDraftTool> _typedLogger;

    public PreviewStudioDraftTool(
        IStudioPackageLifecycleService lifecycleService,
        IGeoprocessingJobService jobService,
        ILogger<PreviewStudioDraftTool> logger)
        : base(lifecycleService, jobService, logger)
    {
        _typedLogger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Preview Studio draft",
        Description =
            "Build a read-only preview plan for a Studio package lifecycle draft (re-validates the envelope first). "
            + "For job-backed families (gp, etl, workflow) the plan is planning-only — it does not submit or execute a job.",
        InputSchema = StudioMcpSchemas.DraftIdArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioPreviewPlanOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Preview Studio draft")
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("StudioPreviewDraft");
        McpLog.ToolInvoked(_typedLogger, ToolName, WorkflowFamily);

        var principal = await EnsureAuthorizedAsync(httpContext, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioDraftIdArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var beforeDraft = await RequireDraftAsync(draftId, cancellationToken).ConfigureAwait(false);

        var actorId = ActorIdFor(principal);
        var plan = await LifecycleService.PreviewPlanAsync(draftId, actorId, cancellationToken).ConfigureAwait(false)
            ?? throw new GeoprocessingNotFoundException($"Studio package draft '{draftId:D}' was not found.");

        Audit(principal, ToolName, draftId, generationBefore: beforeDraft.Generation, generationAfter: beforeDraft.Generation + 1);
        return McpToolHelpers.SuccessResult(plan, StudioJsonContext.Default.StudioPreviewPlan);
    }
}
