// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Studio;

/// <summary>
/// Shared collaborators and helpers for every Studio draft lifecycle and
/// composition-mutation MCP tool (honua-server#3002). Tools delegate every
/// state change to <see cref="IStudioPackageLifecycleService"/> — the
/// canonical Studio package lifecycle service — so no lifecycle logic
/// (generation checking, validation, persistence) is duplicated here; this
/// base only owns the MCP-specific plumbing: authorization against the
/// <c>StudioDraft</c> operator-grant family, typed error translation, and the
/// generation-before/after audit record (NFR-001).
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
        HttpContext httpContext, OperatorOperation operation, CancellationToken cancellationToken)
    {
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await JobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.StudioDraft, operation, cancellationToken)
            .ConfigureAwait(false);
        return principal;
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
    /// Loads a draft by id, translating a missing draft into the typed
    /// <c>not_found</c> MCP error channel instead of returning null.
    /// </summary>
    protected static async Task<StudioPackageDraft> RequireDraftAsync(
        IStudioPackageLifecycleService lifecycleService, Guid draftId, CancellationToken cancellationToken)
    {
        var draft = await lifecycleService.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        return draft ?? throw new GeoprocessingNotFoundException($"Studio package draft '{draftId:D}' was not found.");
    }

    /// <summary>
    /// Applies a mutation to a loaded draft through
    /// <see cref="IStudioPackageLifecycleService.UpdateDraftAsync"/>. Callers
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
        IStudioPackageLifecycleService lifecycleService,
        Guid draftId,
        UpdateStudioPackageDraftCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var updated = await lifecycleService.UpdateDraftAsync(draftId, command, cancellationToken)
                .ConfigureAwait(false);

            return updated
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

    /// <summary>Resolves the audit-record actor id from the resolved principal key.</summary>
    protected static string ActorIdFor(ClaimsPrincipal principal) => McpAuthorizationHelper.ResolvePrincipalKey(principal);

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
