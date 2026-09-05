// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Studio;

/// <summary>
/// Shared collaborators and helpers for every Studio draft lifecycle and
/// composition-mutation MCP tool (honua-server#3002). Tools delegate every
/// state change to <see cref="IStudioPackageLifecycleService"/> — the
/// canonical Studio package lifecycle service — so no lifecycle logic
/// (generation checking, validation, persistence) is duplicated here; this
/// base only owns the MCP-specific plumbing: authorization against the
/// <c>StudioDraft</c> operator-grant family and the canonical Studio ownership
/// policy, typed error translation, and the generation-before/after audit
/// record (NFR-001).
/// </summary>
/// <remarks>
/// <para>
/// Tool descriptors are registered as singletons (matches every other
/// <c>/mcp</c> tool), but <see cref="IStudioPackageLifecycleService"/> and
/// <see cref="IStudioPackageValidator"/> are registered <c>Scoped</c>
/// (<c>AddStudioPackageLifecycle</c>) — a singleton constructor cannot depend
/// on them without becoming a captive dependency (PR #3016 review). Tools
/// therefore never take these as constructor parameters; every
/// <c>InvokeAsync</c> resolves them per call from <c>httpContext.RequestServices</c>
/// (the ASP.NET Core per-request DI scope) via <see cref="RequireLifecycleService"/> /
/// <see cref="RequireValidator"/>, mirroring the same per-request-resolution
/// pattern <c>CreateMapPackageTool</c>/<c>ApplyStylePresetTool</c> already use
/// for services composed after <c>AddMcpDataAccessSurface</c>. This also
/// means Studio tool registration no longer depends on
/// <c>AddStudioPackageLifecycle</c> having run first (or at all) — the tools
/// are registered unconditionally and fail per-call with a structured
/// <c>unavailable</c> error only if the host never composed Studio
/// persistence.
/// </para>
/// </remarks>
internal abstract class StudioDraftToolBase
{
    /// <summary>
    /// Records the authorization denial for a Studio tool rejected by the MCP
    /// dispatcher before <see cref="IMcpTool.InvokeAsync"/> can run. The
    /// dispatcher deliberately does not reveal tool names to anonymous callers,
    /// but a known Studio call still needs the same audit trail as an invocation
    /// that reaches <see cref="EnsureAuthorizedAsync"/>.
    /// </summary>
    internal static void RecordAnonymousAuthorizationDenied(
        ILogger logger, HttpContext httpContext, string toolName)
    {
        var principal = httpContext.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        StudioMcpAudit.Record(logger, principal, toolName, draftId: null, generationBefore: null, generationAfter: null);
    }

    protected StudioDraftToolBase(IGeoprocessingJobService jobService, ILogger logger)
    {
        JobService = jobService;
        Logger = logger;
    }

    protected IGeoprocessingJobService JobService { get; }

    protected ILogger Logger { get; }

    /// <summary>
    /// Resolves the caller's principal and authorizes it against the
    /// <see cref="OperatorResourceType.StudioDraft"/> grant family (the
    /// "studio-compose" grant family; honua-server#3002/#3001). Admin
    /// principals bypass as usual (matches the existing REST Studio lifecycle
    /// surface's admin-tier default posture); the OAuth bearer-scope
    /// narrowing in <see cref="IGeoprocessingJobService.EnsureCallerAuthorizedAsync"/>
    /// applies identically to every other <c>/mcp</c> tool.
    /// </summary>
    protected async Task<ClaimsPrincipal> EnsureAuthorizedAsync(
        HttpContext httpContext,
        OperatorOperation operation,
        StudioAuthorizationOperation studioOperation,
        CancellationToken cancellationToken)
    {
        try
        {
            var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
            await JobService
                .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.StudioDraft, operation, cancellationToken)
                .ConfigureAwait(false);
            return principal;
        }
        catch (GeoprocessingAuthorizationException exception)
        {
            var code = exception.PolicyCode
                ?? (exception.RequiresAuthentication
                    ? StudioAuthorizationService.AuthenticationRequiredCode
                    : exception.DenialReason == AuthorizationDenialReason.InsufficientScope
                        ? StudioAuthorizationService.OAuthScopeRequiredCode
                        : StudioAuthorizationService.OperatorGrantRequiredCode);
            await RecordAuthorizationDecisionAsync(
                httpContext,
                studioOperation,
                resourceId: null,
                StudioAuthorizationDecision.Deny(code, exception.Message)).ConfigureAwait(false);

            if (exception.PolicyCode is not null)
            {
                throw;
            }

            // The generic operator gate does not know Studio's stable denial vocabulary.
            // Carry the code derived above into the exception that the MCP transport maps so
            // the audited preliminary decision and the caller-visible tool error stay bound.
            throw new GeoprocessingAuthorizationException(
                exception.RequiresAuthentication,
                exception.Message,
                exception.ResourceType,
                exception.Operation,
                exception.DenialReason,
                code);
        }
    }

    /// <summary>
    /// Resolves <see cref="IStudioPackageLifecycleService"/> from the current
    /// request's DI scope. Throws <see cref="GeoprocessingStoreUnavailableException"/>
    /// (surfaced as a retryable <c>unavailable</c> MCP error) when the host
    /// never composed Studio persistence (<c>AddStudioPackageLifecycle</c>) —
    /// the same structured-unavailable pattern <c>ApplyStylePresetTool</c>
    /// uses for <c>IStyleCatalog</c>.
    /// </summary>
    protected static IStudioPackageLifecycleService RequireLifecycleService(HttpContext httpContext) =>
        httpContext.RequestServices.GetService<IStudioPackageLifecycleService>()
        ?? throw new GeoprocessingStoreUnavailableException("The Studio package lifecycle service is not available on this server.");

    /// <summary>
    /// Resolves the canonical durable mutation runtime from the current request scope.
    /// Studio tools are advertised by the modular MCP composition even when a host has
    /// not composed the server-only operations toolset, so absence must remain a typed,
    /// retryable capability error rather than masquerading as optimistic concurrency.
    /// </summary>
    protected static IStudioDraftMutationRuntime RequireMutationRuntime(HttpContext httpContext) =>
        httpContext.RequestServices.GetService<IStudioDraftMutationRuntime>()
        ?? throw new GeoprocessingStoreUnavailableException("The Studio draft mutation runtime is not available on this server.");

    /// <summary>
    /// Resolves the canonical Studio ownership-policy service from the current
    /// request scope. Studio MCP tools must use the same owner rules as the REST
    /// lifecycle surface; failing closed when that policy was not composed is
    /// safer than falling back to the generic operator-grant check alone.
    /// </summary>
    protected static IStudioAuthorizationService RequireAuthorizationService(HttpContext httpContext) =>
        httpContext.RequestServices.GetService<IStudioAuthorizationService>()
        ?? throw new GeoprocessingStoreUnavailableException("The Studio lifecycle authorization service is not available on this server.");

    /// <summary>
    /// Resolves the pure <see cref="IStudioPackageValidator"/> from the
    /// current request's DI scope, for tools that validate WITHOUT persisting
    /// (<c>honua_studio_validate_draft</c>, <c>honua_studio_preview_draft</c>
    /// — PR #3016 review: these must not silently mutate draft state behind a
    /// <c>readOnlyHint: true</c> advertisement).
    /// </summary>
    protected static IStudioPackageValidator RequireValidator(HttpContext httpContext) =>
        httpContext.RequestServices.GetService<IStudioPackageValidator>()
        ?? throw new GeoprocessingStoreUnavailableException("The Studio package validator is not available on this server.");

    /// <summary>
    /// Loads a draft by id and authorizes the loaded owner through the same
    /// <see cref="IStudioAuthorizationService"/> policy used by the REST
    /// lifecycle surface. Keeping the raw load private makes the secure
    /// load-then-authorize sequence the only draft-loading helper available to
    /// derived tools.
    /// </summary>
    protected static async Task<StudioPackageDraft> RequireAuthorizedDraftAsync(
        HttpContext httpContext,
        ClaimsPrincipal principal,
        IStudioPackageLifecycleService lifecycleService,
        Guid draftId,
        StudioAuthorizationOperation studioOperation,
        OperatorOperation operatorOperation,
        CancellationToken cancellationToken)
    {
        var draft = await lifecycleService.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            throw new GeoprocessingNotFoundException($"Studio package draft '{draftId:D}' was not found.");
        }

        await EnsureStudioAuthorizedAsync(
            httpContext,
            RequireAuthorizationService(httpContext),
            principal,
            studioOperation,
            draft.OwnerId,
            draftId.ToString("D"),
            "studio-package-draft",
            operatorOperation,
            cancellationToken).ConfigureAwait(false);

        return draft;
    }

    /// <summary>
    /// Applies the canonical Studio ownership decision and translates a denial
    /// into the MCP authorization exception channel while retaining the stable
    /// Studio decision code for protocol parity with REST.
    /// </summary>
    protected static async Task EnsureStudioAuthorizedAsync(
        HttpContext httpContext,
        IStudioAuthorizationService authorization,
        ClaimsPrincipal principal,
        StudioAuthorizationOperation studioOperation,
        string? resourceOwnerId,
        string? resourceId,
        string resourceType,
        OperatorOperation operatorOperation,
        CancellationToken cancellationToken)
    {
        var callerId = authorization.ResolveCallerId(principal);
        var decision = await authorization.AuthorizeAsync(
            principal,
            callerId,
            studioOperation,
            resourceOwnerId,
            resourceId: resourceId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await RecordAuthorizationDecisionAsync(
            httpContext,
            studioOperation,
            resourceId,
            decision,
            resourceType).ConfigureAwait(false);

        if (!decision.IsAllowed)
        {
            throw new GeoprocessingAuthorizationException(
                requiresAuthentication: string.Equals(
                    decision.Code,
                    StudioAuthorizationService.AuthenticationRequiredCode,
                    StringComparison.Ordinal),
                message: decision.Reason ?? "The caller is not authorized to access this Studio resource.",
                resourceType: OperatorResourceType.StudioDraft,
                operation: operatorOperation,
                policyCode: decision.Code);
        }
    }

    /// <summary>
    /// Rejects an explicit owner assignment from a non-admin caller. The MCP schema advertises
    /// this field as admin-only, and rejecting it prevents a successful response from implying
    /// that a silently ignored ownership transfer occurred.
    /// </summary>
    protected static async Task EnsureOwnerAssignmentAuthorizedAsync(
        HttpContext httpContext,
        IStudioAuthorizationService authorization,
        ClaimsPrincipal principal,
        string? requestedOwnerId,
        StudioAuthorizationOperation studioOperation,
        string? resourceId,
        OperatorOperation operatorOperation)
    {
        if (requestedOwnerId is null || authorization.IsAdmin(principal))
        {
            return;
        }

        const string reason = "Assigning a Studio draft owner requires the admin role.";
        var decision = StudioAuthorizationDecision.Deny(
            StudioAuthorizationService.OwnerAssignmentAdminRequiredCode,
            reason);
        await RecordAuthorizationDecisionAsync(
            httpContext,
            studioOperation,
            resourceId,
            decision).ConfigureAwait(false);
        throw new GeoprocessingAuthorizationException(
            requiresAuthentication: false,
            message: reason,
            resourceType: OperatorResourceType.StudioDraft,
            operation: operatorOperation,
            policyCode: decision.Code);
    }

    /// <summary>
    /// Binds a mutating request's expected generation to the exact owner-authorized snapshot.
    /// The lifecycle store still performs its compare-and-swap, covering changes after this
    /// check; this check prevents a caller from supplying a future generation that belongs to a
    /// concurrently rebound resource.
    /// </summary>
    protected static void RequireAuthorizedGeneration(StudioPackageDraft draft, long expectedGeneration)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Generation != expectedGeneration)
        {
            throw new StudioDraftGenerationConflictException(draft.Generation);
        }
    }

    protected static async Task RecordAuthorizationDecisionAsync(
        HttpContext httpContext,
        StudioAuthorizationOperation studioOperation,
        string? resourceId,
        StudioAuthorizationDecision decision,
        string resourceType = "studio-package-draft")
    {
        var auditLog = httpContext.RequestServices.GetService<IAuditLog>();
        if (auditLog is null)
        {
            return;
        }

        var timeProvider = httpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        await StudioAuthorizationAudit.RecordDecisionAsync(
            httpContext,
            auditLog,
            timeProvider,
            studioOperation,
            resourceType,
            resourceId,
            decision).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a mutation to a loaded draft through the canonical durable
    /// <see cref="IStudioDraftMutationRuntime"/>. Callers
    /// build <paramref name="command"/> from the draft's current
    /// packageKey/workspaceId/ownerId (composition tools, which never change
    /// them) or from caller-supplied overrides
    /// (<see cref="UpdateStudioDraftTool"/>, which does). Translates the
    /// store's optimistic-concurrency <see cref="InvalidOperationException"/>
    /// into a typed <see cref="GeoprocessingPreconditionFailedException"/>
    /// (surfaced as a <c>failed_precondition</c> MCP error) so a generation
    /// conflict never presents as an opaque internal error, and a draft that
    /// disappeared between load and update into <see cref="GeoprocessingNotFoundException"/>.
    /// </summary>
    protected static async Task<StudioPackageDraft> ApplyUpdateAsync(
        HttpContext httpContext,
        ClaimsPrincipal principal,
        Guid draftId,
        UpdateStudioPackageDraftCommand command,
        CancellationToken cancellationToken)
    {
        var runtime = RequireMutationRuntime(httpContext);
        try
        {
            var receipt = await runtime.UpdateAsync(
                draftId,
                command,
                new StudioDraftMutationContext
                {
                    PrincipalId = command.ActorId,
                    TenantId = httpContext.RequestServices.GetService<ITenantContext>()?.TenantId,
                    SchemaName = httpContext.RequestServices.GetService<ISchemaContext>()?.CurrentSchema,
                    CorrelationId = httpContext.TraceIdentifier,
                    AuthorizationOutcome = "authorized",
                    Roles = principal.FindAll(ClaimTypes.Role).Select(static claim => claim.Value).ToArray(),
                    ScopeGoverned = OperatorScopeCatalog.IsScopeGoverned(principal),
                    RecognizedScopes = OperatorScopeCatalog.CollectRecognizedScopes(principal)
                        .OrderBy(scope => scope, StringComparer.Ordinal).ToArray(),
                },
                cancellationToken)
                .ConfigureAwait(false);

            if (receipt.Operation.Status != OperationHandleStatus.Completed)
            {
                ThrowMutationOutcome(receipt.Operation);
            }

            return receipt.Value
                ?? throw new GeoprocessingNotFoundException($"Studio package draft '{draftId:D}' was not found.");
        }
        catch (InvalidOperationException ex)
        {
            // The lifecycle store's generation-conflict signal
            // ("Stale draft generation; refresh and retry.") is a plain
            // InvalidOperationException at the store boundary (see
            // InMemoryStudioPackageStore.UpdateDraftAsync); translate it to the
            // typed precondition-failed channel so honua_studio_update_draft and
            // every composition tool surface identical, structured
            // failed_precondition errors instead of an opaque internal error.
            throw new GeoprocessingPreconditionFailedException(ex.Message);
        }
    }

    private static void ThrowMutationOutcome(OperationHandle operation)
    {
        var message = operation.Reason ?? $"Studio draft update ended as '{operation.Status}'.";
        if (operation.Status == OperationHandleStatus.RequiresApproval)
        {
            throw new GeoprocessingApprovalRequiredException(
                operation.ApprovalLane ?? operation.PolicyDecision?.ToString() ?? operation.OperationId,
                message,
                operation.ProposalId);
        }

        if (operation.Result?.Details.TryGetValue("errorKind", out var errorKind) == true)
        {
            switch (errorKind)
            {
                case "argument":
                    throw new GeoprocessingValidationException(message);
                case "not-found":
                    throw new GeoprocessingNotFoundException(message);
            }
        }

        if (operation.Status == OperationHandleStatus.Denied)
        {
            throw new GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                message: message,
                resourceType: OperatorResourceType.StudioDraft,
                operation: OperatorOperation.Create);
        }

        throw new GeoprocessingPreconditionFailedException(message);
    }

    /// <summary>
    /// Builds an <see cref="UpdateStudioPackageDraftCommand"/> that preserves
    /// <paramref name="draft"/>'s current packageKey/workspaceId/ownerId and
    /// applies only <paramref name="envelope"/> + <paramref name="expectedGeneration"/> —
    /// the shape every composition-mutation tool uses (add/remove layer, set
    /// style, set view, add/remove widget, propose-publication all touch only
    /// the envelope).
    /// </summary>
    protected static UpdateStudioPackageDraftCommand EnvelopeOnlyUpdate(
        StudioPackageDraft draft, StudioPackageEnvelope envelope, long expectedGeneration, string? actorId) => new()
        {
            PackageKey = draft.PackageKey,
            WorkspaceId = draft.WorkspaceId,
            OwnerId = draft.OwnerId,
            Envelope = envelope,
            Generation = expectedGeneration,
            ActorId = actorId,
        };

    /// <summary>
    /// Resolves the lifecycle actor/owner identifier through the canonical
    /// Studio policy so MCP-created ownership keys match REST-created keys.
    /// </summary>
    protected static string? ActorIdFor(IStudioAuthorizationService authorization, ClaimsPrincipal principal) =>
        authorization.ResolveCallerId(principal);

    /// <summary>Records the per-call audit entry (NFR-001).</summary>
    protected void Audit(ClaimsPrincipal principal, string toolName, Guid? draftId, long? generationBefore, long? generationAfter)
        => StudioMcpAudit.Record(Logger, principal, toolName, draftId, generationBefore, generationAfter);

    /// <summary>
    /// Parses a family token (the lowercase wire values in
    /// <see cref="StudioPackageFamily"/>'s <c>JsonStringEnumMemberName</c>
    /// attributes — the same tokens <see cref="StudioMcpSchemas.CreateDraftArgumentSchema"/>
    /// enumerates) into the domain enum. <c>Enum.TryParse</c> cannot be used
    /// directly because the wire token for <see cref="StudioPackageFamily.Geoprocessing"/>
    /// is <c>gp</c>, not the member name.
    /// </summary>
    protected static StudioPackageFamily ParseFamily(string? family) => family?.Trim().ToLowerInvariant() switch
    {
        "query" => StudioPackageFamily.Query,
        "analysis" => StudioPackageFamily.Analysis,
        "map" => StudioPackageFamily.Map,
        "dashboard" => StudioPackageFamily.Dashboard,
        "report" => StudioPackageFamily.Report,
        "form" => StudioPackageFamily.Form,
        "app" => StudioPackageFamily.App,
        "workflow" => StudioPackageFamily.Workflow,
        "gp" => StudioPackageFamily.Geoprocessing,
        "etl" => StudioPackageFamily.Etl,
        _ => throw new GeoprocessingValidationException(
            $"'family' must be one of: query, analysis, map, dashboard, report, form, app, workflow, gp, etl. Got '{family}'."),
    };
}
