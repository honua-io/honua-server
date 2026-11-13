// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Threading.Channels;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

internal sealed partial class AdminRealtimeBroadcaster : BackgroundService
{
    private readonly Channel<OpsHealthSnapshotResponse> _opsHealth =
        Channel.CreateBounded<OpsHealthSnapshotResponse>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false
        });
    private readonly AdminRealtimePublisher _publisher;
    private readonly OpsHealthFlushSignal _flushSignal;
    private readonly ILogger<AdminRealtimeBroadcaster> _logger;

    public AdminRealtimeBroadcaster(AdminRealtimePublisher publisher, OpsHealthFlushSignal flushSignal,
        ILogger<AdminRealtimeBroadcaster> logger)
    {
        _publisher = publisher;
        _flushSignal = flushSignal;
        _logger = logger;
        _flushSignal.Flushed += OnFlushed;
    }

    public async Task PublishTransitionAsync(WorkflowOperationTransition transition, CancellationToken cancellationToken)
    {
        await _publisher.PublishAsync(new AdminRealtimeMessage(AdminRealtimeContract.DeployOperationsTopic,
            AdminRealtimeContract.DeployOperationEventName, JsonSerializer.SerializeToElement(MapDeployEvent(transition), AdminRealtimeGroupsJsonContext.Default.DeployOperationRealtimeEvent)), cancellationToken).ConfigureAwait(false);
        var operateEvent = OperateEventResponseMapper.Map(ReleaseTimelineEventFactory.Create(transition));
        await _publisher.PublishAsync(new AdminRealtimeMessage(AdminRealtimeContract.OperateEventsTopic,
            AdminRealtimeContract.OperateEventEventName, JsonSerializer.SerializeToElement(operateEvent, AdminRealtimeGroupsJsonContext.Default.OperateEventResponse)), cancellationToken).ConfigureAwait(false);
    }

    private void OnFlushed(object? sender, OpsHealthFlushedEventArgs args) => _opsHealth.Writer.TryWrite(args.Snapshot);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var snapshot in _opsHealth.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _publisher.PublishAsync(new AdminRealtimeMessage(AdminRealtimeContract.OpsHealthTopic,
                        AdminRealtimeContract.OpsHealthSnapshotEventName, JsonSerializer.SerializeToElement(snapshot, AdminRealtimeGroupsJsonContext.Default.OpsHealthSnapshotResponse)), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    OpsHealthAppendFailed(_logger, exception);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private static DeployOperationRealtimeEvent MapDeployEvent(WorkflowOperationTransition transition)
    {
        var occurredTicks = transition.OccurredAt.UtcTicks.ToString(CultureInfo.InvariantCulture);
        return new DeployOperationRealtimeEvent(
            EventId: $"deploy:{transition.OperationId}:{transition.Kind}:{occurredTicks}",
            OperationId: transition.OperationId,
            TransitionKind: transition.Kind.ToString(),
            Status: transition.Operation.Status.ToString(),
            Phase: transition.Operation.CurrentPhase,
            TargetId: transition.TargetId,
            ReleaseId: transition.ReleaseId,
            CorrelationId: transition.CorrelationId,
            OperationInstanceId: transition.Operation.Audit.OperationInstanceId,
            AuditId: transition.Operation.Audit.AuditId,
            ProposalId: transition.Operation.Audit.ProposalId,
            OccurredAt: transition.OccurredAt);
    }

    public override void Dispose()
    {
        _flushSignal.Flushed -= OnFlushed;
        _opsHealth.Writer.TryComplete();
        base.Dispose();
    }

    [LoggerMessage(EventId = 9311, Level = LogLevel.Warning, Message = "Ops-health realtime append failed; clients reconcile via history.")]
    private static partial void OpsHealthAppendFailed(ILogger logger, Exception exception);
}
