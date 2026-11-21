// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Core.Features.Observability.Abstractions;

namespace Honua.Server.Features.Infrastructure.Monitoring;

internal sealed class OpsHeartbeatCoordinator(
    IExecutionJobStore jobStore,
    IOpsHeartbeatPublisher publisher,
    IServiceScopeFactory? scopeFactory = null)
{
    private readonly string _owner = Guid.NewGuid().ToString("N");

    public async Task PublishAsync(string stackName, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));

        if (!await IsHostReadyAsync(deadline.Token).ConfigureAwait(false))
        {
            return;
        }

        var leaseKey = $"ops-heartbeat:{stackName}";
        if (!await jobStore.TryAcquireLeaseAsync(leaseKey, _owner,
            TimeSpan.FromSeconds(60), deadline.Token).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            await publisher.PublishAsync(stackName, deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            await ReleaseLeaseBestEffortAsync(leaseKey).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> IsHostReadyAsync(CancellationToken cancellationToken)
    {
        if (scopeFactory is null)
        {
            return true;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var readiness = scope.ServiceProvider.GetService<IReadinessCheckService>();
        if (readiness is null)
        {
            return true;
        }

        var result = await readiness.CheckReadinessAsync(cancellationToken).ConfigureAwait(false);
        return result.IsReady;
    }

    private async Task ReleaseLeaseBestEffortAsync(string leaseKey)
    {
        try
        {
            using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await jobStore.ReleaseLeaseAsync(leaseKey, _owner, releaseTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best-effort release so another replica can publish promptly after a transient failure.
        }
    }
}
