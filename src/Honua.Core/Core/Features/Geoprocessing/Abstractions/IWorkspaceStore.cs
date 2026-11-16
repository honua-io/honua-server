// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

public interface IWorkspaceStore
{
    Task<Workspace> CreateAsync(Workspace workspace, CancellationToken cancellationToken = default);

    Task<Workspace?> GetAsync(string workspaceId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Workspace>> ListByOwnerAsync(string ownerId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Workspace>> ListExpiredAsync(DateTimeOffset threshold, int maxCount, CancellationToken cancellationToken = default);

    Task<bool> TransitionStateAsync(string workspaceId, WorkspaceLifecycleState newState, CancellationToken cancellationToken = default);

    Task<bool> ExtendExpirationAsync(string workspaceId, DateTimeOffset newExpiration, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string workspaceId, CancellationToken cancellationToken = default);

    Task<WorkspaceUsageSummary> GetUsageSummaryAsync(string ownerId, CancellationToken cancellationToken = default);
}

public sealed record WorkspaceUsageSummary
{
    public int ActiveWorkspaceCount { get; init; }

    public int TotalArtifactCount { get; init; }

    public long TotalStorageBytes { get; init; }
}
