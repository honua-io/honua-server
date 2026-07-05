// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Capabilities;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.VersionManagementServer.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.VersionManagementServer;

/// <summary>
/// Esri GeoServices VersionManagementServer protocol slice (#1272, ADR-0051). A thin protocol
/// adapter over the canonical <see cref="IVersionManager"/>: it parses Esri-shaped requests,
/// enforces the Enterprise branch-versioning entitlement and service write authorization, and maps
/// version-manager results to the Esri wire shape. It owns no storage, read, write, reconcile, or
/// post behavior — that all lives in the provider's <see cref="IVersionManager"/> implementation.
/// </summary>
/// <remarks>
/// <para>
/// Branch versioning is Postgres-only and Enterprise-gated. When the active provider's
/// <see cref="IVersionManager.SupportsVersioning"/> is false (DuckDB / SQL Server / MySQL), the
/// mutating and lifecycle operations return a 501 not-supported response; the read-only
/// service-info / <c>versions</c> / <c>versionInfo</c> operations report an empty version set.
/// </para>
/// <para>
/// In Honua's overlay/moment storage model a version read/edit carries its
/// <see cref="VersionContext"/> per-request (resolved from <c>gdbVersion</c> on the FeatureServer
/// surface), so there is no server-held read/edit session. The <c>startReading</c>/<c>stopReading</c>
/// and <c>startEditing</c>/<c>stopEditing</c> operations are therefore stateless acknowledgements;
/// this divergence from Esri's session-token model is intentional and documented here. They are not,
/// however, no-ops: each resolves the named version, returns the version's durable branch generation
/// as the read/edit <c>moment</c> (a stable cursor an Esri client can echo to pin a consistent
/// snapshot), and <c>startEditing</c> refuses with a 409 in-progress when the version is
/// mid-reconcile/post (locked). Reconcile/post delegate straight to the version manager, which owns
/// the Redis-backed version lock and job runtime.
/// </para>
/// </remarks>
public static class VersionManagementServerEndpoints
{
    private const string BasePath = "/rest/services/{serviceId}/VersionManagementServer";
    private const string Tag = "VersionManagementServer";

    /// <summary>
    /// Maps the VersionManagementServer REST endpoints.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapVersionManagementServerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // versioning.branch is built-experimental (ADR-0058 / #2346): the VMS surface is
        // implemented but gated OFF the GA surface by default. Routes return 404 when the
        // capability resolves experimental-disabled (the production default). Opt in via
        // Capabilities:Experimental:versioning.branch:Enabled=true (or the global master
        // switch Capabilities:Experimental:Enabled=true). This does NOT affect the
        // FeatureServer gdbVersion/replication paths — only the VMS REST surface.
        var group = endpoints.MapGroup(BasePath)
            .WithCapabilityGate("versioning.branch");

        // HANDLER-AUTHORIZED (#1144): every route below enforces authorization in its handler —
        // service read (Query) access for the read surface, plus the Enterprise branch-versioning
        // entitlement and service write authorization (Update + data-editor RBAC) for lifecycle
        // operations. Marked AllowAnonymous so the audit architecture guard records the
        // intentional decision, matching the sibling GeoServices endpoint files.
        group.MapGet("", HandleServiceInfo)
            .WithName("GetVersionManagementServiceInfo")
            .WithSummary("Get VersionManagementServer service metadata")
            .WithTags(Tag)
            .AllowAnonymous();

        group.MapGet("/versions", HandleListVersions)
            .WithName("ListVersions")
            .WithSummary("List branch versions")
            .WithTags(Tag)
            .AllowAnonymous();

        group.MapGet("/versions/{versionGuid}", HandleVersionInfo)
            .WithName("GetVersionInfo")
            .WithSummary("Get a single branch version's metadata")
            .WithTags(Tag)
            .AllowAnonymous();

        group.MapPost("/create", HandleCreate)
            .WithName("CreateVersion")
            .WithSummary("Create a branch version")
            .WithTags(Tag)
            .AllowAnonymous();

        group.MapPost("/versions/{versionGuid}/delete", HandleDelete)
            .WithName("DeleteVersion")
            .WithSummary("Delete a branch version")
            .WithTags(Tag)
            .AllowAnonymous();

        group.MapPost("/versions/{versionGuid}/alter", HandleAlter)
            .WithName("AlterVersion")
            .WithSummary("Alter a branch version's metadata")
            .WithTags(Tag)
            .AllowAnonymous();

        group.MapPost("/versions/{versionGuid}/startReading", HandleStartReading)
            .WithName("StartReadingVersion")
            .WithSummary("Begin a read session against a branch version")
            .WithTags(Tag)
            .AllowAnonymous();

        group.MapPost("/versions/{versionGuid}/stopReading", HandleStopReading)
            .WithName("StopReadingVersion")
            .WithSummary("End a read session against a branch version")
            .WithTags(Tag)
            .AllowAnonymous();

        group.MapPost("/versions/{versionGuid}/startEditing", HandleStartEditing)
            .WithName("StartEditingVersion")
            .WithSummary("Begin an edit session against a branch version")
            .WithTags(Tag)
            .AllowAnonymous();

        group.MapPost("/versions/{versionGuid}/stopEditing", HandleStopEditing)
            .WithName("StopEditingVersion")
            .WithSummary("End an edit session against a branch version")
            .WithTags(Tag)
            .AllowAnonymous();

        group.MapPost("/versions/{versionGuid}/reconcile", HandleReconcile)
            .WithName("ReconcileVersion")
            .WithSummary("Reconcile a branch version against DEFAULT")
            .WithTags(Tag)
            .AllowAnonymous();

        group.MapGet("/versions/{versionGuid}/inspectConflicts", HandleInspectConflicts)
            .WithName("InspectVersionConflicts")
            .WithSummary("Retrieve the pending conflict set for a branch version")
            .WithTags(Tag)
            .AllowAnonymous();

        group.MapPost("/versions/{versionGuid}/resolveConflicts", HandleResolveConflicts)
            .WithName("ResolveVersionConflicts")
            .WithSummary("Submit manual conflict resolutions for a branch version")
            .WithTags(Tag)
            .AllowAnonymous();

        group.MapPost("/versions/{versionGuid}/post", HandlePost)
            .WithName("PostVersion")
            .WithSummary("Post a reconciled branch version's changes onto DEFAULT")
            .WithTags(Tag)
            .AllowAnonymous();

        group.MapGet("/versions/{versionGuid}/jobs/{jobId}", HandleJobStatus)
            .WithName("GetVersionJobStatus")
            .WithSummary("Poll the status of an async reconcile/post job")
            .WithTags(Tag)
            .AllowAnonymous();

        return endpoints;
    }

    // ---- Read-only surface ---------------------------------------------------------------------

    private static async Task<IResult> HandleServiceInfo(
        string serviceId,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        CancellationToken cancellationToken)
    {
        var problem = await ValidateServiceAsync(serviceId, context, resourceValidator, cancellationToken)
            .ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        return Results.Json(new VersionManagementServiceInfo(),
            VersionManagementJsonContext.Default.VersionManagementServiceInfo,
            contentType: "application/json");
    }

    private static async Task<IResult> HandleListVersions(
        string serviceId,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IVersionManager versionManager,
        CancellationToken cancellationToken)
    {
        var problem = await ValidateServiceAsync(serviceId, context, resourceValidator, cancellationToken)
            .ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        var entitlementGate = LicenseGate.RequireEntitlement(
            context, FeatureCatalog.BranchVersioningKey, "Branch versioning");
        if (entitlementGate is not null)
        {
            return entitlementGate;
        }

        var versions = await versionManager.ListAsync(cancellationToken).ConfigureAwait(false);

        // BH3-002: filter the version set before projecting. Private versions are only
        // visible to their owner and service administrators; Public and Protected versions
        // are visible to any query-access caller.
        var callerName = context.User?.Identity?.Name;
        var isAdmin = ServiceDataEditorAuthorization.IsAdminPrincipal(context);
        var response = new VersionListResponse
        {
            Versions = versions
                .Where(v => VersionAccessPolicy.IsVersionVisible(v, callerName, isAdmin))
                .Select(ToVersionInfo)
                .ToArray(),
        };

        return Results.Json(response, VersionManagementJsonContext.Default.VersionListResponse,
            contentType: "application/json");
    }

    private static async Task<IResult> HandleVersionInfo(
        string serviceId,
        string versionGuid,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IVersionManager versionManager,
        CancellationToken cancellationToken)
    {
        var problem = await ValidateServiceAsync(serviceId, context, resourceValidator, cancellationToken)
            .ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        var entitlementGate = LicenseGate.RequireEntitlement(
            context, FeatureCatalog.BranchVersioningKey, "Branch versioning");
        if (entitlementGate is not null)
        {
            return entitlementGate;
        }

        if (!Guid.TryParse(versionGuid, out var versionId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "versionGuid is not a valid GUID.");
        }

        var versions = await versionManager.ListAsync(cancellationToken).ConfigureAwait(false);
        var match = versions.FirstOrDefault(v => v.VersionId == versionId);
        if (match.VersionId != versionId)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Version '{versionGuid}' was not found.");
        }

        // BH3-002: A Private version is only visible to its owner and service admins.
        // Return 404 (not 403) so a non-owner cannot confirm the version's existence.
        var callerName = context.User?.Identity?.Name;
        var isAdmin = ServiceDataEditorAuthorization.IsAdminPrincipal(context);
        if (!VersionAccessPolicy.IsVersionVisible(match, callerName, isAdmin))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Version '{versionGuid}' was not found.");
        }

        return Results.Json(ToVersionInfo(match), VersionManagementJsonContext.Default.VersionInfo,
            contentType: "application/json");
    }

    // ---- Lifecycle (Enterprise-gated, write-authorized) ----------------------------------------

    private static async Task<IResult> HandleCreate(
        string serviceId,
        HttpContext context,
        [FromServices] IVersionManager versionManager,
        CancellationToken cancellationToken)
    {
        var (gate, values) = await AuthorizeAndReadAsync(serviceId, context, versionManager, cancellationToken)
            .ConfigureAwait(false);
        if (gate is not null)
        {
            return gate;
        }

        var versionName = GeoServicesRequestValueHelpers.GetValueString(values!, "versionName");
        if (string.IsNullOrWhiteSpace(versionName))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "versionName parameter is required.");
        }

        var owner = GeoServicesRequestValueHelpers.GetValueString(values!, "owner")
            ?? ResolveOwner(context);
        var access = ParseAccess(GeoServicesRequestValueHelpers.GetValueString(values!, "accessPermission"));
        var description = GeoServicesRequestValueHelpers.GetValueString(values!, "description");

        var request = new CreateVersionRequest(versionName, owner, access, ParentVersion: null, Description: description);

        try
        {
            var created = await versionManager.CreateAsync(request, cancellationToken).ConfigureAwait(false);
            return Results.Json(new CreateVersionResponse { VersionInfo = ToVersionInfo(created) },
                VersionManagementJsonContext.Default.CreateVersionResponse,
                contentType: "application/json");
        }
        catch (DuplicateVersionNameException ex)
        {
            return StandardErrorHelpers.CreateConflict(
                context,
                ex.Message,
                [$"Version '{ex.VersionName}' already exists. Choose a different name or delete the existing version first."]);
        }
    }

    private static async Task<IResult> HandleDelete(
        string serviceId,
        string versionGuid,
        HttpContext context,
        [FromServices] IVersionManager versionManager,
        CancellationToken cancellationToken)
    {
        var (gate, _, versionId) = await AuthorizeReadAndResolveVersionAsync(
            serviceId, versionGuid, context, versionManager, cancellationToken).ConfigureAwait(false);
        if (gate is not null)
        {
            return gate;
        }

        // BH6-002: refuse to delete a version that is mid-reconcile or mid-post.
        // Calling DeleteAsync against a locked version produces undefined behavior in the version
        // store — the in-flight reconcile job may write its result to a deleted record, corrupt the
        // DEFAULT branch, or leave dangling change rows that can never be cleaned up. Return 409
        // so the caller retries once the operation completes, mirroring the state guard already
        // present in AcknowledgeSessionAsync (startEditing path).
        var versions = await versionManager.ListAsync(cancellationToken).ConfigureAwait(false);
        var version = versions.FirstOrDefault(v => v.VersionId == versionId);
        if (version.VersionId == versionId && version.State is VersionState.Reconciling or VersionState.Posting)
        {
            return StandardErrorHelpers.CreateConflict(
                context,
                $"Version '{version.VersionName}' is {StatusToString(version.State)} and cannot be deleted.",
                ["A reconcile or post is in progress for this version. Delete it once the operation completes."]);
        }

        var deleted = await versionManager.DeleteAsync(versionId, cancellationToken).ConfigureAwait(false);
        if (!deleted)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Version '{versionGuid}' was not found.");
        }

        return Moment(true);
    }

    private static async Task<IResult> HandleAlter(
        string serviceId,
        string versionGuid,
        HttpContext context,
        [FromServices] IVersionManager versionManager,
        CancellationToken cancellationToken)
    {
        var (gate, values, versionId) = await AuthorizeReadAndResolveVersionAsync(
            serviceId, versionGuid, context, versionManager, cancellationToken).ConfigureAwait(false);
        if (gate is not null)
        {
            return gate;
        }

        var newName = GeoServicesRequestValueHelpers.GetValueString(values!, "versionName");
        var accessRaw = GeoServicesRequestValueHelpers.GetValueString(values!, "accessPermission");
        var description = GeoServicesRequestValueHelpers.GetValueString(values!, "description");
        VersionAccess? access = string.IsNullOrWhiteSpace(accessRaw) ? null : ParseAccess(accessRaw);

        var request = new AlterVersionRequest(versionId, newName, access, description);
        var altered = await versionManager.AlterAsync(request, cancellationToken).ConfigureAwait(false);
        if (altered is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Version '{versionGuid}' was not found.");
        }

        return Moment(true);
    }

    private static Task<IResult> HandleStartReading(
        string serviceId,
        string versionGuid,
        HttpContext context,
        [FromServices] IVersionManager versionManager,
        CancellationToken cancellationToken)
        => AcknowledgeSessionAsync(serviceId, versionGuid, context, versionManager, requireWritable: false, cancellationToken);

    private static Task<IResult> HandleStopReading(
        string serviceId,
        string versionGuid,
        HttpContext context,
        [FromServices] IVersionManager versionManager,
        CancellationToken cancellationToken)
        => AcknowledgeSessionAsync(serviceId, versionGuid, context, versionManager, requireWritable: false, cancellationToken);

    private static Task<IResult> HandleStartEditing(
        string serviceId,
        string versionGuid,
        HttpContext context,
        [FromServices] IVersionManager versionManager,
        CancellationToken cancellationToken)
        => AcknowledgeSessionAsync(serviceId, versionGuid, context, versionManager, requireWritable: true, cancellationToken);

    private static Task<IResult> HandleStopEditing(
        string serviceId,
        string versionGuid,
        HttpContext context,
        [FromServices] IVersionManager versionManager,
        CancellationToken cancellationToken)
        => AcknowledgeSessionAsync(serviceId, versionGuid, context, versionManager, requireWritable: false, cancellationToken);

    private static async Task<IResult> HandleReconcile(
        string serviceId,
        string versionGuid,
        HttpContext context,
        [FromServices] IVersionManager versionManager,
        [FromServices] IVersionJobRunner jobRunner,
        CancellationToken cancellationToken)
    {
        var (gate, values, versionId) = await AuthorizeReadAndResolveVersionAsync(
            serviceId, versionGuid, context, versionManager, cancellationToken).ConfigureAwait(false);
        if (gate is not null)
        {
            return gate;
        }

        var (policy, policyError) = ParseReconcilePolicy(context, values!);
        if (policyError is not null)
        {
            return policyError;
        }

        var (detection, detectionError) = ParseConflictDetection(context, values!);
        if (detectionError is not null)
        {
            return detectionError;
        }

        // Async fast path: start a durable, pollable job under the version lock and return 202 with a
        // job handle. The synchronous path stays the default for small/fast versions (#1553).
        if (ParseAsyncRequested(values!))
        {
            var job = await jobRunner.StartReconcileAsync(serviceId, versionId, policy, detection, cancellationToken)
                .ConfigureAwait(false);
            return AcceptedJob(serviceId, versionGuid, job);
        }

        var withPost = ParseFlag(values!, "withPost");

        try
        {
            var result = await versionManager.ReconcileAsync(versionId, policy, detection, cancellationToken)
                .ConfigureAwait(false);
            var hasConflicts = !result.Conflicts.IsDefaultOrEmpty && result.Conflicts.Length > 0;

            // withPost=true posts the version in the same operation after a clean reconcile (Esri
            // conformance; #2135). When conflicts remain it is a no-op-with-conflicts response: the
            // reconcile conflicts are returned and nothing is posted.
            var posted = false;
            var appliedChanges = 0;
            long serverGeneration = 0;
            if (withPost && result.CanPost && !hasConflicts)
            {
                var post = await versionManager.PostAsync(versionId, cancellationToken).ConfigureAwait(false);
                posted = post.Posted;
                appliedChanges = post.AppliedChanges;
                serverGeneration = post.ServerGeneration;
            }

            var response = new ReconcileResponse
            {
                HasConflicts = hasConflicts,
                CanPost = result.CanPost,
                AutoResolvedCount = result.AutoResolvedCount,
                Posted = posted,
                AppliedChanges = appliedChanges,
                ServerGeneration = serverGeneration,
                Conflicts = result.Conflicts.IsDefaultOrEmpty
                    ? []
                    : result.Conflicts.Select(ToConflictInfo).ToArray(),
            };

            return Results.Json(response, VersionManagementJsonContext.Default.ReconcileResponse,
                contentType: "application/json");
        }
        catch (VersionLockedException ex)
        {
            return InProgress(context, ex);
        }
    }

    private static async Task<IResult> HandleInspectConflicts(
        string serviceId,
        string versionGuid,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IVersionManager versionManager,
        CancellationToken cancellationToken)
    {
        // Read-only review surface: gate on the entitlement + service validation, but no write auth and
        // no request body (GET). Resolve the version directly.
        var problem = await ValidateServiceAsync(serviceId, context, resourceValidator, cancellationToken)
            .ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        var entitlementGate = LicenseGate.RequireEntitlement(
            context, FeatureCatalog.BranchVersioningKey, "Branch versioning");
        if (entitlementGate is not null)
        {
            return entitlementGate;
        }

        if (!versionManager.SupportsVersioning)
        {
            return StandardErrorHelpers.CreateNotImplemented(
                context,
                "Branch versioning is not supported by the configured data provider.",
                ["Branch versioning requires a PostgreSQL/PostGIS feature provider."]);
        }

        if (!Guid.TryParse(versionGuid, out var versionId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "versionGuid is not a valid GUID.");
        }

        // BH3-003 / BH6-001: conflict records contain full before/after attribute and geometry
        // images. Load the version record and enforce ownership before exposing conflict data.
        // Private AND Protected versions require owner-or-admin (CanManageVersion). Only Public
        // versions (whose conflict state is intentionally world-readable by design) bypass the
        // ownership check. A comment at the original site incorrectly equated query-level access
        // with authorization to view raw conflict diffs for Protected versions — BH6-001 corrects
        // the conditional so Protected versions are now gated on CanManageVersion, matching the
        // sibling HandleResolveConflicts which checks unconditionally via AuthorizeReadAndResolveVersionAsync.
        var allVersions = await versionManager.ListAsync(cancellationToken).ConfigureAwait(false);
        var conflictVersion = allVersions.FirstOrDefault(v => v.VersionId == versionId);
        if (conflictVersion.VersionId != versionId)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Version '{versionGuid}' was not found.");
        }

        if (conflictVersion.Access is VersionAccess.Private or VersionAccess.Protected)
        {
            var callerName = context.User?.Identity?.Name;
            var isAdmin = ServiceDataEditorAuthorization.IsAdminPrincipal(context);
            if (!VersionAccessPolicy.CanManageVersion(conflictVersion, callerName, isAdmin))
            {
                return StandardErrorHelpers.CreateForbidden(context, AccessPolicyHelpers.AccessForbiddenMessage);
            }
        }

        var conflicts = await versionManager.GetPendingConflictsAsync(versionId, cancellationToken).ConfigureAwait(false);
        var response = new InspectConflictsResponse
        {
            HasConflicts = conflicts.Count > 0,
            Conflicts = conflicts.Select(ToConflictInfo).ToArray(),
        };

        return Results.Json(response, VersionManagementJsonContext.Default.InspectConflictsResponse,
            contentType: "application/json");
    }

    private static async Task<IResult> HandleResolveConflicts(
        string serviceId,
        string versionGuid,
        HttpContext context,
        [FromServices] IVersionManager versionManager,
        CancellationToken cancellationToken)
    {
        var (gate, values, versionId) = await AuthorizeReadAndResolveVersionAsync(
            serviceId, versionGuid, context, versionManager, cancellationToken).ConfigureAwait(false);
        if (gate is not null)
        {
            return gate;
        }

        var (resolutions, parseError) = ParseResolutions(context, values!);
        if (parseError is not null)
        {
            return parseError;
        }

        var result = await versionManager.ResolveConflictsAsync(versionId, resolutions!, cancellationToken)
            .ConfigureAwait(false);
        var response = new ResolveConflictsResponse
        {
            Resolved = result.Resolved,
            Remaining = result.Remaining,
            CanPost = result.CanPost,
        };

        return Results.Json(response, VersionManagementJsonContext.Default.ResolveConflictsResponse,
            contentType: "application/json");
    }

    private static async Task<IResult> HandlePost(
        string serviceId,
        string versionGuid,
        HttpContext context,
        [FromServices] IVersionManager versionManager,
        [FromServices] IVersionJobRunner jobRunner,
        CancellationToken cancellationToken)
    {
        var (gate, values, versionId) = await AuthorizeReadAndResolveVersionAsync(
            serviceId, versionGuid, context, versionManager, cancellationToken).ConfigureAwait(false);
        if (gate is not null)
        {
            return gate;
        }

        // Async fast path: start a durable, pollable post job under the version lock (#1553).
        if (ParseAsyncRequested(values!))
        {
            var job = await jobRunner.StartPostAsync(serviceId, versionId, cancellationToken).ConfigureAwait(false);
            return AcceptedJob(serviceId, versionGuid, job);
        }

        try
        {
            var result = await versionManager.PostAsync(versionId, cancellationToken).ConfigureAwait(false);
            var response = new PostResponse
            {
                Success = result.Posted,
                AppliedChanges = result.AppliedChanges,
                ServerGeneration = result.ServerGeneration,
                BlockedByConflicts = result.BlockedByConflicts,
            };

            return Results.Json(response, VersionManagementJsonContext.Default.PostResponse,
                contentType: "application/json");
        }
        catch (VersionLockedException ex)
        {
            return InProgress(context, ex);
        }
    }

    private static async Task<IResult> HandleJobStatus(
        string serviceId,
        string versionGuid,
        string jobId,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IVersionManager versionManager,
        [FromServices] IVersionJobRunner jobRunner,
        CancellationToken cancellationToken)
    {
        // Read-only poll: gate on the entitlement + service validation only (no write auth, no body).
        var problem = await ValidateServiceAsync(serviceId, context, resourceValidator, cancellationToken)
            .ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        var entitlementGate = LicenseGate.RequireEntitlement(
            context, FeatureCatalog.BranchVersioningKey, "Branch versioning");
        if (entitlementGate is not null)
        {
            return entitlementGate;
        }

        if (!versionManager.SupportsVersioning)
        {
            return StandardErrorHelpers.CreateNotImplemented(
                context,
                "Branch versioning is not supported by the configured data provider.",
                ["Branch versioning requires a PostgreSQL/PostGIS feature provider."]);
        }

        if (!Guid.TryParse(versionGuid, out var versionId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "versionGuid is not a valid GUID.");
        }

        if (!Guid.TryParse(jobId, out var jobGuid))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "jobId is not a valid GUID.");
        }

        var job = await jobRunner.GetJobAsync(jobGuid, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Version job '{jobId}' was not found.");
        }

        // Reject jobs that do not belong to the route's service/version (mirrors GPServer's
        // ValidateJobBinding) so a caller authorized on one service cannot poll another
        // service's reconcile/post jobs by GUID.
        if (!string.Equals(job.Service, serviceId, StringComparison.OrdinalIgnoreCase) ||
            job.VersionId != versionId)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Version job '{jobId}' was not found.");
        }

        return Results.Json(ToJobResponse(serviceId, versionGuid, job),
            VersionManagementJsonContext.Default.VersionJobResponse, contentType: "application/json");
    }

    // ---- Shared adapter plumbing ---------------------------------------------------------------

    /// <summary>
    /// Handles the <c>startReading</c>/<c>stopReading</c>/<c>startEditing</c>/<c>stopEditing</c>
    /// session acknowledgements. Honua threads the version per-request via <c>gdbVersion</c>
    /// (overlay/moment model), so there is no server-held read/edit session to open or close. The
    /// acknowledgement is nonetheless made meaningful: it resolves the named version, returns its
    /// durable branch generation as the read/edit moment (a stable cursor an Esri client can echo on
    /// subsequent reads), and — when the version is mid-reconcile/post (locked) — refuses an
    /// edit-session open with a 409 in-progress instead of returning a false success. A
    /// <paramref name="requireWritable"/> session against a transitional version is rejected; a
    /// read/stop session reports the current moment regardless of state.
    /// </summary>
    private static async Task<IResult> AcknowledgeSessionAsync(
        string serviceId,
        string versionGuid,
        HttpContext context,
        IVersionManager versionManager,
        bool requireWritable,
        CancellationToken cancellationToken)
    {
        var (gate, _, versionId) = await AuthorizeReadAndResolveVersionAsync(
            serviceId, versionGuid, context, versionManager, cancellationToken).ConfigureAwait(false);
        if (gate is not null)
        {
            return gate;
        }

        var versions = await versionManager.ListAsync(cancellationToken).ConfigureAwait(false);
        var version = versions.FirstOrDefault(v => v.VersionId == versionId);
        if (version.VersionId != versionId)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Version '{versionGuid}' was not found.");
        }

        // Opening an edit session against a version that is mid-reconcile/post would be a false
        // acknowledgement: the version is locked (Redis-backed) for the duration of that job, so no
        // edit can land. Refuse with a 409 in-progress, matching reconcile/post lock contention.
        if (requireWritable && version.State is VersionState.Reconciling or VersionState.Posting)
        {
            return StandardErrorHelpers.CreateConflict(
                context,
                $"Version '{version.VersionName}' is {StatusToString(version.State)} and cannot be edited.",
                ["A reconcile or post is in progress for this version. Open an edit session once it completes."]);
        }

        // The version's durable branch generation is the read/edit moment: a stable cursor the client
        // can echo to pin a consistent snapshot, rather than a throwaway wall-clock value.
        return Moment(version.BranchGeneration);
    }

    private static IResult Moment(bool success) =>
        Results.Json(
            new VersionMomentResponse { Success = success, Moment = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            VersionManagementJsonContext.Default.VersionMomentResponse,
            contentType: "application/json");

    private static IResult Moment(long moment) =>
        Results.Json(
            new VersionMomentResponse { Success = true, Moment = moment },
            VersionManagementJsonContext.Default.VersionMomentResponse,
            contentType: "application/json");

    private static async Task<IResult?> ValidateServiceAsync(
        string serviceId,
        HttpContext context,
        IResourceValidator resourceValidator,
        CancellationToken cancellationToken)
    {
        var validation = await FeatureServerResourceValidationHelpers.ValidateServiceV2Async(
            resourceValidator, serviceId, context, logger: null, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return validation.ErrorResult!;
        }

        // Read-surface RBAC parity with the sibling FeatureServer/GPServer handlers (#1376): version
        // metadata (owners, names) and conflict payloads (attribute/geometry images) must not be
        // readable on a service the caller cannot query. Write operations layer the stricter
        // Update + data-editor checks on top via VersionManagementAuthorization.
        return await AccessPolicyHelpers.RequireServiceAccessAsync(
            context, validation.Service!, AuthorizationOperation.Query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Enterprise-gates the version-management surface, enforces service write authorization, and
    /// reads the request body. Returns a non-null gate result to short-circuit on any failure.
    /// </summary>
    private static async Task<(IResult? Gate, IReadOnlyDictionary<string, StringValues>? Values)> AuthorizeAndReadAsync(
        string serviceId,
        HttpContext context,
        IVersionManager versionManager,
        CancellationToken cancellationToken)
    {
        var entitlementGate = LicenseGate.RequireEntitlement(
            context, FeatureCatalog.BranchVersioningKey, "Branch versioning");
        if (entitlementGate is not null)
        {
            return (entitlementGate, null);
        }

        if (!versionManager.SupportsVersioning)
        {
            return (StandardErrorHelpers.CreateNotImplemented(
                context,
                "Branch versioning is not supported by the configured data provider.",
                ["Branch versioning requires a PostgreSQL/PostGIS feature provider."]), null);
        }

        var writeError = await VersionManagementAuthorization.RequireServiceWriteAccessAsync(
            serviceId, context, cancellationToken).ConfigureAwait(false);
        if (writeError is not null)
        {
            return (writeError, null);
        }

        var (values, readError) = await GeoServicesRequestValueHelpers.TryReadRequestValuesAsync(
            context.Request, cancellationToken).ConfigureAwait(false);
        if (values is null)
        {
            if (GeoServicesRequestValueHelpers.TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return (GeoServicesRequestValueHelpers.CreateUnsupportedRequestContentTypeResult(context, receivedContentType), null);
            }

            return (StandardErrorHelpers.CreateBadRequest(
                context, "Invalid request body.", [readError ?? "Invalid request body."]), null);
        }

        return (null, values);
    }

    private static async Task<(IResult? Gate, IReadOnlyDictionary<string, StringValues>? Values, Guid VersionId)>
        AuthorizeReadAndResolveVersionAsync(
            string serviceId,
            string versionGuid,
            HttpContext context,
            IVersionManager versionManager,
            CancellationToken cancellationToken)
    {
        var (gate, values) = await AuthorizeAndReadAsync(serviceId, context, versionManager, cancellationToken)
            .ConfigureAwait(false);
        if (gate is not null)
        {
            return (gate, null, Guid.Empty);
        }

        if (!Guid.TryParse(versionGuid, out var versionId))
        {
            return (StandardErrorHelpers.CreateBadRequest(context, "versionGuid is not a valid GUID."), null, Guid.Empty);
        }

        // BH3-004: lifecycle operations (delete, alter, reconcile, post, resolveConflicts,
        // start/stop editing/reading) are restricted to the version owner and service admins,
        // regardless of the version's access level. Load the version and enforce ownership here
        // so every lifecycle handler gets the check for free via this shared entry point.
        var ownershipGate = await RequireVersionOwnerOrAdminAsync(
            context, versionManager, versionId, versionGuid, cancellationToken).ConfigureAwait(false);
        if (ownershipGate is not null)
        {
            return (ownershipGate, null, Guid.Empty);
        }

        return (null, values, versionId);
    }

    /// <summary>
    /// Loads the version with <paramref name="versionId"/> and verifies that the caller is its
    /// owner or holds the administrator role. Returns a non-null error result when denied,
    /// <see langword="null"/> when authorized.
    /// </summary>
    private static async Task<IResult?> RequireVersionOwnerOrAdminAsync(
        HttpContext context,
        IVersionManager versionManager,
        Guid versionId,
        string versionGuidString,
        CancellationToken cancellationToken)
    {
        var versions = await versionManager.ListAsync(cancellationToken).ConfigureAwait(false);
        var version = versions.FirstOrDefault(v => v.VersionId == versionId);
        if (version.VersionId != versionId)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Version '{versionGuidString}' was not found.");
        }

        var callerName = context.User?.Identity?.Name;
        var isAdmin = ServiceDataEditorAuthorization.IsAdminPrincipal(context);
        if (!VersionAccessPolicy.CanManageVersion(version, callerName, isAdmin))
        {
            return StandardErrorHelpers.CreateForbidden(context, AccessPolicyHelpers.AccessForbiddenMessage);
        }

        return null;
    }

    private static string ResolveOwner(HttpContext context)
    {
        var name = context.User?.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? "admin" : name;
    }

    private static VersionAccess ParseAccess(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "public" => VersionAccess.Public,
        "protected" => VersionAccess.Protected,
        "private" => VersionAccess.Private,
        _ => VersionAccess.Private,
    };

    private static string AccessToString(VersionAccess access) => access switch
    {
        VersionAccess.Public => "public",
        VersionAccess.Protected => "protected",
        _ => "private",
    };

    private static string StatusToString(VersionState state) => state switch
    {
        VersionState.Reconciling => "reconciling",
        VersionState.Posting => "posting",
        VersionState.Deleted => "deleted",
        _ => "active",
    };

    private static VersionInfo ToVersionInfo(GdbVersion version) => new()
    {
        VersionGuid = version.VersionId.ToString(),
        VersionName = version.VersionName,
        Owner = version.Owner,
        Access = AccessToString(version.Access),
        Status = StatusToString(version.State),
        Description = version.Description,
        ParentVersionGuid = version.ParentVersion?.ToString(),
        CreationMoment = version.CreatedAt.ToUnixTimeMilliseconds(),
        ModifiedMoment = version.ModifiedAt.ToUnixTimeMilliseconds(),
    };

    private static VersionConflictInfo ToConflictInfo(VersionReconcileConflict conflict) => new()
    {
        LayerId = conflict.LayerId,
        ObjectId = conflict.ObjectId,
        ConflictType = ConflictTypeToString(conflict.ConflictType),
        BaseAttributes = conflict.BaseAttributesJson,
        DefaultAttributes = conflict.DefaultAttributesJson,
        VersionAttributes = conflict.VersionAttributesJson,
        BaseGeometry = conflict.BaseGeometryWkt,
        DefaultGeometry = conflict.DefaultGeometryWkt,
        VersionGeometry = conflict.VersionGeometryWkt,
        FieldDiffs = conflict.FieldDiffs.IsDefaultOrEmpty
            ? []
            : conflict.FieldDiffs.Select(d => new VersionConflictFieldDiffInfo
            {
                Name = d.Name,
                Base = d.Base,
                Default = d.Default,
                Version = d.Version,
            }).ToArray(),
    };

    /// <summary>
    /// Parses the reconcile auto-resolution policy from the request. Accepts Honua's named policies
    /// (<c>none</c>/<c>lastWriteWins</c>/<c>versionWins</c>/<c>defaultWins</c>) on a <c>conflictResolution</c>
    /// (or <c>resolutionPolicy</c>) parameter, and maps the Esri-style <c>abortIfConflicts</c> flag to
    /// the manual (none) policy when set. Absent or empty resolves to <see cref="VersionReconcilePolicy.None"/>.
    /// </summary>
    private static (VersionReconcilePolicy Policy, IResult? Error) ParseReconcilePolicy(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values)
    {
        var raw = GeoServicesRequestValueHelpers.GetValueString(values, "conflictResolution")
            ?? GeoServicesRequestValueHelpers.GetValueString(values, "resolutionPolicy");

        // abortIfConflicts=true is Esri's "do not auto-resolve" intent; force manual review.
        var abortRaw = GeoServicesRequestValueHelpers.GetValueString(values, "abortIfConflicts");
        if (string.Equals(abortRaw?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return (VersionReconcilePolicy.None, null);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return (VersionReconcilePolicy.None, null);
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "none" or "manual" => (VersionReconcilePolicy.None, null),
            "lastwritewins" or "last-write-wins" => (VersionReconcilePolicy.LastWriteWins, null),
            "versionwins" or "version-wins" or "favoreditversion" => (VersionReconcilePolicy.VersionWins, null),
            "defaultwins" or "default-wins" or "favortargetversion" => (VersionReconcilePolicy.DefaultWins, null),
            _ => (VersionReconcilePolicy.None, StandardErrorHelpers.CreateBadRequest(
                context,
                "Unsupported conflictResolution policy.",
                ["Supported policies: none, lastWriteWins, versionWins, defaultWins."])),
        };
    }

    /// <summary>
    /// Parses the reconcile conflict-detection granularity from the <c>conflictDetection</c> parameter
    /// (Esri conformance; #2135). Accepts Honua's short names (<c>byObject</c>/<c>byAttribute</c>), the
    /// Esri tokens (<c>esriReconcileByObject</c>/<c>esriReconcileByAttribute</c>), and the bare
    /// <c>object</c>/<c>attribute</c> forms. Absent or empty resolves to the
    /// <see cref="VersionConflictDetection.ByAttribute"/> default (pre-#2135 behavior).
    /// </summary>
    private static (VersionConflictDetection Detection, IResult? Error) ParseConflictDetection(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values)
    {
        var raw = GeoServicesRequestValueHelpers.GetValueString(values, "conflictDetection");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (VersionConflictDetection.ByAttribute, null);
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "byattribute" or "esrireconcilebyattribute" or "attribute" => (VersionConflictDetection.ByAttribute, null),
            "byobject" or "esrireconcilebyobject" or "object" => (VersionConflictDetection.ByObject, null),
            _ => (VersionConflictDetection.ByAttribute, StandardErrorHelpers.CreateBadRequest(
                context,
                "Unsupported conflictDetection mode.",
                ["Supported modes: byObject, byAttribute."])),
        };
    }

    /// <summary>
    /// Whether a boolean request flag (e.g. <c>withPost</c>) is set to <c>true</c> (case-insensitive).
    /// Absent or any other value is treated as false.
    /// </summary>
    private static bool ParseFlag(IReadOnlyDictionary<string, StringValues> values, string name)
    {
        var raw = GeoServicesRequestValueHelpers.GetValueString(values, name);
        return string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses the per-feature resolution choices from the <c>conflicts</c> JSON-array parameter. Each
    /// element is <c>{ "layerId": int, "objectId": long, "choice": "version|default|base" }</c>.
    /// </summary>
    private static (IReadOnlyList<VersionConflictResolution>? Resolutions, IResult? Error) ParseResolutions(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values)
    {
        var raw = GeoServicesRequestValueHelpers.GetValueString(values, "conflicts")
            ?? GeoServicesRequestValueHelpers.GetValueString(values, "resolutions");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(
                context, "conflicts parameter is required.",
                ["Provide a JSON array of { layerId, objectId, choice } resolution objects."]));
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return (null, StandardErrorHelpers.CreateBadRequest(context, "conflicts must be a JSON array."));
            }

            var resolutions = new List<VersionConflictResolution>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !element.TryGetProperty("layerId", out var layerProp) ||
                    !element.TryGetProperty("objectId", out var objectProp) ||
                    !element.TryGetProperty("choice", out var choiceProp))
                {
                    return (null, StandardErrorHelpers.CreateBadRequest(
                        context, "Each conflict resolution requires layerId, objectId, and choice."));
                }

                if (choiceProp.ValueKind != System.Text.Json.JsonValueKind.String ||
                    !TryParseChoice(choiceProp.GetString(), out var choice))
                {
                    return (null, StandardErrorHelpers.CreateBadRequest(
                        context, "Unsupported resolution choice.",
                        ["Supported choices: version, default, base."]));
                }

                // Guard the value kinds before reading: GetInt32/GetInt64 throw
                // InvalidOperationException (not a JsonException) on non-numeric kinds, which
                // would surface as a 500 instead of the intended 400.
                if (layerProp.ValueKind != System.Text.Json.JsonValueKind.Number ||
                    !layerProp.TryGetInt32(out var layerId) ||
                    objectProp.ValueKind != System.Text.Json.JsonValueKind.Number ||
                    !objectProp.TryGetInt64(out var objectId))
                {
                    return (null, StandardErrorHelpers.CreateBadRequest(
                        context, "conflicts contains an invalid layerId/objectId."));
                }

                resolutions.Add(new VersionConflictResolution(layerId, objectId, choice));
            }

            return (resolutions, null);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, "conflicts is not valid JSON."));
        }
    }

    private static bool TryParseChoice(string? raw, out VersionConflictResolutionChoice choice)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "version":
            case "takeversion":
                choice = VersionConflictResolutionChoice.TakeVersion;
                return true;
            case "default":
            case "takedefault":
                choice = VersionConflictResolutionChoice.TakeDefault;
                return true;
            case "base":
            case "takebase":
                choice = VersionConflictResolutionChoice.TakeBase;
                return true;
            default:
                choice = VersionConflictResolutionChoice.TakeVersion;
                return false;
        }
    }

    /// <summary>
    /// Whether the caller requested asynchronous (job-wrapped) reconcile/post execution via an
    /// <c>async=true</c> flag or <c>mode=async</c> parameter (#1553). Absent or any other value runs the
    /// synchronous fast path.
    /// </summary>
    private static bool ParseAsyncRequested(IReadOnlyDictionary<string, StringValues> values)
    {
        var asyncRaw = GeoServicesRequestValueHelpers.GetValueString(values, "async");
        if (string.Equals(asyncRaw?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var modeRaw = GeoServicesRequestValueHelpers.GetValueString(values, "mode");
        return string.Equals(modeRaw?.Trim(), "async", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the HTTP 202 Accepted response carrying the started job handle and its status-poll URL.
    /// </summary>
    private static IResult AcceptedJob(string serviceId, string versionGuid, VersionJob job)
    {
        var response = ToJobResponse(serviceId, versionGuid, job);
        return Results.Json(response, VersionManagementJsonContext.Default.VersionJobResponse,
            statusCode: StatusCodes.Status202Accepted, contentType: "application/json");
    }

    /// <summary>
    /// Maps a contended version lock to a 409 in-progress response so the caller knows another
    /// reconcile/post for the same version is already running (#1553).
    /// </summary>
    private static IResult InProgress(HttpContext context, VersionLockedException ex)
        => StandardErrorHelpers.CreateConflict(
            context,
            ex.Message,
            ["A reconcile or post for this version is already in progress. Retry once it completes, or poll the in-flight job."]);

    private static VersionJobResponse ToJobResponse(string serviceId, string versionGuid, VersionJob job) => new()
    {
        JobId = job.JobId.ToString(),
        Kind = job.Kind == VersionJobKind.Reconcile ? "reconcile" : "post",
        Status = JobStatusToString(job.Status),
        StatusUrl = $"/rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/jobs/{job.JobId}",
        ConflictCount = job.ConflictCount,
        AutoResolvedCount = job.AutoResolvedCount,
        CanPost = job.CanPost,
        AppliedChanges = job.AppliedChanges,
        ServerGeneration = job.ServerGeneration,
        BlockedByConflicts = job.BlockedByConflicts,
        Error = job.ErrorMessage,
    };

    private static string JobStatusToString(VersionJobStatus status) => status switch
    {
        VersionJobStatus.Pending => "pending",
        VersionJobStatus.Running => "running",
        VersionJobStatus.Succeeded => "succeeded",
        VersionJobStatus.Failed => "failed",
        VersionJobStatus.LockContended => "lockContended",
        _ => status.ToString().ToLower(CultureInfo.InvariantCulture),
    };

    private static string ConflictTypeToString(ReplicaConflictType type) => type switch
    {
        ReplicaConflictType.Attribute => "attribute",
        ReplicaConflictType.Geometry => "geometry",
        ReplicaConflictType.DeleteUpdate => "deleteUpdate",
        ReplicaConflictType.UpdateDelete => "updateDelete",
        ReplicaConflictType.DuplicateInsert => "duplicateInsert",
        ReplicaConflictType.Attachment => "attachment",
        ReplicaConflictType.Relationship => "relationship",
        _ => type.ToString().ToLower(CultureInfo.InvariantCulture),
    };
}
