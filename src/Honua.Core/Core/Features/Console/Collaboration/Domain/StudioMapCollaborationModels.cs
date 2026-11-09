// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Console.Collaboration.Domain;

public sealed record StudioMapCommentThread
{
    public required Guid ThreadId { get; init; }

    public required string MapId { get; init; }

    public required string FeatureLabel { get; init; }

    public required string LayerRef { get; init; }

    public required double AnchorX { get; init; }

    public required double AnchorY { get; init; }

    public bool Resolved { get; init; }

    public required IReadOnlyList<StudioMapCommentMessage> Messages { get; init; }

    public string? CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record StudioMapCommentMessage
{
    public required Guid MessageId { get; init; }

    public required string AuthorName { get; init; }

    public string? AuthorId { get; init; }

    public required string Body { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record StudioMapActivityEvent
{
    public required Guid EventId { get; init; }

    public required string MapId { get; init; }

    public required StudioMapActivityKind Kind { get; init; }

    public required string ActorName { get; init; }

    public string? ActorId { get; init; }

    public required string Action { get; init; }

    public Guid? ThreadId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}

public enum StudioMapActivityKind
{
    ThreadOpened,

    CommentPosted,

    ThreadResolved,

    ThreadReopened,
}
