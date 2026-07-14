// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Alerts.Ops;

/// <summary>
/// Ops-notification producer for execution jobs: emits an Error-severity ops
/// notification when a job reaches the terminal <see cref="ExecutionJobStatus.Failed"/>
/// state, carrying the operation id and workload. Best-effort and kind-agnostic (all
/// job kinds), mirroring the other <see cref="IJobTerminalCallback"/> implementations:
/// a throwing notification must never block the job's terminal transition. Resolves
/// the scoped <see cref="OpsNotificationService"/> from a fresh scope because this
/// callback is a singleton fired outside any request scope.
/// </summary>
internal sealed partial class OpsJobTerminalCallback : IJobTerminalCallback
{
    private const string OpsSource = "job";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AlertOptions _options;
    private readonly ILogger<OpsJobTerminalCallback> _logger;

    public OpsJobTerminalCallback(
        IServiceScopeFactory scopeFactory,
        IOptions<AlertOptions> options,
        ILogger<OpsJobTerminalCallback> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        // Only terminal failures raise an ops notification; success/cancel are not ops-worthy.
        if (job.Status != ExecutionJobStatus.Failed || !_options.Ops.Enabled)
        {
            return;
        }

        var notification = new OpsNotification
        {
            Source = OpsSource,
            Severity = AlertSeverity.Critical,
            Title = $"Job failed: {job.Spec.WorkloadName}",
            Body = job.ErrorMessage is { Length: > 0 } error
                ? $"Execution job '{job.OperationId}' ({job.Spec.Kind}) failed: {error}"
                : $"Execution job '{job.OperationId}' ({job.Spec.Kind}) failed.",
            DedupeIdentifier = $"{job.OperationId}:{job.Status}",
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["operationId"] = job.OperationId,
                ["workload"] = job.Spec.WorkloadName,
                ["kind"] = job.Spec.Kind.ToString(),
                ["status"] = job.Status.ToString(),
            },
        };

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var opsNotifier = scope.ServiceProvider.GetRequiredService<OpsNotificationService>();
            await opsNotifier.NotifyAsync(notification, cancellationToken).ConfigureAwait(false);
        }
        // Intentional catch-all: best-effort notification. A failed notification
        // must not block the job terminal transition that raised it.
        catch (Exception ex)
        {
            LogNotifyFailed(_logger, job.OperationId, ex);
        }
    }

    [LoggerMessage(EventId = 9453, Level = LogLevel.Warning, Message = "Ops notification for failed job {OperationId} could not be enqueued.")]
    private static partial void LogNotifyFailed(ILogger logger, string operationId, Exception exception);
}
