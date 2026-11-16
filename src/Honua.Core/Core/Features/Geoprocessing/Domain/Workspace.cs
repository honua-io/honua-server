// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record Workspace
{
    public required string WorkspaceId { get; init; }

    public required WorkspaceKind Kind { get; init; }

    public required string Label { get; init; }

    public required string OwnerId { get; init; }

    public string? ScopeId { get; init; }

    public required WorkspaceLifecycleState State { get; init; }

    public string? Uri { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public IReadOnlyList<Artifact> Artifacts { get; init; } = [];

    public long StorageBytes { get; init; }

    public WorkspaceRef ToRef() => new()
    {
        WorkspaceId = WorkspaceId,
        Kind = Kind,
        Label = Label,
        Uri = Uri,
        ExpiresAt = ExpiresAt
    };

    public bool IsExpired(DateTimeOffset now) =>
        ExpiresAt.HasValue && now >= ExpiresAt.Value;
}

public sealed record Artifact
{
    public required string ArtifactId { get; init; }

    public required ArtifactKind Kind { get; init; }

    public required string Label { get; init; }

    public required ArtifactLifecycleState State { get; init; }

    public string? Uri { get; init; }

    public string? ContentType { get; init; }

    public long SizeBytes { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    public required string WorkspaceId { get; init; }

    public ArtifactRef ToRef() => new()
    {
        ArtifactId = ArtifactId,
        Kind = Kind,
        Label = Label,
        Uri = Uri,
        ContentType = ContentType,
        Metadata = Metadata
    };
}
