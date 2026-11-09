// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Console.Collaboration.Domain;

namespace Honua.Core.Features.Console.Collaboration.Abstractions;

public interface IStudioMapCollaborationStore
{
    Task<IReadOnlyList<StudioMapCommentThread>> ListThreadsAsync(
        string mapId,
        CancellationToken cancellationToken = default);

    Task<StudioMapCommentThread?> GetThreadAsync(
        string mapId,
        Guid threadId,
        CancellationToken cancellationToken = default);

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

    Task<StudioMapCommentThread?> AddReplyAsync(
        string mapId,
        Guid threadId,
        string authorName,
        string? authorId,
        string body,
        CancellationToken cancellationToken = default);

    Task<StudioMapCommentThread?> SetResolvedAsync(
        string mapId,
        Guid threadId,
        bool resolved,
        string actorName,
        string? actorId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StudioMapActivityEvent>> ListActivityAsync(
        string mapId,
        int limit,
        CancellationToken cancellationToken = default);
}
