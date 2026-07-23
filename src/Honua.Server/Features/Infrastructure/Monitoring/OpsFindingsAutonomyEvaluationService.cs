// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Observability.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Leased singleton loop that periodically re-evaluates deterministic ops findings
/// against the graduated autonomy policy. The service is registered only when the
/// durable Redis job-store path is active, so hosts without shared coordination stay inert.
/// </summary>
internal sealed partial class OpsFindingsAutonomyEvaluationService(
    IServiceScopeFactory scopeFactory,
    IExecutionJobStore jobStore,
    IOptionsMonitor<OpsAutonomyOptions> options,
    ILogger<OpsFindingsAutonomyEvaluationService> logger) : BackgroundService
{
    private const string LeaseOperationId = "ops-findings-autonomy-evaluator";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private readonly string _ownerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = ResolveDelay(options.CurrentValue);
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                await EvaluateOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            // Intentionally generic: this is a long-running background evaluation loop. A
            // single failed iteration (e.g. a transient lease/store failure) must not kill
            // the host's background service; log and keep polling on the next delay.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.EvaluationLoopFailed(logger, ex);
            }
        }
    }

    internal async Task EvaluateOnceAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = options.CurrentValue;
        if (!snapshot.Enabled || snapshot.KillSwitchEnabled)
        {
            return;
        }

        if (!await jobStore
                .TryAcquireLeaseAsync(LeaseOperationId, _ownerId, LeaseDuration, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var findingsService = scope.ServiceProvider.GetRequiredService<IOpsFindingsService>();
            var evaluator = scope.ServiceProvider.GetRequiredService<IOpsAutonomyEvaluator>();
            var findings = await findingsService.EvaluateAsync(cancellationToken).ConfigureAwait(false);

            foreach (var finding in findings)
            {
                var decision = await evaluator
                    .EvaluateFindingAsync(finding, cancellationToken)
                    .ConfigureAwait(false);
                if (!decision.CanAutoApply)
                {
                    continue;
                }

                await findingsService.ProposeAsync(finding.Id, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await jobStore.ReleaseLeaseAsync(LeaseOperationId, _ownerId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan ResolveDelay(OpsAutonomyOptions options)
    {
        var seconds = options.EvaluationSeconds <= 0 ? 60 : options.EvaluationSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    private static partial class Log
    {
        [LoggerMessage(9410, LogLevel.Warning, "Ops findings autonomy evaluation loop failed")]
        public static partial void EvaluationLoopFailed(ILogger logger, Exception exception);
    }
}
