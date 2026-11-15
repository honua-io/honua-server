// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Realtime;
using Npgsql;

namespace Honua.Server.Features.Realtime;

// What both log-backed providers share: appends go to the realtime log and wake this task's
// dispatcher at once; subscriptions are this task's dispatcher's. They differ in how other tasks'
// events wake the dispatcher, which the tail service decides, and so in their capabilities.
internal abstract class RealtimeLogFanoutProvider(
    PostgresRealtimeEventLog log,
    RealtimeDispatcher dispatcher,
    RealtimePresenceBatcher presence,
    ILeaseStore? leases) : IRealtimeFanoutProvider
{
    public abstract RealtimeProviderCapabilities Capabilities { get; }

    public async ValueTask<RealtimePosition> AppendAsync(RealtimeEventDraft draft, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var npgsqlTransaction = transaction switch
        {
            null => null,
            NpgsqlTransaction postgres => postgres,
            _ => throw new ArgumentException("The realtime log joins PostgreSQL transactions only.", nameof(transaction))
        };

        var head = await log.AppendAsync(
                draft.Family.Name,
                [new RealtimeLogRow(draft.ScopeKey, draft.Kind, draft.Payload)],
                npgsqlTransaction,
                cancellationToken)
            .ConfigureAwait(false);

        // In a caller's transaction the row is visible only after commit; the notification (or the
        // local poll) then wakes the dispatcher again.
        dispatcher.Wake();
        return new RealtimePosition(draft.Family, head);
    }

    public void Enqueue(RealtimeEventDraft draft) => presence.Enqueue(draft);

    public IRealtimeSubscription Subscribe(RealtimeSubscriptionRequest request) => dispatcher.Subscribe(request);

    public ValueTask<IRealtimeStreamLease?> TryAcquireMcpStreamAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return RealtimeStreamLease.TryAcquireAsync(
            leases ?? throw new InvalidOperationException("MCP stream ownership requires the shared PostgreSQL lease store."),
            sessionId, cancellationToken);
    }

    public async ValueTask<RealtimeLogWindow> GetWindowAsync(RealtimeFamily family, CancellationToken cancellationToken)
        => await dispatcher.ReadWindowAsync(family.Name, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<RealtimeEvent>> ReadPresencePageAsync(
        string scopeKey, long after, long through, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        ArgumentOutOfRangeException.ThrowIfNegative(after);
        ArgumentOutOfRangeException.ThrowIfLessThan(through, after);
        // A fixed page bound also bounds memory when a caller configures a large replay allowance.
        return await log.ReadAfterAsync(
            new RealtimeLogRead(RealtimeLogFamilies.CollabPresence, after, 256, [scopeKey], through),
            cancellationToken).ConfigureAwait(false);
    }
}

// One task, or tasks that each serve only their own clients: woken locally and by a slow poll.
internal sealed class InProcessRealtimeFanoutProvider(
    PostgresRealtimeEventLog log,
    RealtimeDispatcher dispatcher,
    RealtimePresenceBatcher presence,
    ILeaseStore? leases = null) : RealtimeLogFanoutProvider(log, dispatcher, presence, leases)
{
    private static readonly RealtimeProviderCapabilities _capabilities = new(CrossNode: false, OrderedPerFamily: true, Resumable: true);

    public override RealtimeProviderCapabilities Capabilities => _capabilities;
}

// Every task: woken by PostgreSQL notifications, with a safety poll and a degraded poll.
internal sealed class PostgresRealtimeFanoutProvider(
    PostgresRealtimeEventLog log,
    RealtimeDispatcher dispatcher,
    RealtimePresenceBatcher presence,
    ILeaseStore? leases = null) : RealtimeLogFanoutProvider(log, dispatcher, presence, leases)
{
    private static readonly RealtimeProviderCapabilities _capabilities = new(CrossNode: true, OrderedPerFamily: true, Resumable: true);

    public override RealtimeProviderCapabilities Capabilities => _capabilities;
}
