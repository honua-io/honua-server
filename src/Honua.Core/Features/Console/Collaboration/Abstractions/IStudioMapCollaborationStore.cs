// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Collaboration.Domain;

namespace Honua.Core.Features.Console.Collaboration.Abstractions;

/// <summary>
/// Persistence contract for durable Studio map collaboration data (honua-server#1278,
/// slice 1): feature-pinned comment threads and the collaboration activity feed for a
/// map draft/package. The real-time presence/cursor/markup transport is a separate
/// slice and is intentionally not part of this contract.
/// </summary>
/// <remarks>
/// Recording a comment-thread mutation (open/reply/resolve/reopen) also appends a
/// corresponding <see cref="StudioMapActivityEvent"/> so the activity feed stays a
/// faithful, ordered projection of collaboration history.
/// </remarks>
public interface IStudioMapCollaborationStore
{
    /// <summary>Lists comment threads for a map, newest activity first.</summary>
    Task<IReadOnlyList<StudioMapCommentThread>> ListThreadsAsync(
        string mapId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a single thread by id, or <see langword="null"/> when not found.</summary>
    Task<StudioMapCommentThread?> GetThreadAsync(
        string mapId,
        Guid threadId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a new comment thread with its first message and anchor metadata.
    /// Returns the created thread.
    /// </summary>
    Task<StudioMapCommentThread> CreateThreadAsync(
        string mapId,
        string featureLabel,
        string layerRef,
        double anchorX,
        double anchorY,
        string authorName,
        string? authorId,
        string body,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a reply to an existing thread. Returns the updated thread, or
    /// <see langword="null"/> when the thread does not exist for the map.
    /// </summary>
    Task<StudioMapCommentThread?> AddReplyAsync(
        string mapId,
        Guid threadId,
        string authorName,
        string? authorId,
        string body,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the resolved flag for a thread. Returns the updated thread, or
    /// <see langword="null"/> when the thread does not exist for the map.
    /// </summary>
    Task<StudioMapCommentThread?> SetResolvedAsync(
        string mapId,
        Guid threadId,
        bool resolved,
        string actorName,
        string? actorId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists collaboration activity events for a map, newest first.</summary>
    Task<IReadOnlyList<StudioMapActivityEvent>> ListActivityAsync(
        string mapId,
        int limit,
        CancellationToken cancellationToken = default);
}
