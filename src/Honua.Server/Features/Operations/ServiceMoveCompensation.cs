// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Admin;

namespace Honua.Server.Features.Operations;

internal static class ServiceMoveCompensation
{
    internal const string CatalogChangedDetail =
        "The catalog changed after this service move. Submit a fresh move instead of restoring the recorded assignment.";

    internal static async Task<IResult> PostAsync(HttpContext context, string handleId)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!await OperationAdminAuthorization.IsAuthorizedAsync(
                context, context.User, OperationSideEffectClass.MutatesMetadata, context.RequestAborted).ConfigureAwait(false))
            return Results.Forbid();
        if (AgentApprovalRule.IsAgent(AgentPrincipal.EffectiveRoles(context.User)))
            return OperatorApprovalGate.AgentApprovalRequired(context, AgentApprovalFilter.OperationRef, AgentApprovalFilter.ConsoleGuidance);
        var store = context.RequestServices.GetService<IOperationInstanceStore>();
        var catalog = context.RequestServices.GetService<IMetadataV2GraphStore>();
        if (store is null || catalog is null) return Unavailable();
        var loaded = await LoadCompletedMoveAsync(context, store, handleId).ConfigureAwait(false);
        if (loaded.Refusal is not null) return loaded.Refusal;
        var existing = await RecordedCompensationAsync(store, loaded.Evidence, context.RequestAborted).ConfigureAwait(false);
        if (existing is not null) return Status(existing);
        return await RestoreAsync(context, catalog, loaded).ConfigureAwait(false);
    }

    private static async Task<IResult> RestoreAsync(HttpContext context, IMetadataV2GraphStore catalog, LoadedMove loaded)
    {
        var snapshot = await catalog.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        var decision = ServiceMoveEvidence.Decide(snapshot.Graph, snapshot.Etag, loaded.Evidence);
        if (decision == ServiceMoveRecoveryKind.CatalogChanged)
            return Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed, detail: CatalogChangedDetail);
        if (decision == ServiceMoveRecoveryKind.NotRestorable)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "The recorded service move cannot be restored.");
        if (decision == ServiceMoveRecoveryKind.AlreadyRestored)
        {
            var denied = await FolderMutationService.AuthorizeObservationAsync(
                RestoreExecution(context, loaded.Evidence), snapshot.Graph, context.RequestAborted).ConfigureAwait(false);
            if (denied is not null) return MapFailure(context, denied);
        }
        else
        {
            var execution = await FolderMutationService.ExecuteAsync(
                RestoreExecution(context, loaded.Evidence), context.RequestAborted).ConfigureAwait(false);
            if (execution.Failure is not null)
                return await HandleExecutionFailureAsync(context, loaded, execution.Failure).ConfigureAwait(false);
        }
        return await RecordObservationAsync(context, loaded.Handle, loaded.Evidence).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleExecutionFailureAsync(
        HttpContext context, LoadedMove loaded, FolderExecutionFailure failure)
    {
        if (failure is FolderExecutionFailure.Mutation { Refusal: var refusal } && refusal == FolderMutationFailure.SnapshotChanged)
        {
            var store = context.RequestServices.GetRequiredService<IOperationInstanceStore>();
            var current = await store.GetAsync(loaded.Handle.OperationInstanceId, context.RequestAborted).ConfigureAwait(false);
            var latest = current is null ? null : ServiceMoveEvidence.Read(current.Result?.Details ?? EmptyDetails);
            var existing = await RecordedCompensationAsync(store, latest, context.RequestAborted).ConfigureAwait(false);
            if (existing is not null) return Status(existing);
            var catalog = context.RequestServices.GetRequiredService<IMetadataV2GraphStore>();
            var fresh = await catalog.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
            if (ServiceMoveEvidence.Decide(fresh.Graph, fresh.Etag, loaded.Evidence) == ServiceMoveRecoveryKind.AlreadyRestored)
                return await RecordObservationAsync(context, loaded.Handle, loaded.Evidence).ConfigureAwait(false);
            return Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed, detail: CatalogChangedDetail);
        }
        return MapFailure(context, failure);
    }

    private static FolderMutationExecution RestoreExecution(HttpContext context, ServiceMoveEvidenceRecord evidence) => new()
    {
        Services = context.RequestServices,
        Principal = context.User,
        Command = new FolderMutationCommand.Restore(evidence.ServiceName, evidence.Assignments),
        ExpectedEtag = evidence.PostconditionEtag,
        Namespace = evidence.Namespace,
        RequestCache = context.Items
    };

    private static async Task<IResult> RecordObservationAsync(HttpContext context, OperationHandle handle, ServiceMoveEvidenceRecord evidence)
    {
        var store = context.RequestServices.GetRequiredService<IOperationInstanceStore>();
        var cancellationToken = context.RequestAborted;
        var current = await store.GetAsync(handle.OperationInstanceId, cancellationToken).ConfigureAwait(false) ?? handle;
        var latest = ServiceMoveEvidence.Read(current.Result?.Details ?? EmptyDetails);
        var existing = await RecordedCompensationAsync(store, latest, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return Status(existing);
        var compensation = CompensationHandle(current, evidence, UtcNow(context));
        if (!await store.TryCreateAsync(compensation, cancellationToken).ConfigureAwait(false))
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: "The restore result could not be recorded.");
        var winner = await StampOriginalAsync(context, current, compensation).ConfigureAwait(false);
        return Status(winner ?? compensation);
    }

    private static async Task<OperationHandle?> StampOriginalAsync(
        HttpContext context, OperationHandle current, OperationHandle compensation)
    {
        var store = context.RequestServices.GetRequiredService<IOperationInstanceStore>();
        var cancellationToken = context.RequestAborted;
        var stamped = ServiceMoveEvidence.WithCompensation(current, compensation.OperationInstanceId, compensation.UpdatedAt);
        if (await store.TrySetAsync(stamped, current.Version, cancellationToken).ConfigureAwait(false)) return null;
        var winner = await store.GetAsync(current.OperationInstanceId, cancellationToken).ConfigureAwait(false);
        var evidence = winner is null ? null : ServiceMoveEvidence.Read(winner.Result?.Details ?? EmptyDetails);
        if (evidence?.CompensationInstanceId is not { } winnerId) return null;
        return await store.GetAsync(winnerId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<LoadedMove> LoadCompletedMoveAsync(
        HttpContext context, IOperationInstanceStore store, string handleId)
    {
        var handle = await store.GetAsync(handleId, context.RequestAborted).ConfigureAwait(false);
        if (handle is null || !OperationTenantAuthorization.CanAccess(context, handle.TenantId))
            return new(null!, null!, Results.NotFound());
        if (handle.OperationId != "service.move" || handle.Status != OperationHandleStatus.Completed || handle.Result is null)
            return new(null!, null!, Conflict("Only a completed service move can be restored."));
        var evidence = ServiceMoveEvidence.Read(handle.Result.Details);
        return evidence is null
            ? new(null!, null!, Conflict("This service move has no recorded prior folder assignment."))
            : new(handle, evidence, null);
    }

    private static async Task<OperationHandle?> RecordedCompensationAsync(
        IOperationInstanceStore store, ServiceMoveEvidenceRecord? evidence, CancellationToken cancellationToken)
    {
        if (evidence?.CompensationInstanceId is not { } existingId) return null;
        return await store.GetAsync(existingId, cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset UtcNow(HttpContext context)
        => context.RequestServices.GetService<TimeProvider>()?.GetUtcNow() ?? TimeProvider.System.GetUtcNow();

    private static OperationHandle CompensationHandle(OperationHandle current, ServiceMoveEvidenceRecord evidence, DateTimeOffset now)
    {
        return new OperationHandle
        {
            OperationInstanceId = $"opinst-{Guid.NewGuid():N}",
            OperationId = "service.move",
            Status = OperationHandleStatus.Completed,
            CorrelationId = current.CorrelationId,
            CreatedAt = now,
            UpdatedAt = now,
            TenantId = current.TenantId,
            Result = new OperationResultSummary
            {
                Summary = "Move service restored the recorded prior folder.",
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ServiceMoveEvidence.ServiceNameKey] = evidence.ServiceName,
                    ["serviceMove.restoredFrom"] = current.OperationInstanceId
                }
            }
        };
    }

    private static readonly Dictionary<string, string> EmptyDetails = new(StringComparer.Ordinal);

    private sealed record LoadedMove(OperationHandle Handle, ServiceMoveEvidenceRecord Evidence, IResult? Refusal);

    private static IResult Unavailable()
        => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: FolderMutationFailure.ReadOnlyCatalog.Detail);

    private static IResult Conflict(string detail)
        => Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: detail);

    private static IResult Status(OperationHandle handle)
        => Results.Json(ApiResponse<OperationStatus>.CreateSuccess(OperationStatusProjection.FromHandle(handle)),
            OperationsJsonContext.Default.ApiResponseOperationStatus);

    private static IResult MapFailure(HttpContext context, FolderExecutionFailure failure) => failure switch
    {
        FolderExecutionFailure.Mutation mutation => mutation.Refusal.ToResult(),
        FolderExecutionFailure.Authorization authorization => OperatorApprovalGate.CreateAuthorizationDeniedResult(context, authorization.Decision)!,
        FolderExecutionFailure.Approval approval => context.RequestServices.GetRequiredService<OperatorApprovalGate>()
            .CreateApprovalDeniedResult(context, new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Catalog,
                Operation = OperatorOperation.Publish
            }, approval.Requirement)!,
        FolderExecutionFailure.Replay => Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: FolderApprovalAuthorization.FreshProposalRequired),
        FolderExecutionFailure.AffectedServices => Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: "The requester cannot change the affected services."),
        _ => Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "The recorded service move cannot be restored.")
    };
}
