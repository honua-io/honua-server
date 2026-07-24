// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Tools;

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
internal abstract class StudioDraftToolBase
{
    protected StudioDraftToolBase(
        IStudioPackageLifecycleService lifecycleService,
        IGeoprocessingJobService jobService,
        ILogger logger)
    {
        LifecycleService = lifecycleService;
        JobService = jobService;
        Logger = logger;
    }

    protected IStudioPackageLifecycleService LifecycleService { get; }

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
    /// Loads a draft by id, translating a missing draft into the typed
    /// <c>not_found</c> MCP error channel instead of returning null.
    /// </summary>
    protected async Task<StudioPackageDraft> RequireDraftAsync(Guid draftId, CancellationToken cancellationToken)
    {
        var draft = await LifecycleService.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
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
    protected async Task<StudioPackageDraft> ApplyUpdateAsync(
        Guid draftId,
        UpdateStudioPackageDraftCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var updated = await LifecycleService.UpdateDraftAsync(draftId, command, cancellationToken)
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
