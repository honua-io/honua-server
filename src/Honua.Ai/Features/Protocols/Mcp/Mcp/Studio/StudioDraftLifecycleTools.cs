// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;

namespace Honua.Ai.Protocols.Mcp.Studio;

/// <summary>
/// MCP tool that creates a mutable Studio package lifecycle draft
/// (honua-server#3002, REQ-001). Delegates entirely to
/// <see cref="Honua.Core.Features.Studio.Abstractions.IStudioPackageLifecycleService.CreateDraftAsync"/> —
/// the same canonical service <c>POST /api/v*/studio/package-drafts</c>
/// uses — so an external MCP client and the in-app Studio AI proxy compose
/// against the identical draft the Studio UI observes (AD-8).
/// </summary>
internal sealed class CreateStudioDraftTool : StudioDraftToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_create_draft";

    private readonly ILogger<CreateStudioDraftTool> _typedLogger;

    public CreateStudioDraftTool(IGeoprocessingJobService jobService, ILogger<CreateStudioDraftTool> logger)
        : base(jobService, logger)
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
            + "every subsequent honua_studio_update_draft / composition-mutation call for optimistic concurrency. "
            + "The optional `ownerId` field is admin-only; non-admin callers must omit it and become the owner automatically.",
        InputSchema = StudioMcpSchemas.CreateDraftArgumentSchema,
        OutputSchema = McpToolOutputSchemas.StudioDraftMutationOutputSchema,
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

        var principal = await EnsureAuthorizedAsync(
                httpContext,
                OperatorOperation.Create,
                StudioAuthorizationOperation.CreateDraft,
                cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

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
        var familyDescriptor = lifecycleService.GetCapabilities().Families
            .FirstOrDefault(descriptor => descriptor.Family == family)
            ?? throw new GeoprocessingValidationException($"Studio package family '{family}' is not available.");
        var envelope = new StudioPackageEnvelope
        {
            Family = family,
            SchemaVersion = argument.SchemaVersion,
            Format = familyDescriptor.Format,
            Body = argument.Body,
        };

        var authorization = RequireAuthorizationService(httpContext);
        var actorId = ActorIdFor(authorization, principal);
        var requestedOwnerId = argument.OwnerId;
        // Match the REST lifecycle boundary: an explicit item id targets an
        // existing content item's recorded owner; otherwise creation is
        // authorized as a new resource owned by the authenticated caller.
        var existingPointers = argument.ItemId is { } itemId
            ? await lifecycleService.GetPointersAsync(itemId, cancellationToken).ConfigureAwait(false)
            : null;
        await EnsureStudioAuthorizedAsync(
            httpContext,
            authorization,
            principal,
            StudioAuthorizationOperation.CreateDraft,
            existingPointers is null ? actorId : existingPointers.OwnerId,
            existingPointers is null ? null : argument.ItemId?.ToString("D"),
            existingPointers is null ? "studio-package-draft" : "studio-content-item",
            OperatorOperation.Create,
            cancellationToken).ConfigureAwait(false);

        // Apply the canonical Studio mode/ownership decision first. This keeps
        // the end-user mode gate's policy code authoritative when that mode is
        // disabled, before applying the admin-only owner-assignment rule.
        await EnsureOwnerAssignmentAuthorizedAsync(
            httpContext,
            authorization,
            principal,
            requestedOwnerId,
            StudioAuthorizationOperation.CreateDraft,
            argument.ItemId?.ToString("D"),
            OperatorOperation.Create).ConfigureAwait(false);

        // A non-admin caller can create only a draft they own. Admins retain
        // the REST surface's ability to assign an explicit owner.
        var ownerId = requestedOwnerId ?? existingPointers?.OwnerId ?? actorId;
        StudioDraftMutationReceipt<StudioPackageDraft> receipt;
        try
        {
            var mutationRuntime = httpContext.RequestServices.GetRequiredService<IStudioDraftMutationRuntime>();
            receipt = await mutationRuntime.CreateAsync(
                new CreateStudioPackageDraftCommand
                {
                    ItemId = argument.ItemId,
                    PackageKey = argument.PackageKey,
                    WorkspaceId = argument.WorkspaceId,
                    OwnerId = ownerId,
                    ExpectedExistingItemOwnerId = existingPointers?.OwnerId,
                    ExpectedExistingItemPresent = existingPointers is not null,
                    Envelope = envelope,
                    ActorId = actorId,
                    BaseVersionId = argument.BaseVersionId,
                },
                new StudioDraftMutationContext
                {
                    PrincipalId = actorId,
                    TenantId = httpContext.RequestServices.GetService<ITenantContext>()?.TenantId,
                    SchemaName = httpContext.RequestServices.GetService<ISchemaContext>()?.CurrentSchema,
                    CorrelationId = httpContext.TraceIdentifier,
                    AuthorizationOutcome = "authorized",
                    Roles = principal.FindAll(ClaimTypes.Role).Select(static claim => claim.Value).ToArray(),
                    ScopeGoverned = OperatorScopeCatalog.IsScopeGoverned(principal),
                    RecognizedScopes = OperatorScopeCatalog.CollectRecognizedScopes(principal)
                        .OrderBy(scope => scope, StringComparer.Ordinal).ToArray(),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (StudioCompositionConflictException ex)
        {
            // The store performs this ownership check atomically with creation;
            // map the race outcome to the stable MCP permission channel rather
            // than leaking it as an internal error.
            await RecordAuthorizationDecisionAsync(
                httpContext,
                StudioAuthorizationOperation.CreateDraft,
                argument.ItemId?.ToString("D"),
                StudioAuthorizationDecision.Deny("studio_authorization/owner_conflict", ex.Message),
                resourceType: "studio-content-item").ConfigureAwait(false);
            throw new GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                message: ex.Message,
                resourceType: OperatorResourceType.StudioDraft,
                operation: OperatorOperation.Create,
                policyCode: "studio_authorization/owner_conflict");
        }

        if (receipt.Operation.Status == OperationHandleStatus.Failed
            && receipt.Operation.Result?.Details.TryGetValue("errorKind", out var errorKind) == true
            && string.Equals(errorKind, "owner-conflict", StringComparison.Ordinal))
        {
            var message = receipt.Operation.Reason ?? "The existing Studio item is owned by another principal.";
            await RecordAuthorizationDecisionAsync(
                httpContext,
                StudioAuthorizationOperation.CreateDraft,
                argument.ItemId?.ToString("D"),
                StudioAuthorizationDecision.Deny("studio_authorization/owner_conflict", message),
                resourceType: "studio-content-item").ConfigureAwait(false);
            throw new GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                message: message,
                resourceType: OperatorResourceType.StudioDraft,
                operation: OperatorOperation.Create,
                policyCode: "studio_authorization/owner_conflict");
        }

        if (receipt.Value is { } draft)
        {
            Audit(principal, ToolName, draft.DraftId, generationBefore: null, generationAfter: draft.Generation);
        }

        return McpToolHelpers.SuccessResult(
            McpStudioDraftMutationOutput.From(receipt),
            StudioMcpJsonContext.Default.McpStudioDraftMutationOutput);
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

    public GetStudioDraftTool(IGeoprocessingJobService jobService, ILogger<GetStudioDraftTool> logger)
        : base(jobService, logger)
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

        var principal = await EnsureAuthorizedAsync(
                httpContext,
                OperatorOperation.Read,
                StudioAuthorizationOperation.ReadDraft,
                cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioDraftIdArgument);
        var draftId = RequireDraftId(argument.DraftId);
        var draft = await RequireAuthorizedDraftAsync(
            httpContext,
            principal,
            lifecycleService,
            draftId,
            StudioAuthorizationOperation.ReadDraft,
            OperatorOperation.Read,
            cancellationToken).ConfigureAwait(false);

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

    public UpdateStudioDraftTool(IGeoprocessingJobService jobService, ILogger<UpdateStudioDraftTool> logger)
        : base(jobService, logger)
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
            + "The optional `ownerId` field is admin-only; non-admin callers must omit it. "
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

        var principal = await EnsureAuthorizedAsync(
                httpContext,
                OperatorOperation.Create,
                StudioAuthorizationOperation.UpdateDraft,
                cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);

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

        var existing = await RequireAuthorizedDraftAsync(
            httpContext,
            principal,
            lifecycleService,
            draftId,
            StudioAuthorizationOperation.UpdateDraft,
            OperatorOperation.Create,
            cancellationToken).ConfigureAwait(false);
        RequireAuthorizedGeneration(existing, generation);
        var envelope = existing.Envelope with
        {
            SchemaVersion = argument.SchemaVersion,
            Format = argument.Format ?? existing.Envelope.Format,
            Body = argument.Body ?? existing.Envelope.Body,
        };

        var authorization = RequireAuthorizationService(httpContext);
        var actorId = ActorIdFor(authorization, principal);
        await EnsureOwnerAssignmentAuthorizedAsync(
            httpContext,
            authorization,
            principal,
            argument.OwnerId,
            StudioAuthorizationOperation.UpdateDraft,
            draftId.ToString("D"),
            OperatorOperation.Create).ConfigureAwait(false);
        // Draft metadata may be renamed before a body is composed. Validate document replacements
        // and format/schema changes, while retaining the existing metadata-only draft workflow.
        var documentChanged = argument.Body is not null
            || argument.Format is not null
            || !string.Equals(argument.SchemaVersion, existing.Envelope.SchemaVersion, StringComparison.Ordinal);
        if (documentChanged && StudioCompositionBodyEditor.CompositionEligibleFamilies.Contains(existing.Family))
        {
            var validation = RequireValidator(httpContext).Validate(envelope);
            if (validation.Status == StudioPackageValidationStatus.Invalid)
            {
                var diagnostic = validation.Diagnostics.Count > 0 ? validation.Diagnostics[0] : null;
                throw new GeoprocessingValidationException(diagnostic is null
                    ? "The composition body is invalid."
                    : $"{diagnostic.Code} at {diagnostic.Path}: {diagnostic.Message}");
            }
        }
        var updated = await ApplyUpdateAsync(
            httpContext,
            principal,
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
/// MCP tool that validates a Studio package lifecycle draft's CURRENT
/// envelope (honua-server#3002, REQ-001). Genuinely read-only (PR #3016
/// review): unlike the REST admin surface's <c>POST .../validate</c>, this
/// tool calls the pure <see cref="Honua.Core.Features.Studio.Abstractions.IStudioPackageValidator"/>
/// directly and does NOT persist the result or advance the draft's
/// generation — so its <c>readOnlyHint: true</c> annotation is honest and a
/// concurrent composition-mutation call's expected generation is never
/// invalidated by a validate call racing it.
/// </summary>
internal sealed class ValidateStudioDraftTool : StudioDraftToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_validate_draft";

    private readonly ILogger<ValidateStudioDraftTool> _typedLogger;

    public ValidateStudioDraftTool(IGeoprocessingJobService jobService, ILogger<ValidateStudioDraftTool> logger)
        : base(jobService, logger)
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
        Description =
            "Validate a Studio package lifecycle draft's current envelope and return the validation summary. "
            + "Genuinely read-only: it does NOT persist the result onto the draft or change its generation "
            + "(unlike the REST admin POST .../validate endpoint), so it is always safe to call between composition-mutation calls.",
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

        var principal = await EnsureAuthorizedAsync(
                httpContext,
                OperatorOperation.Read,
                StudioAuthorizationOperation.ValidateDraft,
                cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);
        var validator = RequireValidator(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioDraftIdArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var draft = await RequireAuthorizedDraftAsync(
            httpContext,
            principal,
            lifecycleService,
            draftId,
            StudioAuthorizationOperation.ValidateDraft,
            OperatorOperation.Read,
            cancellationToken).ConfigureAwait(false);

        // Pure computation only — no UpdateDraftAsync/ValidateDraftAsync call,
        // so the draft's persisted generation is untouched.
        var validation = validator.Validate(draft.Envelope);

        Audit(principal, ToolName, draftId, generationBefore: draft.Generation, generationAfter: draft.Generation);
        return McpToolHelpers.SuccessResult(validation, StudioJsonContext.Default.StudioValidationSummary);
    }
}

/// <summary>
/// MCP tool that builds a preview plan for a Studio package lifecycle draft's
/// CURRENT envelope (honua-server#3002, REQ-001). Genuinely read-only (PR
/// #3016 review): mirrors <c>StudioPackageLifecycleService.PreviewPlanAsync</c>'s
/// projection (validate, then classify synchronous vs job-backed by family)
/// without calling it, because that method persists a refreshed validation
/// summary through the store first. For job-backed families (gp/etl/workflow)
/// the plan is planning-only — it does not submit or execute a job.
/// </summary>
internal sealed class PreviewStudioDraftTool : StudioDraftToolBase, IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_studio_preview_draft";

    private readonly ILogger<PreviewStudioDraftTool> _typedLogger;

    public PreviewStudioDraftTool(IGeoprocessingJobService jobService, ILogger<PreviewStudioDraftTool> logger)
        : base(jobService, logger)
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
            "Build a read-only preview plan for a Studio package lifecycle draft's current envelope. "
            + "Genuinely read-only: it does NOT persist a validation summary or change the draft's generation "
            + "(unlike the REST admin POST .../preview-plan endpoint). "
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

        var principal = await EnsureAuthorizedAsync(
                httpContext,
                OperatorOperation.Read,
                StudioAuthorizationOperation.ValidateDraft,
                cancellationToken)
            .ConfigureAwait(false);
        var lifecycleService = RequireLifecycleService(httpContext);
        var validator = RequireValidator(httpContext);

        var argument = McpToolHelpers.ParseArguments(arguments, StudioMcpJsonContext.Default.McpStudioDraftIdArgument);
        var draftId = GetStudioDraftTool.RequireDraftId(argument.DraftId);
        var draft = await RequireAuthorizedDraftAsync(
            httpContext,
            principal,
            lifecycleService,
            draftId,
            StudioAuthorizationOperation.ValidateDraft,
            OperatorOperation.Read,
            cancellationToken).ConfigureAwait(false);

        // Pure computation only — mirrors StudioPackageLifecycleService.PreviewPlanAsync's
        // projection without calling it (that method persists via ValidateDraftAsync first).
        var validation = validator.Validate(draft.Envelope);
        var requiresJob = StudioPackageLifecycleService.RequiresBackgroundJob(draft.Family);
        var steps = requiresJob
            ? new[] { "validate-envelope", "plan-background-preview-job" }
            : new[] { "validate-envelope", "prepare-inline-preview" };
        var plan = new StudioPreviewPlan
        {
            DraftId = draft.DraftId,
            Family = draft.Family,
            Synchronous = !requiresJob,
            RequiresJob = requiresJob,
            Steps = steps,
            Validation = validation,
        };

        Audit(principal, ToolName, draftId, generationBefore: draft.Generation, generationAfter: draft.Generation);
        return McpToolHelpers.SuccessResult(plan, StudioJsonContext.Default.StudioPreviewPlan);
    }
}
