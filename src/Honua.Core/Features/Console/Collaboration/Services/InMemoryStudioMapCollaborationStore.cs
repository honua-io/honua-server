// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Collaboration.Abstractions;
using Honua.Core.Features.Console.Collaboration.Domain;

namespace Honua.Core.Features.Console.Collaboration.Services;

/// <summary>
/// In-memory Studio map collaboration store for tests and local fallback when no
/// durable Postgres backing is configured. Thread-safe via a coarse lock.
/// </summary>
public sealed class InMemoryStudioMapCollaborationStore : IStudioMapCollaborationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, StudioMapCommentThread> _threads = new();
    private readonly List<StudioMapActivityEvent> _activity = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the store with the supplied clock.</summary>
    public InMemoryStudioMapCollaborationStore(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public Task<IReadOnlyList<StudioMapCommentThread>> ListThreadsAsync(
        string mapId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<StudioMapCommentThread> threads = _threads.Values
                .Where(t => string.Equals(t.MapId, mapId, StringComparison.Ordinal))
                .OrderByDescending(t => t.UpdatedAt)
                .ToList();
            return Task.FromResult(threads);
        }
    }

    /// <inheritdoc />
    public Task<StudioMapCommentThread?> GetThreadAsync(
        string mapId,
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(FindThread(mapId, threadId));
        }
    }

    /// <inheritdoc />
    public Task<StudioMapCommentThread> CreateThreadAsync(
        string mapId,
        string featureLabel,
        string layerRef,
        double anchorX,
        double anchorY,
        string authorName,
        string? authorId,
        string body,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        var thread = new StudioMapCommentThread
        {
            ThreadId = Guid.NewGuid(),
            MapId = mapId,
            FeatureLabel = featureLabel,
            LayerRef = layerRef,
            AnchorX = anchorX,
            AnchorY = anchorY,
            Resolved = false,
            Messages =
            [
                new StudioMapCommentMessage
                {
                    MessageId = Guid.NewGuid(),
                    AuthorName = authorName,
                    AuthorId = authorId,
                    Body = body,
                    CreatedAt = now,
                },
            ],
            CreatedBy = authorId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        lock (_gate)
        {
            _threads[thread.ThreadId] = thread;
            RecordActivity(mapId, StudioMapActivityKind.ThreadOpened, authorName, authorId,
                $"opened a comment on {featureLabel}", thread.ThreadId, now);
        }

        return Task.FromResult(thread);
    }

    /// <inheritdoc />
    public Task<StudioMapCommentThread?> AddReplyAsync(
        string mapId,
        Guid threadId,
        string authorName,
        string? authorId,
        string body,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            var existing = FindThread(mapId, threadId);
            if (existing is null)
            {
                return Task.FromResult<StudioMapCommentThread?>(null);
            }

            var messages = existing.Messages.ToList();
            messages.Add(new StudioMapCommentMessage
            {
                MessageId = Guid.NewGuid(),
                AuthorName = authorName,
                AuthorId = authorId,
                Body = body,
                CreatedAt = now,
            });

            var updated = existing with { Messages = messages, UpdatedAt = now };
            _threads[threadId] = updated;
            RecordActivity(mapId, StudioMapActivityKind.CommentPosted, authorName, authorId,
                $"replied on {existing.FeatureLabel}", threadId, now);
            return Task.FromResult<StudioMapCommentThread?>(updated);
        }
    }

    /// <inheritdoc />
    public Task<StudioMapCommentThread?> SetResolvedAsync(
        string mapId,
        Guid threadId,
        bool resolved,
        string actorName,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            var existing = FindThread(mapId, threadId);
            if (existing is null)
            {
                return Task.FromResult<StudioMapCommentThread?>(null);
            }

            var updated = existing with { Resolved = resolved, UpdatedAt = now };
            _threads[threadId] = updated;
            var kind = resolved ? StudioMapActivityKind.ThreadResolved : StudioMapActivityKind.ThreadReopened;
            var verb = resolved ? "resolved" : "reopened";
            RecordActivity(mapId, kind, actorName, actorId,
                $"{verb} the comment on {existing.FeatureLabel}", threadId, now);
            return Task.FromResult<StudioMapCommentThread?>(updated);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StudioMapActivityEvent>> ListActivityAsync(
        string mapId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<StudioMapActivityEvent> events = _activity
                .Where(e => string.Equals(e.MapId, mapId, StringComparison.Ordinal))
                .OrderByDescending(e => e.OccurredAt)
                .Take(limit)
                .ToList();
            return Task.FromResult(events);
        }
    }

    private StudioMapCommentThread? FindThread(string mapId, Guid threadId)
        => _threads.TryGetValue(threadId, out var thread)
            && string.Equals(thread.MapId, mapId, StringComparison.Ordinal)
            ? thread
            : null;

    private void RecordActivity(
        string mapId,
        StudioMapActivityKind kind,
        string actorName,
        string? actorId,
        string action,
        Guid threadId,
        DateTimeOffset occurredAt)
    {
        _activity.Add(new StudioMapActivityEvent
        {
            EventId = Guid.NewGuid(),
            MapId = mapId,
            Kind = kind,
            ActorName = actorName,
            ActorId = actorId,
            Action = action,
            ThreadId = threadId,
            OccurredAt = occurredAt,
        });
    }
}
