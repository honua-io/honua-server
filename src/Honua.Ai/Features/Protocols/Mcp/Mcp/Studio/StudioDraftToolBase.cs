// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

internal abstract class StudioDraftToolBase
{
    // The dispatcher hides tool names from anonymous callers, but a known Studio call is still audited.
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

    protected static IStudioPackageLifecycleService RequireLifecycleService(HttpContext httpContext) =>
        httpContext.RequestServices.GetService<IStudioPackageLifecycleService>()
        ?? throw new GeoprocessingStoreUnavailableException("The Studio package lifecycle service is not available on this server.");

    // A missing mutation runtime stays a retryable capability error rather than an optimistic-concurrency failure.
    protected static IStudioDraftMutationRuntime RequireMutationRuntime(HttpContext httpContext) =>
        httpContext.RequestServices.GetService<IStudioDraftMutationRuntime>()
        ?? throw new GeoprocessingStoreUnavailableException("The Studio draft mutation runtime is not available on this server.");

    // A missing ownership policy fails closed instead of falling back to the generic operator-grant check.
    protected static IStudioAuthorizationService RequireAuthorizationService(HttpContext httpContext) =>
        httpContext.RequestServices.GetService<IStudioAuthorizationService>()
        ?? throw new GeoprocessingStoreUnavailableException("The Studio lifecycle authorization service is not available on this server.");

    protected static IStudioPackageValidator RequireValidator(HttpContext httpContext) =>
        httpContext.RequestServices.GetService<IStudioPackageValidator>()
        ?? throw new GeoprocessingStoreUnavailableException("The Studio package validator is not available on this server.");

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
            // Non-disclosure (#3429): authorize a missing draft as an ownerless target first, so a
            // caller who could not act on another owner's draft receives that same governed denial
            // instead of learning whether the id exists.
            await EnsureStudioAuthorizedAsync(
                httpContext,
                RequireAuthorizationService(httpContext),
                principal,
                studioOperation,
                resourceOwnerId: null,
                // A lookup miss carries the caller's own tenant so the tenant boundary stays a
                // no-op here and the ownerless-target denial keeps deciding (honua-server#4905).
                resourceTenantId: RequestTenantId(httpContext),
                draftId.ToString("D"),
                "studio-package-draft",
                operatorOperation,
                cancellationToken).ConfigureAwait(false);
            throw new GeoprocessingNotFoundException($"Studio package draft '{draftId:D}' was not found.");
        }

        await EnsureStudioAuthorizedAsync(
            httpContext,
            RequireAuthorizationService(httpContext),
            principal,
            studioOperation,
            draft.OwnerId,
            draft.TenantId,
            draftId.ToString("D"),
            "studio-package-draft",
            operatorOperation,
            cancellationToken).ConfigureAwait(false);

        return draft;
    }

    protected static string? RequestTenantId(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return httpContext.RequestServices.GetService<ITenantContext>()?.TenantId;
    }

    protected static async Task EnsureStudioAuthorizedAsync(
        HttpContext httpContext,
        IStudioAuthorizationService authorization,
        ClaimsPrincipal principal,
        StudioAuthorizationOperation studioOperation,
        string? resourceOwnerId,
        string? resourceTenantId,
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
            resourceTenantId,
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
        catch (Exception ex) when (
            ex is InvalidOperationException or GeoprocessingPreconditionFailedException
            && string.Equals(ex.Message, "Stale draft generation; refresh and retry.", StringComparison.Ordinal))
        {
            // A writer may win the store CAS after the initial authorized read. Re-authorize
            // the new snapshot before exposing its generation; never replay the mutation here.
            var current = await RequireAuthorizedDraftAsync(
                httpContext,
                principal,
                RequireLifecycleService(httpContext),
                draftId,
                StudioAuthorizationOperation.UpdateDraft,
                OperatorOperation.Create,
                cancellationToken).ConfigureAwait(false);
            throw new StudioDraftGenerationConflictException(current.Generation);
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

    protected static string? ActorIdFor(IStudioAuthorizationService authorization, ClaimsPrincipal principal) =>
        authorization.ResolveCallerId(principal);

    protected void Audit(ClaimsPrincipal principal, string toolName, Guid? draftId, long? generationBefore, long? generationAfter)
        => StudioMcpAudit.Record(Logger, principal, toolName, draftId, generationBefore, generationAfter);

    // Enum.TryParse cannot be used because the wire token is gp, not the member name.
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
