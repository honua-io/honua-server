// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Publishing.Domain;

public sealed record PublishedServiceRecord
{
    public required string ServiceId { get; init; }

    public required string IntentId { get; init; }

    public required PublishSourceKind SourceKind { get; init; }

    public required string SourceId { get; init; }

    public required PublishTargetKind TargetKind { get; init; }

    public required PublishedServiceStatus Status { get; init; }

    public IReadOnlyList<ArtifactRef> Artifacts { get; init; } = [];

    public string? Endpoint { get; init; }

    public RefreshPolicy? RefreshPolicy { get; init; }

    public required DateTimeOffset PublishedAt { get; init; }

    public DateTimeOffset? LastRefreshedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public OperationAuditInfo Audit { get; init; } = new();

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static PublishedServiceRecord CreateFromIntent(
        string serviceId,
        PublishIntent intent,
        IReadOnlyList<ArtifactRef>? artifacts = null,
        string? endpoint = null,
        RefreshPolicy? refreshPolicy = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new PublishedServiceRecord
        {
            ServiceId = serviceId,
            IntentId = intent.IntentId,
            SourceKind = intent.SourceKind,
            SourceId = intent.SourceId,
            TargetKind = intent.TargetKind,
            Status = PublishedServiceStatus.Provisioning,
            Artifacts = artifacts ?? [],
            Endpoint = endpoint,
            RefreshPolicy = refreshPolicy,
            PublishedAt = now,
            UpdatedAt = now,
            Audit = intent.Audit
        };
    }

    public PublishedServiceRecord WithActive(string? endpoint = null)
        => this with
        {
            Status = PublishedServiceStatus.Active,
            Endpoint = endpoint ?? Endpoint,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    public PublishedServiceRecord WithSuspended()
        => this with { Status = PublishedServiceStatus.Suspended, UpdatedAt = DateTimeOffset.UtcNow };

    public PublishedServiceRecord WithRefreshed(IReadOnlyList<ArtifactRef>? updatedArtifacts = null)
    {
        var now = DateTimeOffset.UtcNow;
        return this with
        {
            Status = PublishedServiceStatus.Active,
            Artifacts = updatedArtifacts ?? Artifacts,
            RefreshPolicy = RefreshPolicy?.WithRefreshCompleted(now),
            LastRefreshedAt = now,
            UpdatedAt = now
        };
    }

    public PublishedServiceRecord WithRefreshFailed(IReadOnlyList<string>? warnings = null)
        => this with
        {
            Status = PublishedServiceStatus.RefreshFailed,
            Warnings = warnings ?? Warnings,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    public PublishedServiceRecord WithDecommissioned()
        => this with { Status = PublishedServiceStatus.Decommissioned, UpdatedAt = DateTimeOffset.UtcNow };
}
