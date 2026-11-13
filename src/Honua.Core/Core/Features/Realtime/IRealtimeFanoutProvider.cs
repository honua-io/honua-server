// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Text.Json;
using System.Threading.Channels;

namespace Honua.Core.Features.Realtime;

public interface IRealtimeFanoutProvider
{
    RealtimeProviderCapabilities Capabilities { get; }

    // Joins the supplied transaction; otherwise the append commits before returning.
    ValueTask<RealtimePosition> AppendAsync(RealtimeEventDraft draft, DbTransaction? transaction, CancellationToken cancellationToken);

    // Best effort, for presence only.
    void Enqueue(RealtimeEventDraft draft);

    IRealtimeSubscription Subscribe(RealtimeSubscriptionRequest request);

    ValueTask<RealtimeLogWindow> GetWindowAsync(RealtimeFamily family, CancellationToken cancellationToken);
}

public sealed record RealtimeProviderCapabilities(bool CrossNode, bool OrderedPerFamily, bool Resumable);

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

public readonly record struct RealtimePosition(RealtimeFamily Family, long Sequence)
{
    public override string ToString() => $"{Family.Name}:{Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}

// CoalesceKey keeps only the latest queued draft for each family, scope and key.
public sealed record RealtimeEventDraft(
    RealtimeFamily Family,
    string ScopeKey,
    string Kind,
    JsonElement Payload,
    string? CoalesceKey = null);

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

public sealed record RealtimeLogWindow(RealtimeFamily Family, long Oldest, long Head);

// Empty scopes mean all scopes; a null position starts live. Delivery never waits on the writer.
public sealed record RealtimeSubscriptionRequest(
    RealtimeFamily Family,
    IReadOnlyCollection<string> ScopeKeys,
    long? After,
    ChannelWriter<RealtimeDelivery> Writer,
    Func<RealtimeEvent, bool>? Filter = null);

public interface IRealtimeSubscription : IAsyncDisposable
{
    RealtimeFamily Family { get; }

    long Position { get; }
}

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

public static class RealtimeResetReasons
{
    public const string Retention = "retention";
    public const string CursorEpoch = "cursor-epoch";
    public const string TooFarBehind = "too-far-behind";
    public const string SlowConsumer = "slow-consumer";
}
