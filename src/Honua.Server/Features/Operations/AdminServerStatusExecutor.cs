// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.HealthCheck;

namespace Honua.Server.Features.Operations;

/// <summary>Executes the live, read-only <c>admin.server.status</c> operation.</summary>
internal sealed class AdminServerStatusExecutor : IOperationExecutor
{
    public const string OperationName = "admin.server.status";

    private readonly IReadinessCheckService _readiness;
    private readonly TimeProvider _clock;

    public AdminServerStatusExecutor(IReadinessCheckService readiness, TimeProvider clock)
    {
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
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
        var version = typeof(AdminServerStatusExecutor).Assembly.GetName().Version?.ToString() ?? "unknown";

        return new OperationHandle
        {
            OperationId = OperationId,
            HandleId = $"op-{_clock.GetUtcNow().ToUnixTimeMilliseconds():x}-{Guid.NewGuid():N}"[..32],
            Status = OperationHandleStatus.Completed,
            Result = new OperationResultSummary
            {
                Summary = $"Server is {status}.",
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["status"] = status,
                    ["version"] = version,
                    ["message"] = readiness.Message
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
            OperationId = OperationId,
            HandleId = handle.HandleId,
            Status = handle.Status,
            Result = handle.Result
        });
    }
}
