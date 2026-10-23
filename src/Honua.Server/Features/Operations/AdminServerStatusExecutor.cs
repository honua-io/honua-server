// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Hosting;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Operations;

/// <summary>Executes the live, read-only <c>admin.server.status</c> operation.</summary>
internal sealed class AdminServerStatusExecutor : IOperationExecutor
{
    public const string OperationName = "admin.server.status";

    private readonly IReadinessCheckService _readiness;
    private readonly TimeProvider _clock;
    private readonly IServerInstanceIdentity _instance;

    public AdminServerStatusExecutor(
        IReadinessCheckService readiness,
        TimeProvider clock,
        IServerInstanceIdentity instance)
    {
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
    }

    public string OperationId => OperationName;

    public Task<OperationValidation> ValidateAsync(
        OperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });
    }

    public async Task<OperationHandle> SubmitAsync(
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var readiness = await _readiness.CheckReadinessAsync(cancellationToken).ConfigureAwait(false);
        var status = readiness.IsReady ? "ready" : "not_ready";
        var version = HonuaDeploymentIdentity.GetReleaseVersion(typeof(AdminServerStatusExecutor).Assembly);
        var now = _clock.GetUtcNow();

        return new OperationHandle
        {
            OperationInstanceId = context.OperationInstanceId ?? $"opinst-{Guid.NewGuid():N}",
            OperationId = OperationId,
            CorrelationId = context.CorrelationId ?? $"corr-{Guid.NewGuid():N}",
            Status = OperationHandleStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now,
            Result = new OperationResultSummary
            {
                Summary = $"Server is {status}.",
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["status"] = status,
                    ["version"] = version,
                    ["message"] = readiness.Message,
                    // Which replica answered (same value as the Honua-Instance response header),
                    // so a cross-replica read can be proven through this operation too.
                    ["instanceId"] = _instance.InstanceId,
                    ["instanceSource"] = _instance.Source,
                    ["instanceStartedAt"] = _instance.StartedAt.ToString("O", CultureInfo.InvariantCulture)
                }
            }
        };
    }

    public Task<OperationStatus> GetStatusAsync(
        OperationHandle handle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return Task.FromResult(new OperationStatus
        {
            OperationInstanceId = handle.OperationInstanceId,
            OperationId = OperationId,
            CorrelationId = handle.CorrelationId,
            AuditId = handle.AuditId,
            ProposalId = handle.ProposalId,
            CreatedAt = handle.CreatedAt,
            UpdatedAt = handle.UpdatedAt,
            AuthorizationOutcome = handle.AuthorizationOutcome,
            PolicyDecision = handle.PolicyDecision,
            Status = handle.Status,
            Result = handle.Result,
            ApprovalLane = handle.ApprovalLane,
            Reason = handle.Reason,
            ResourceIds = handle.ResourceIds,
            EvidenceRefs = handle.EvidenceRefs,
        });
    }
}
