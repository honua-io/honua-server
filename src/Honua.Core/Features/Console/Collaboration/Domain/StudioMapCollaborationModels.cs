// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Console.Collaboration.Domain;

/// <summary>
/// A feature-pinned comment thread anchored to a feature/layer on a Studio map
/// draft or package (honua-server#1278, slice 1). The anchor metadata
/// (<see cref="FeatureLabel"/>, <see cref="LayerRef"/>, <see cref="AnchorX"/>,
/// <see cref="AnchorY"/>) lets the Console comment drawer re-render the pin over
/// the map canvas. Threads are durable and survive map edits.
/// </summary>
public sealed record StudioMapCommentThread
{
    /// <summary>Stable thread identifier.</summary>
    public required Guid ThreadId { get; init; }

    /// <summary>Map draft/package the thread belongs to.</summary>
    public required string MapId { get; init; }

    /// <summary>Human-readable label of the anchored feature (e.g. "Parcel 1042").</summary>
    public required string FeatureLabel { get; init; }

    /// <summary>Layer reference the anchored feature belongs to.</summary>
    public required string LayerRef { get; init; }

    /// <summary>Normalized horizontal anchor position over the map canvas (0..1).</summary>
    public required double AnchorX { get; init; }

    /// <summary>Normalized vertical anchor position over the map canvas (0..1).</summary>
    public required double AnchorY { get; init; }

    /// <summary>Whether the thread has been resolved.</summary>
    public bool Resolved { get; init; }

    /// <summary>Messages in the thread, oldest first.</summary>
    public required IReadOnlyList<StudioMapCommentMessage> Messages { get; init; }

    /// <summary>Actor that opened the thread.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>When the thread was opened.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the thread last changed (new reply or resolve toggle).</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A single message within a <see cref="StudioMapCommentThread"/>.</summary>
public sealed record StudioMapCommentMessage
{
    /// <summary>Stable message identifier.</summary>
    public required Guid MessageId { get; init; }

    /// <summary>Display name of the author.</summary>
    public required string AuthorName { get; init; }

    /// <summary>Stable author identifier (audit actor), when known.</summary>
    public string? AuthorId { get; init; }

    /// <summary>Message body text.</summary>
    public required string Body { get; init; }

    /// <summary>When the message was posted.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// A durable collaboration activity event for a Studio map draft/package: who did
/// what and when. Slice 1 records the comment-thread lifecycle (thread opened,
/// reply posted, thread resolved/reopened); the real-time markup/presence stream
/// is deferred to slice 2.
/// </summary>
public sealed record StudioMapActivityEvent
{
    /// <summary>Stable activity event identifier.</summary>
    public required Guid EventId { get; init; }

    /// <summary>Map draft/package the event belongs to.</summary>
    public required string MapId { get; init; }

    /// <summary>Kind of collaboration event.</summary>
    public required StudioMapActivityKind Kind { get; init; }

    /// <summary>Display name of the actor that produced the event.</summary>
    public required string ActorName { get; init; }

    /// <summary>Stable actor identifier (audit actor), when known.</summary>
    public string? ActorId { get; init; }

    /// <summary>Human-readable action summary (e.g. "commented on Parcel 1042").</summary>
    public required string Action { get; init; }

    /// <summary>Related thread, when the event is comment-scoped.</summary>
    public Guid? ThreadId { get; init; }

    /// <summary>When the event occurred.</summary>
    public required DateTimeOffset OccurredAt { get; init; }
}

/// <summary>Category of a <see cref="StudioMapActivityEvent"/>.</summary>
public enum StudioMapActivityKind
{
    /// <summary>A comment thread was opened.</summary>
    ThreadOpened,

    /// <summary>A reply was posted to a thread.</summary>
    CommentPosted,

    /// <summary>A thread was resolved.</summary>
    ThreadResolved,

    /// <summary>A resolved thread was reopened.</summary>
    ThreadReopened,
}
