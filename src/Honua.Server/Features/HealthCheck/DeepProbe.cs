// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Monitoring;

namespace Honua.Server.Features.HealthCheck;

// The anonymous deep probe touches the database. Concurrent callers share one in-flight check,
// so a burst of requests costs one dependency round trip; once it completes the next caller
// starts a fresh check, so a success is never served from a cache.
internal sealed class DeepProbe(IServiceScopeFactory scopes, ILoggerFactory loggerFactory)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private Task<ReadinessResult>? _inFlight;

    public Task<ReadinessResult> CheckAsync()
    {
        lock (_gate)
        {
            if (_inFlight is not { IsCompleted: false })
            {
                // The shared check must not carry the first caller's trace, log scope or schema.
                using (ExecutionContext.SuppressFlow())
                {
                    _inFlight = Task.Run(RunAsync);
                }
            }

            return _inFlight;
        }
    }

    // Runs in its own scope and deadline, so one caller disconnecting cannot fail the others.
    private async Task<ReadinessResult> RunAsync()
    {
        using var deadline = new CancellationTokenSource(Timeout);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await CheckDependenciesAsync(scope.ServiceProvider, deadline.Token);
        }
        // This anonymous probe boundary reports dependency failures without exposing their details.
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            HealthEndpointsLog.DeepProbeFailed(loggerFactory.CreateLogger("Honua.HealthCheck"), exception);
            return ReadinessResult.NotReady("Dependencies unavailable");
        }
    }

    private static async Task<ReadinessResult> CheckDependenciesAsync(IServiceProvider services, CancellationToken deadline)
    {
        var database = services.GetRequiredService<IDatabaseHealthChecker>();
        var catalog = services.GetRequiredService<IMetadataV2GraphProvider>();
        if (!services.GetRequiredService<MigrationState>().IsReady ||
            !await database.IsDatabaseHealthyAsync(deadline).WaitAsync(deadline))
        {
            return ReadinessResult.NotReady("Dependencies unavailable");
        }

        await catalog.GetCurrentAsync(deadline).AsTask().WaitAsync(deadline);
        deadline.ThrowIfCancellationRequested();
        return ReadinessResult.Ready();
    }
}
