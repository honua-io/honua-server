// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Text.Json;
using System.Threading.Channels;

namespace Honua.Core.Features.Realtime;

/// <summary>The only seam realtime features use to publish and subscribe (ADR-0085).</summary>
public interface IRealtimeFanoutProvider
{
    RealtimeProviderCapabilities Capabilities { get; }

    /// <summary>Appends one event; joins <paramref name="transaction"/> when given.</summary>
    ValueTask<RealtimePosition> AppendAsync(RealtimeEventDraft draft, DbTransaction? transaction, CancellationToken cancellationToken);

    /// <summary>Queues a best-effort event that is appended in the next batch (presence only).</summary>
    void Enqueue(RealtimeEventDraft draft);

    /// <summary>Starts a subscription that replays after <c>request.After</c> and then follows the log.</summary>
    IRealtimeSubscription Subscribe(RealtimeSubscriptionRequest request);

    ValueTask<RealtimeLogWindow> GetWindowAsync(RealtimeFamily family, CancellationToken cancellationToken);
}

/// <summary>What a provider guarantees. Features gate behaviour on these, never on the provider type.</summary>
public sealed record RealtimeProviderCapabilities(bool CrossNode, bool OrderedPerFamily, bool Resumable);

/// <summary>An event family: one ordered sequence of events (specification section 3).</summary>
public readonly record struct RealtimeFamily
{
    public RealtimeFamily(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }

    public static RealtimeFamily Features { get; } = new("features");
    public static RealtimeFamily CollabOps { get; } = new("collab-ops");
    public static RealtimeFamily CollabPresence { get; } = new("collab-presence");
    public static RealtimeFamily Observations { get; } = new("observations");
    public static RealtimeFamily Alerts { get; } = new("alerts");
    public static RealtimeFamily Jobs { get; } = new("jobs");
    public static RealtimeFamily Catalog { get; } = new("catalog");
    public static RealtimeFamily Admin { get; } = new("admin");
    public static RealtimeFamily Security { get; } = new("security");
    public static RealtimeFamily Mcp { get; } = new("mcp");

    public override string ToString() => Name;
}

/// <summary>A position in one family: deliver events after <see cref="Sequence"/>.</summary>
public readonly record struct RealtimePosition(RealtimeFamily Family, long Sequence)
{
    /// <summary>The SSE event id, <c>family:sequence</c>.</summary>
    public override string ToString() => $"{Family.Name}:{Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}

/// <summary>
/// One event to publish. <paramref name="CoalesceKey"/> is used only by <see cref="IRealtimeFanoutProvider.Enqueue"/>:
/// of several queued drafts with the same family, scope and key, only the latest is appended.
/// </summary>
public sealed record RealtimeEventDraft(
    RealtimeFamily Family,
    string ScopeKey,
    string Kind,
    JsonElement Payload,
    string? CoalesceKey = null);

/// <summary>A committed event as dispatch sees it.</summary>
public sealed record RealtimeEvent(
    RealtimeFamily Family,
    long Sequence,
    string ScopeKey,
    string Kind,
    JsonElement Payload,
    DateTimeOffset CreatedAt)
{
    public RealtimePosition Position => new(Family, Sequence);
}

/// <summary>The retained range of a family: events after <see cref="Oldest"/> - 1 up to <see cref="Head"/>.</summary>
public sealed record RealtimeLogWindow(RealtimeFamily Family, long Oldest, long Head);

/// <summary>
/// A subscription: the family, the scope keys (empty means every scope), the position to replay
/// after (null for live only), the bounded channel the transport reads and a filter the feature
/// supplies for authorization and matching. The provider never waits for the channel while
/// following the log.
/// </summary>
public sealed record RealtimeSubscriptionRequest(
    RealtimeFamily Family,
    IReadOnlyCollection<string> ScopeKeys,
    long? After,
    ChannelWriter<RealtimeDelivery> Writer,
    Func<RealtimeEvent, bool>? Filter = null);

/// <summary>An open subscription. Disposing it stops delivery; the provider completes the writer.</summary>
public interface IRealtimeSubscription : IAsyncDisposable
{
    RealtimeFamily Family { get; }

    /// <summary>The last sequence delivered or skipped by this subscription.</summary>
    long Position { get; }
}

/// <summary>What a transport writes: an event, a reset that tells the client to resynchronize, or a heartbeat.</summary>
public abstract record RealtimeDelivery
{
    private RealtimeDelivery()
    {
    }

    public sealed record Event(RealtimeEvent Value) : RealtimeDelivery;

    public sealed record Reset(string Reason, long Head) : RealtimeDelivery;

    public sealed record Heartbeat : RealtimeDelivery
    {
        public static Heartbeat Instance { get; } = new();
    }
}

/// <summary>Completes a subscription's channel when the provider closes it, for example a slow consumer.</summary>
public sealed class RealtimeSubscriptionClosedException : Exception
{
    public RealtimeSubscriptionClosedException()
        : this("closed")
    {
    }

    public RealtimeSubscriptionClosedException(string reason)
        : base($"The realtime subscription was closed: {reason}.")
        => Reason = reason;

    public RealtimeSubscriptionClosedException(string message, Exception innerException)
        : base(message, innerException)
        => Reason = "closed";

    public string Reason { get; }
}

/// <summary>The reasons a subscription is reset.</summary>
public static class RealtimeResetReasons
{
    public const string Retention = "retention";
    public const string CursorEpoch = "cursor-epoch";
    public const string TooFarBehind = "too-far-behind";
    public const string SlowConsumer = "slow-consumer";
}
