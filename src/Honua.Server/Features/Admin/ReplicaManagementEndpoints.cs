// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Console;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin/operator endpoints for inspecting named disconnected-sync replicas and reviewing and
/// resolving disconnected-sync conflicts (#1167).
/// </summary>
/// <remarks>
/// <para>
/// This is the operator-facing management surface that mirrors the Esri offline/disconnected
/// editing shape: clients create named replicas via the GeoServices FeatureServer
/// <c>createReplica</c>/<c>synchronizeReplica</c> REST endpoints, and operators review the
/// active replicas (name, owner-supplied metadata, layers, sync model, last sync time, derived
/// status, and the server generation cursor) here. It reads durable replica state from
/// <see cref="IReplicaRepository"/> and durable conflict records from
/// <see cref="IReplicaConflictRepository"/>; it does not create, mutate, or synchronize replicas.
/// </para>
/// <para>
/// Conflict review (slice 2, #1167) adds: list conflicts for a replica, conflict detail with
/// base/client/server feature states, and a resolution endpoint that applies an operator-selected
/// resolution, records audit evidence, and reports whether a new committed server state was
/// produced. Providers that cannot support manual conflict review (read-only analytics providers)
/// report <see cref="IReplicaConflictRepository.SupportsConflictReview"/> as <c>false</c>, in which
/// case the conflict endpoints return a not-supported denial rather than an empty result.
/// </para>
/// <para>
/// The group is gated by <c>RequireAdminAuthorization</c> (replica/conflict-review entitlement),
/// which is the distinct authorization surface separate from the per-layer data-editor checks
/// used by the protocol replication endpoints.
/// </para>
/// </remarks>
internal static class ReplicaManagementEndpoints
{
    /// <summary>
    /// Staleness window after which a replica's last successful sync is considered expired. Beyond
    /// this window the derived replica status is reported as <c>expired</c>.
    /// </summary>
    private static readonly TimeSpan ReplicaStaleAfter = TimeSpan.FromDays(7);

    private const string StatusActive = "active";
    private const string StatusExpired = "expired";

    /// <summary>
    /// Maps the admin replica-management endpoints onto the admin services API group.
    /// </summary>
    public static void MapReplicaManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/services/{serviceId}/replicas")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Replicas")
            .RequireAdminAuthorization();

        _ = group.MapGet("/", HandleListReplicas)
            .WithName("ListAdminReplicas")
            .WithSummary("List named replicas registered against a feature service")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.MapGet("/{replicaId}", HandleGetReplica)
            .WithName("GetAdminReplica")
            .WithSummary("Get detail for a single named replica")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.MapGet("/{replicaId}/conflicts", HandleListConflicts)
            .WithName("ListAdminReplicaConflicts")
            .WithSummary("List durable disconnected-sync conflicts for a replica")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.MapGet("/{replicaId}/conflicts/{conflictId}", HandleGetConflict)
            .WithName("GetAdminReplicaConflict")
            .WithSummary("Get base/client/server detail for a single conflict")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.MapPost("/{replicaId}/conflicts/{conflictId}/resolve", HandleResolveConflict)
            .WithName("ResolveAdminReplicaConflict")
            .WithSummary("Apply an operator-selected resolution to a pending conflict")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
    }

    private static async Task<IResult> HandleListReplicas(
        string serviceId,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IReplicaRepository replicaRepository,
        CancellationToken cancellationToken)
    {
        var serviceProblem = await ValidateServiceAsync(serviceId, context, resourceValidator, cancellationToken)
            .ConfigureAwait(false);
        if (serviceProblem != null)
        {
            return serviceProblem;
        }

        var records = await replicaRepository.ListByServiceAsync(serviceId, cancellationToken).ConfigureAwait(false);

        var response = new ReplicaManagementListResponse
        {
            ServiceId = serviceId,
            Replicas = records.Select(ToSummary).ToArray(),
        };

        return Results.Json(
            ApiResponse<ReplicaManagementListResponse>.CreateSuccess(response),
            ReplicaManagementJsonContext.Default.ApiResponseReplicaManagementListResponse);
    }

    private static async Task<IResult> HandleGetReplica(
        string serviceId,
        string replicaId,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IReplicaRepository replicaRepository,
        CancellationToken cancellationToken)
    {
        var serviceProblem = await ValidateServiceAsync(serviceId, context, resourceValidator, cancellationToken)
            .ConfigureAwait(false);
        if (serviceProblem != null)
        {
            return serviceProblem;
        }

        var record = await replicaRepository.GetAsync(replicaId, cancellationToken).ConfigureAwait(false);
        if (record is null ||
            !string.Equals(record.Value.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                $"Replica '{replicaId}' not found for service '{serviceId}'.");
        }

        var detail = ToDetail(record.Value);
        return Results.Json(
            ApiResponse<ReplicaManagementDetail>.CreateSuccess(detail),
            ReplicaManagementJsonContext.Default.ApiResponseReplicaManagementDetail);
    }

    private static async Task<IResult?> ValidateServiceAsync(
        string serviceId,
        HttpContext context,
        IResourceValidator resourceValidator,
        CancellationToken cancellationToken)
    {
        var serviceResult = await resourceValidator.ValidateServiceV2Async(serviceId, cancellationToken)
            .ConfigureAwait(false);
        if (serviceResult.IsValid && serviceResult.Resource != null)
        {
            return null;
        }

        var statusCode = serviceResult.ErrorCode == ResourceValidationError.InvalidIdentifier
            ? StatusCodes.Status400BadRequest
            : StatusCodes.Status404NotFound;
        var message = serviceResult.ErrorMessage ?? $"Service '{serviceId}' not found.";
        return ProblemDetailsHelpers.CreateAdminProblem(context, statusCode, message);
    }

    private static ReplicaManagementSummary ToSummary(ReplicaRecord record) => new()
    {
        ReplicaId = record.ReplicaId,
        ReplicaName = record.ReplicaName,
        ServiceId = record.ServiceId,
        SyncModel = record.SyncModel,
        LayerIds = record.LayerIds,
        CreatedAt = record.CreatedAt,
        LastSyncTime = record.LastSyncTime,
        Status = DeriveStatus(record.LastSyncTime),
    };

    private static ReplicaManagementDetail ToDetail(ReplicaRecord record) => new()
    {
        ReplicaId = record.ReplicaId,
        ReplicaName = record.ReplicaName,
        ServiceId = record.ServiceId,
        SyncModel = record.SyncModel,
        LayerIds = record.LayerIds,
        CreatedAt = record.CreatedAt,
        LastSyncTime = record.LastSyncTime,
        LastSyncGeneration = record.LastSyncGeneration,
        Status = DeriveStatus(record.LastSyncTime),
    };

    private static string DeriveStatus(DateTimeOffset lastSyncTime) =>
        DateTimeOffset.UtcNow - lastSyncTime > ReplicaStaleAfter ? StatusExpired : StatusActive;

    // ---- Conflict review (#1167, slice 2) ------------------------------------------------------

    private static async Task<IResult> HandleListConflicts(
        string serviceId,
        string replicaId,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IReplicaRepository replicaRepository,
        [FromServices] IReplicaConflictRepository conflictRepository,
        CancellationToken cancellationToken)
    {
        var (replica, problem) = await ResolveReplicaAsync(
            serviceId, replicaId, context, resourceValidator, replicaRepository, conflictRepository, cancellationToken)
            .ConfigureAwait(false);
        if (problem != null)
        {
            return problem;
        }

        ReplicaConflictStatus? statusFilter = null;
        var statusQuery = context.Request.Query["status"].ToString();
        if (!string.IsNullOrWhiteSpace(statusQuery))
        {
            if (!TryParseStatus(statusQuery, out var parsedStatus))
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status400BadRequest,
                    $"Unknown conflict status filter '{statusQuery}'. Expected one of: pending, resolved, deferred.");
            }

            statusFilter = parsedStatus;
        }

        var conflicts = await conflictRepository
            .ListByReplicaAsync(replica.ReplicaId, statusFilter, cancellationToken)
            .ConfigureAwait(false);

        var response = new ReplicaConflictListResponse
        {
            ServiceId = serviceId,
            ReplicaId = replica.ReplicaId,
            StatusFilter = statusFilter is null ? null : StatusToString(statusFilter.Value),
            Conflicts = conflicts.Select(ToConflictSummary).ToArray(),
        };

        return Results.Json(
            ApiResponse<ReplicaConflictListResponse>.CreateSuccess(response),
            ReplicaManagementJsonContext.Default.ApiResponseReplicaConflictListResponse);
    }

    private static async Task<IResult> HandleGetConflict(
        string serviceId,
        string replicaId,
        string conflictId,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IReplicaRepository replicaRepository,
        [FromServices] IReplicaConflictRepository conflictRepository,
        CancellationToken cancellationToken)
    {
        var (replica, problem) = await ResolveReplicaAsync(
            serviceId, replicaId, context, resourceValidator, replicaRepository, conflictRepository, cancellationToken)
            .ConfigureAwait(false);
        if (problem != null)
        {
            return problem;
        }

        var conflict = await conflictRepository.GetAsync(conflictId, cancellationToken).ConfigureAwait(false);
        if (conflict is null ||
            !string.Equals(conflict.Value.ReplicaId, replica.ReplicaId, StringComparison.OrdinalIgnoreCase))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                $"Conflict '{conflictId}' not found for replica '{replicaId}'.");
        }

        return Results.Json(
            ApiResponse<ReplicaConflictDetail>.CreateSuccess(ToConflictDetail(conflict.Value)),
            ReplicaManagementJsonContext.Default.ApiResponseReplicaConflictDetail);
    }

    private static async Task<IResult> HandleResolveConflict(
        string serviceId,
        string replicaId,
        string conflictId,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IReplicaRepository replicaRepository,
        [FromServices] IReplicaConflictRepository conflictRepository,
        [FromServices] IChangeTracker changeTracker,
        [FromServices] IAuditLog auditLog,
        CancellationToken cancellationToken)
    {
        var (replica, problem) = await ResolveReplicaAsync(
            serviceId, replicaId, context, resourceValidator, replicaRepository, conflictRepository, cancellationToken)
            .ConfigureAwait(false);
        if (problem != null)
        {
            return problem;
        }

        ReplicaConflictResolutionRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                    ReplicaManagementJsonContext.Default.ReplicaConflictResolutionRequest,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Invalid conflict resolution request body.");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Action) ||
            !TryParseAction(request.Action, out var action))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "A valid 'action' is required: acceptClient, keepServer, mergeFields, chooseGeometry, rejectClient, or defer.");
        }

        var existing = await conflictRepository.GetAsync(conflictId, cancellationToken).ConfigureAwait(false);
        if (existing is null ||
            !string.Equals(existing.Value.ReplicaId, replica.ReplicaId, StringComparison.OrdinalIgnoreCase))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                $"Conflict '{conflictId}' not found for replica '{replicaId}'.");
        }

        if (existing.Value.Status == ReplicaConflictStatus.Resolved)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status409Conflict,
                $"Conflict '{conflictId}' has already been resolved.");
        }

        // Resolutions that adopt client edits (accept-client, field merge, geometry choice) produce
        // a new committed server state, captured by advancing the server generation cursor. Keep-
        // server, reject-client, and defer do not produce a new server state.
        var committedNewServerState = action is ReplicaConflictResolutionAction.AcceptClient
            or ReplicaConflictResolutionAction.MergeFields
            or ReplicaConflictResolutionAction.ChooseGeometry;

        long? resolvedGeneration = committedNewServerState
            ? await changeTracker.GetCurrentGenerationAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var actor = ConsolePrincipal.ResolveActorId(context.User) ?? "system";
        var resolution = new ReplicaConflictResolution(
            conflictId,
            action,
            actor,
            DateTimeOffset.UtcNow,
            resolvedGeneration);

        var outcome = await conflictRepository.ResolveAsync(resolution, cancellationToken).ConfigureAwait(false);
        if (outcome.Record is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                $"Conflict '{conflictId}' not found for replica '{replicaId}'.");
        }

        if (!outcome.Applied)
        {
            // The conflict was already resolved (e.g. by a concurrent operator that won the guarded
            // update); do not re-report this losing request as a success or emit a success audit
            // event. Mirror the pre-check's already-resolved response.
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status409Conflict,
                $"Conflict '{conflictId}' has already been resolved.");
        }

        var resolved = outcome.Record;

        await auditLog.RecordAsync(
            new AuditEvent
            {
                Timestamp = resolution.ResolvedAt,
                EventType = AuditEventType.AdminAction,
                Actor = actor,
                ActorType = AuditActorType.UserId,
                ResourceType = "replica_conflict",
                ResourceId = conflictId,
                Action = $"replica.conflict.resolve.{ActionToString(action)}",
                Outcome = AuditOutcome.Success,
                CorrelationId = context.TraceIdentifier,
                Details = JsonSerializer.Serialize(
                    new ReplicaConflictResolutionRequest { Action = ActionToString(action) },
                    ReplicaManagementJsonContext.Default.ReplicaConflictResolutionRequest),
            },
            cancellationToken)
            .ConfigureAwait(false);

        var response = new ReplicaConflictResolutionResponse
        {
            Conflict = ToConflictDetail(resolved.Value),
            CommittedNewServerState = committedNewServerState,
        };

        return Results.Json(
            ApiResponse<ReplicaConflictResolutionResponse>.CreateSuccess(response),
            ReplicaManagementJsonContext.Default.ApiResponseReplicaConflictResolutionResponse);
    }

    /// <summary>
    /// Validates the service, enforces the conflict-review capability, and resolves the replica
    /// (scoped to the service). Returns the replica on success, or a ProblemDetails result on
    /// failure (service invalid/not found, conflict review unsupported, or replica not found).
    /// </summary>
    private static async Task<(ReplicaRecord Replica, IResult? Problem)> ResolveReplicaAsync(
        string serviceId,
        string replicaId,
        HttpContext context,
        IResourceValidator resourceValidator,
        IReplicaRepository replicaRepository,
        IReplicaConflictRepository conflictRepository,
        CancellationToken cancellationToken)
    {
        var serviceProblem = await ValidateServiceAsync(serviceId, context, resourceValidator, cancellationToken)
            .ConfigureAwait(false);
        if (serviceProblem != null)
        {
            return (default, serviceProblem);
        }

        if (!conflictRepository.SupportsConflictReview)
        {
            return (default, ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status501NotImplemented,
                "Conflict review is not supported for this service's data provider."));
        }

        var record = await replicaRepository.GetAsync(replicaId, cancellationToken).ConfigureAwait(false);
        if (record is null ||
            !string.Equals(record.Value.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
        {
            return (default, ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                $"Replica '{replicaId}' not found for service '{serviceId}'."));
        }

        return (record.Value, null);
    }

    private static ReplicaConflictSummary ToConflictSummary(ReplicaConflictRecord record) => new()
    {
        ConflictId = record.ConflictId,
        ReplicaId = record.ReplicaId,
        ServiceId = record.ServiceId,
        LayerId = record.LayerId,
        ObjectId = record.ObjectId,
        ConflictType = ConflictTypeToString(record.ConflictType),
        Status = StatusToString(record.Status),
        ServerGeneration = record.ServerGeneration,
        DetectedAt = record.DetectedAt,
    };

    private static ReplicaConflictDetail ToConflictDetail(ReplicaConflictRecord record)
    {
        var baseState = ParseStateJson(record.BaseStateJson);
        var clientState = ParseStateJson(record.ClientStateJson);
        var serverState = ParseStateJson(record.ServerStateJson);
        var (geometryChanged, fieldChanges) = ReplicaConflictDiff.Compute(baseState, clientState, serverState);

        return new ReplicaConflictDetail
        {
            ConflictId = record.ConflictId,
            ReplicaId = record.ReplicaId,
            ServiceId = record.ServiceId,
            LayerId = record.LayerId,
            ObjectId = record.ObjectId,
            ConflictType = ConflictTypeToString(record.ConflictType),
            Status = StatusToString(record.Status),
            SyncOperationId = record.SyncOperationId,
            DeviceId = record.DeviceId,
            UserId = record.UserId,
            ServerGeneration = record.ServerGeneration,
            BaseState = baseState,
            ClientState = clientState,
            ServerState = serverState,
            GeometryChanged = geometryChanged,
            FieldChanges = fieldChanges.Length > 0 ? fieldChanges : null,
            DetectedAt = record.DetectedAt,
            ResolutionAction = record.ResolutionAction is null ? null : ActionToString(record.ResolutionAction.Value),
            ResolvedBy = record.ResolvedBy,
            ResolvedAt = record.ResolvedAt,
            ResolvedServerGeneration = record.ResolvedServerGeneration,
        };
    }

    private static JsonElement? ParseStateJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Stored state is opaque; if it is not valid JSON, omit it rather than failing the read.
            return null;
        }
    }

    private static bool TryParseStatus(string value, out ReplicaConflictStatus status)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "pending":
                status = ReplicaConflictStatus.Pending;
                return true;
            case "resolved":
                status = ReplicaConflictStatus.Resolved;
                return true;
            case "deferred":
                status = ReplicaConflictStatus.Deferred;
                return true;
            default:
                status = default;
                return false;
        }
    }

    private static bool TryParseAction(string value, out ReplicaConflictResolutionAction action)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "acceptclient":
                action = ReplicaConflictResolutionAction.AcceptClient;
                return true;
            case "keepserver":
                action = ReplicaConflictResolutionAction.KeepServer;
                return true;
            case "mergefields":
                action = ReplicaConflictResolutionAction.MergeFields;
                return true;
            case "choosegeometry":
                action = ReplicaConflictResolutionAction.ChooseGeometry;
                return true;
            case "rejectclient":
                action = ReplicaConflictResolutionAction.RejectClient;
                return true;
            case "defer":
                action = ReplicaConflictResolutionAction.Defer;
                return true;
            default:
                action = default;
                return false;
        }
    }

    private static string ConflictTypeToString(ReplicaConflictType type) => type switch
    {
        ReplicaConflictType.Attribute => "attribute",
        ReplicaConflictType.Geometry => "geometry",
        ReplicaConflictType.DeleteUpdate => "deleteUpdate",
        ReplicaConflictType.UpdateDelete => "updateDelete",
        ReplicaConflictType.DuplicateInsert => "duplicateInsert",
        ReplicaConflictType.Attachment => "attachment",
        ReplicaConflictType.Relationship => "relationship",
        _ => type.ToString().ToLowerInvariant(),
    };

    private static string StatusToString(ReplicaConflictStatus status) => status switch
    {
        ReplicaConflictStatus.Pending => "pending",
        ReplicaConflictStatus.Resolved => "resolved",
        ReplicaConflictStatus.Deferred => "deferred",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static string ActionToString(ReplicaConflictResolutionAction action) => action switch
    {
        ReplicaConflictResolutionAction.AcceptClient => "acceptClient",
        ReplicaConflictResolutionAction.KeepServer => "keepServer",
        ReplicaConflictResolutionAction.MergeFields => "mergeFields",
        ReplicaConflictResolutionAction.ChooseGeometry => "chooseGeometry",
        ReplicaConflictResolutionAction.RejectClient => "rejectClient",
        ReplicaConflictResolutionAction.Defer => "defer",
        _ => action.ToString(),
    };
}
