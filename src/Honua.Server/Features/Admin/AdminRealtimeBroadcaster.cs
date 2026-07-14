// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Threading.Channels;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.SignalR;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Fans ops-health snapshots (from the #2553 flush signal) and deploy/workflow-operation transitions (from
/// the #2562 transition seam, forwarded by <see cref="RealtimeOperationTransitionListener"/>) out to the
/// admin hub groups over the Redis backplane (#2554).
/// </summary>
/// <remarks>
/// Isolation guarantees:
/// <list type="bullet">
/// <item>The flush-signal handler and the transition enqueue are non-blocking <c>TryWrite</c>s into bounded
/// channels — they never block the sampler loop or the store-write path, and never throw back into them.</item>
/// <item>The channels are bounded with <see cref="BoundedChannelFullMode.DropOldest"/> so a slow/backed-up
/// broadcast can never grow memory without bound. Dropped items are acceptable: the durable ops-health
/// history and the deploy-operations list API are the authoritative sources a client re-syncs from.</item>
/// <item>Every hub send is wrapped so a backplane/transport fault degrades to a logged miss instead of
/// faulting the loop — serving and the producers are unaffected (fail-open).</item>
/// </list>
/// </remarks>
internal sealed partial class AdminRealtimeBroadcaster : BackgroundService
{
    // Latest-wins: only the freshest ops-health snapshot matters, so a capacity of one that drops the older
    // pending snapshot coalesces bursts without buffering.
    private readonly Channel<OpsHealthSnapshotResponse> _opsHealth =
        Channel.CreateBounded<OpsHealthSnapshotResponse>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    // Each transition is an independent event; bound the buffer and drop the oldest under sustained
    // backpressure (clients reconcile via the deploy-operations list / operate-events APIs).
    private readonly Channel<WorkflowOperationTransition> _transitions =
        Channel.CreateBounded<WorkflowOperationTransition>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly IHubContext<AdminHub> _hubContext;
    private readonly OpsHealthFlushSignal _flushSignal;
    private readonly AdminRealtimeCapabilities _capabilities;
    private readonly ILogger<AdminRealtimeBroadcaster> _logger;

    public AdminRealtimeBroadcaster(
        IHubContext<AdminHub> hubContext,
        OpsHealthFlushSignal flushSignal,
        AdminRealtimeCapabilities capabilities,
        ILogger<AdminRealtimeBroadcaster> logger)
    {
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        _flushSignal = flushSignal ?? throw new ArgumentNullException(nameof(flushSignal));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Subscribe unconditionally; the handler short-circuits when the backplane is absent. Subscribing in
        // the constructor guarantees the seam is wired before the sampler produces its first flush.
        _flushSignal.Flushed += OnFlushed;
    }

    /// <summary>
    /// Forwards a workflow-operation transition to the deploy-operations / operate-events groups. Non-blocking
    /// and exception-isolated so the store-write path that raised the transition is never slowed or faulted.
    /// </summary>
    /// <param name="transition">The observed transition.</param>
    public void EnqueueTransition(WorkflowOperationTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);

        if (!_capabilities.BackplaneEnabled)
        {
            return;
        }

        _transitions.Writer.TryWrite(transition);
    }

    private void OnFlushed(object? sender, OpsHealthFlushedEventArgs args)
    {
        if (!_capabilities.BackplaneEnabled)
        {
            return;
        }

        try
        {
            // Non-blocking latest-wins write; the sampler is never awaited or blocked here.
            _opsHealth.Writer.TryWrite(args.Snapshot);
        }
        // Intentional catch-all: a broadcast-enqueue fault must never propagate
        // into the sampler that raised this event.
        catch (Exception ex)
        {
            FlushEnqueueFailed(_logger, ex);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_capabilities.BackplaneEnabled)
        {
            return;
        }

        await Task.WhenAll(
            DrainOpsHealthAsync(stoppingToken),
            DrainTransitionsAsync(stoppingToken)).ConfigureAwait(false);
    }

    private async Task DrainOpsHealthAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var snapshot in _opsHealth.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _hubContext.Clients
                        .Group(AdminRealtimeContract.OpsHealthGroup)
                        .SendAsync(AdminRealtimeContract.OpsHealthSnapshotEventName, snapshot, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                // Intentional catch-all: this is a per-item send inside the ops-health
                // drain loop; fail-open, a backplane/transport fault drops this push
                // and clients re-sync via history rather than aborting the loop.
                catch (Exception ex)
                {
                    OpsHealthBroadcastFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task DrainTransitionsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var transition in _transitions.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var deployEvent = MapDeployEvent(transition);
                    await _hubContext.Clients
                        .Group(AdminRealtimeContract.DeployOperationsGroup)
                        .SendAsync(AdminRealtimeContract.DeployOperationEventName, deployEvent, stoppingToken)
                        .ConfigureAwait(false);

                    // The operate-events group receives the same wire shape as GET observability/events.
                    var operateEvent = OperateEventResponseMapper.Map(ReleaseTimelineEventFactory.Create(transition));
                    await _hubContext.Clients
                        .Group(AdminRealtimeContract.OperateEventsGroup)
                        .SendAsync(AdminRealtimeContract.OperateEventEventName, operateEvent, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                // Intentional catch-all: this is a per-item send inside the transitions
                // drain loop; fail-open, clients reconcile via the deploy-operations
                // list / operate-events APIs rather than aborting the loop.
                catch (Exception ex)
                {
                    TransitionBroadcastFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
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
            OccurredAt: transition.OccurredAt);
    }

    public override void Dispose()
    {
        _flushSignal.Flushed -= OnFlushed;
        _opsHealth.Writer.TryComplete();
        _transitions.Writer.TryComplete();
        base.Dispose();
    }

    [LoggerMessage(EventId = 9310, Level = LogLevel.Warning, Message = "Ops-health realtime enqueue threw.")]
    private static partial void FlushEnqueueFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9311, Level = LogLevel.Warning, Message = "Ops-health realtime broadcast failed; dropping this push (clients re-sync via history).")]
    private static partial void OpsHealthBroadcastFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9312, Level = LogLevel.Warning, Message = "Deploy/operate realtime broadcast failed; dropping this push (clients re-sync via list API).")]
    private static partial void TransitionBroadcastFailed(ILogger logger, Exception exception);
}
