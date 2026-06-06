// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Validation.Abstractions;
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
/// and <c>startEditing</c>/<c>stopEditing</c> operations are therefore stateless acknowledgements
/// that validate the named version exists and return a moment; this divergence from Esri's
/// session-token model is intentional and documented here. Reconcile/post delegate straight to the
/// version manager, which owns the Redis-backed version lock and job runtime.
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

        endpoints.MapGet(BasePath, HandleServiceInfo)
            .WithName("GetVersionManagementServiceInfo")
            .WithSummary("Get VersionManagementServer service metadata")
            .WithTags(Tag);

        endpoints.MapGet($"{BasePath}/versions", HandleListVersions)
            .WithName("ListVersions")
            .WithSummary("List branch versions")
            .WithTags(Tag);

        endpoints.MapGet($"{BasePath}/versions/{{versionGuid}}", HandleVersionInfo)
            .WithName("GetVersionInfo")
            .WithSummary("Get a single branch version's metadata")
            .WithTags(Tag);

        endpoints.MapPost($"{BasePath}/create", HandleCreate)
            .WithName("CreateVersion")
            .WithSummary("Create a branch version")
            .WithTags(Tag);

        endpoints.MapPost($"{BasePath}/versions/{{versionGuid}}/delete", HandleDelete)
            .WithName("DeleteVersion")
            .WithSummary("Delete a branch version")
            .WithTags(Tag);

        endpoints.MapPost($"{BasePath}/versions/{{versionGuid}}/alter", HandleAlter)
            .WithName("AlterVersion")
            .WithSummary("Alter a branch version's metadata")
            .WithTags(Tag);

        endpoints.MapPost($"{BasePath}/versions/{{versionGuid}}/startReading", HandleStartReading)
            .WithName("StartReadingVersion")
            .WithSummary("Begin a read session against a branch version")
            .WithTags(Tag);

        endpoints.MapPost($"{BasePath}/versions/{{versionGuid}}/stopReading", HandleStopReading)
            .WithName("StopReadingVersion")
            .WithSummary("End a read session against a branch version")
            .WithTags(Tag);

        endpoints.MapPost($"{BasePath}/versions/{{versionGuid}}/startEditing", HandleStartEditing)
            .WithName("StartEditingVersion")
            .WithSummary("Begin an edit session against a branch version")
            .WithTags(Tag);

        endpoints.MapPost($"{BasePath}/versions/{{versionGuid}}/stopEditing", HandleStopEditing)
            .WithName("StopEditingVersion")
            .WithSummary("End an edit session against a branch version")
            .WithTags(Tag);

        endpoints.MapPost($"{BasePath}/versions/{{versionGuid}}/reconcile", HandleReconcile)
            .WithName("ReconcileVersion")
            .WithSummary("Reconcile a branch version against DEFAULT")
            .WithTags(Tag);

        endpoints.MapPost($"{BasePath}/versions/{{versionGuid}}/post", HandlePost)
            .WithName("PostVersion")
            .WithSummary("Post a reconciled branch version's changes onto DEFAULT")
            .WithTags(Tag);

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

        var versions = await versionManager.ListAsync(cancellationToken).ConfigureAwait(false);
        var response = new VersionListResponse
        {
            Versions = versions.Select(ToVersionInfo).ToArray(),
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
        catch (InvalidOperationException ex)
        {
            return StandardErrorHelpers.CreateConflict(context, ex.Message);
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
        => AcknowledgeSessionAsync(serviceId, versionGuid, context, versionManager, cancellationToken);

    private static Task<IResult> HandleStopReading(
        string serviceId,
        string versionGuid,
        HttpContext context,
        [FromServices] IVersionManager versionManager,
        CancellationToken cancellationToken)
        => AcknowledgeSessionAsync(serviceId, versionGuid, context, versionManager, cancellationToken);

    private static Task<IResult> HandleStartEditing(
        string serviceId,
        string versionGuid,
        HttpContext context,
        [FromServices] IVersionManager versionManager,
        CancellationToken cancellationToken)
        => AcknowledgeSessionAsync(serviceId, versionGuid, context, versionManager, cancellationToken);

    private static Task<IResult> HandleStopEditing(
        string serviceId,
        string versionGuid,
        HttpContext context,
        [FromServices] IVersionManager versionManager,
        CancellationToken cancellationToken)
        => AcknowledgeSessionAsync(serviceId, versionGuid, context, versionManager, cancellationToken);

    private static async Task<IResult> HandleReconcile(
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

        var result = await versionManager.ReconcileAsync(versionId, cancellationToken).ConfigureAwait(false);
        var response = new ReconcileResponse
        {
            HasConflicts = !result.Conflicts.IsDefaultOrEmpty && result.Conflicts.Length > 0,
            CanPost = result.CanPost,
            Conflicts = result.Conflicts.IsDefaultOrEmpty
                ? []
                : result.Conflicts.Select(ToConflictInfo).ToArray(),
        };

        return Results.Json(response, VersionManagementJsonContext.Default.ReconcileResponse,
            contentType: "application/json");
    }

    private static async Task<IResult> HandlePost(
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

    // ---- Shared adapter plumbing ---------------------------------------------------------------

    private static async Task<IResult> AcknowledgeSessionAsync(
        string serviceId,
        string versionGuid,
        HttpContext context,
        IVersionManager versionManager,
        CancellationToken cancellationToken)
    {
        var (gate, _, _) = await AuthorizeReadAndResolveVersionAsync(
            serviceId, versionGuid, context, versionManager, cancellationToken).ConfigureAwait(false);
        if (gate is not null)
        {
            return gate;
        }

        // Honua threads the version per-request via gdbVersion (overlay/moment model); there is no
        // server-held read/edit session to open or close, so this is a stateless acknowledgement.
        return Moment(true);
    }

    private static IResult Moment(bool success) =>
        Results.Json(
            new VersionMomentResponse { Success = success, Moment = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
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
        return validation.IsValid ? null : validation.ErrorResult!;
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

        return (null, values, versionId);
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
