// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Publishing.Domain;

/// <summary>
/// Persistent record of a managed published service created from a publish intent.
/// References canonical artifacts rather than maintaining a private copy of the data.
/// </summary>
public sealed record PublishedServiceRecord
{
    /// <summary>
    /// Stable identifier for this published service.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Identifier of the publish intent that created this service.
    /// </summary>
    public required string IntentId { get; init; }

    /// <summary>
    /// Category of the source that was published.
    /// </summary>
    public required PublishSourceKind SourceKind { get; init; }

    /// <summary>
    /// Identifier of the source (result package, workspace, layer, or dataset).
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Category of the target surface.
    /// </summary>
    public required PublishTargetKind TargetKind { get; init; }

    /// <summary>
    /// Current lifecycle status of this published service.
    /// </summary>
    public required PublishedServiceStatus Status { get; init; }

    /// <summary>
    /// Artifacts published to this service, reusing the canonical <see cref="ArtifactRef"/> model.
    /// </summary>
    public IReadOnlyList<ArtifactRef> Artifacts { get; init; } = [];

    /// <summary>
    /// Routable endpoint URI for the published service when available.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Refresh policy governing how this service is kept in sync with its source.
    /// </summary>
    public RefreshPolicy? RefreshPolicy { get; init; }

    /// <summary>
    /// When the service was initially published.
    /// </summary>
    public required DateTimeOffset PublishedAt { get; init; }

    /// <summary>
    /// When the service was most recently refreshed from its source.
    /// </summary>
    public DateTimeOffset? LastRefreshedAt { get; init; }

    /// <summary>
    /// When the service record was most recently updated.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Operator identity and request metadata captured when the service was created.
    /// </summary>
    public OperationAuditInfo Audit { get; init; } = new();

    /// <summary>
    /// Non-fatal warnings associated with this service.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Creates a new provisioning service record from a completed publish intent.
    /// </summary>
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

    /// <summary>
    /// Transitions the service to active status.
    /// </summary>
    public PublishedServiceRecord WithActive(string? endpoint = null)
        => this with
        {
            Status = PublishedServiceStatus.Active,
            Endpoint = endpoint ?? Endpoint,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    /// <summary>
    /// Transitions the service to suspended status.
    /// </summary>
    public PublishedServiceRecord WithSuspended()
        => this with { Status = PublishedServiceStatus.Suspended, UpdatedAt = DateTimeOffset.UtcNow };

    /// <summary>
    /// Records a successful refresh from the source.
    /// </summary>
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

    /// <summary>
    /// Records a failed refresh attempt.
    /// </summary>
    public PublishedServiceRecord WithRefreshFailed(IReadOnlyList<string>? warnings = null)
        => this with
        {
            Status = PublishedServiceStatus.RefreshFailed,
            Warnings = warnings ?? Warnings,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    /// <summary>
    /// Transitions the service to decommissioned status.
    /// </summary>
    public PublishedServiceRecord WithDecommissioned()
        => this with { Status = PublishedServiceStatus.Decommissioned, UpdatedAt = DateTimeOffset.UtcNow };
}
