// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Alerts.Ops;

/// <summary>
/// Decorates the durable workflow-operation store to emit an ops notification when a
/// <see cref="WorkflowOperationKind.Deploy"/> operation reaches a terminal status. This
/// is the single seam for deploy terminal transitions: every persisted transition from
/// both <c>DeployWorkflowService</c> and <c>DeployWorkflowReconciler</c> funnels through
/// <see cref="IWorkflowOperationStore.SetAsync"/>, so decorating it covers them all
/// without scattering notification calls across the reconciler. Notification is
/// best-effort and fired AFTER the authoritative inner write; idempotency is provided
/// by the outbox dedupe key, so repeated terminal writes enqueue at most once.
/// </summary>
internal sealed partial class NotifyingWorkflowOperationStore : IWorkflowOperationStore
{
    private const string OpsSource = "deploy-workflow";

    private readonly IWorkflowOperationStore _inner;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AlertOptions _options;
    private readonly ILogger<NotifyingWorkflowOperationStore> _logger;

    public NotifyingWorkflowOperationStore(
        IWorkflowOperationStore inner,
        IServiceScopeFactory scopeFactory,
        IOptions<AlertOptions> options,
        ILogger<NotifyingWorkflowOperationStore> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // Authoritative write first; a failed notification must never block it.
        await _inner.SetAsync(operation, ttl, cancellationToken).ConfigureAwait(false);

        if (_options.Ops.Enabled
            && operation.Kind == WorkflowOperationKind.Deploy
            && TryMapSeverity(operation.Status, out var severity))
        {
            await NotifyTerminalAsync(operation, severity, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task NotifyTerminalAsync(WorkflowOperationRecord operation, AlertSeverity severity, CancellationToken cancellationToken)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["operationId"] = operation.OperationId,
            ["status"] = operation.Status.ToString(),
        };

        if (operation.CurrentPhase is { Length: > 0 } phase)
        {
            attributes["phase"] = phase;
        }

        var notification = new OpsNotification
        {
            Source = OpsSource,
            Severity = severity,
            Title = $"Deploy {operation.Status}: {operation.OperationId}",
            Body = operation.ErrorMessage is { Length: > 0 } error
                ? $"Deploy operation '{operation.OperationId}' reached terminal status {operation.Status}: {error}"
                : $"Deploy operation '{operation.OperationId}' reached terminal status {operation.Status}.",
            DedupeIdentifier = $"{operation.OperationId}:{operation.Status}",
            Attributes = attributes,
        };

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var opsNotifier = scope.ServiceProvider.GetRequiredService<OpsNotificationService>();
            await opsNotifier.NotifyAsync(notification, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogNotifyFailed(_logger, operation.OperationId, ex);
        }
    }

    private static bool TryMapSeverity(WorkflowOperationStatus status, out AlertSeverity severity)
    {
        switch (status)
        {
            case WorkflowOperationStatus.Failed:
            case WorkflowOperationStatus.RolledBack:
                severity = AlertSeverity.Critical;
                return true;
            case WorkflowOperationStatus.ManualInterventionRequired:
                severity = AlertSeverity.Warning;
                return true;
            case WorkflowOperationStatus.Succeeded:
                severity = AlertSeverity.Info;
                return true;
            default:
                severity = AlertSeverity.Info;
                return false;
        }
    }

    // Straight pass-through for the remaining store surface.
    public Task<bool> TryCreateAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        => _inner.TryCreateAsync(operation, ttl, cancellationToken);

    public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
        => _inner.GetAsync(operationId, cancellationToken);

    public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
        => _inner.GetByMetadataPackageIdAsync(packageId, cancellationToken);

    public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
        => _inner.ListActiveAsync(kind, cancellationToken);

    public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => _inner.TryAcquireLeaseAsync(operationId, ownerId, leaseDuration, cancellationToken);

    public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => _inner.RenewLeaseAsync(operationId, ownerId, leaseDuration, cancellationToken);

    public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
        => _inner.ReleaseLeaseAsync(operationId, ownerId, cancellationToken);

    [LoggerMessage(EventId = 9434, Level = LogLevel.Warning, Message = "Ops notification for terminal deploy {OperationId} could not be enqueued.")]
    private static partial void LogNotifyFailed(ILogger logger, string operationId, Exception exception);
}
