// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin/operator endpoints for inspecting named disconnected-sync replicas (#1167, slice 1).
/// </summary>
/// <remarks>
/// <para>
/// This is the operator-facing management surface that mirrors the Esri offline/disconnected
/// editing shape: clients create named replicas via the GeoServices FeatureServer
/// <c>createReplica</c>/<c>synchronizeReplica</c> REST endpoints, and operators review the
/// active replicas (name, owner-supplied metadata, layers, sync model, last sync time, and the
/// server generation cursor) here. It reads durable replica state from
/// <see cref="IReplicaRepository"/>; it does not create, mutate, or synchronize replicas.
/// </para>
/// <para>
/// The group is gated by <c>RequireAdminAuthorization</c> (replica/conflict-review entitlement),
/// which is the distinct authorization surface separate from the per-layer data-editor checks
/// used by the protocol replication endpoints. The durable conflict-record review and resolution
/// APIs are deferred to a follow-up slice.
/// </para>
/// </remarks>
internal static class ReplicaManagementEndpoints
{
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
    };
}
