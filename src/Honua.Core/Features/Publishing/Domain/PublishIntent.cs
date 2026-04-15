// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Publishing.Domain;

/// <summary>
/// Captures the operator's intent to publish source artifacts to a managed target surface.
/// Links to canonical artifact and result-package semantics rather than maintaining a private storage model.
/// </summary>
public sealed record PublishIntent
{
    /// <summary>
    /// Stable identifier for this publish intent.
    /// </summary>
    public required string IntentId { get; init; }

    /// <summary>
    /// Category of the source being published.
    /// </summary>
    public required PublishSourceKind SourceKind { get; init; }

    /// <summary>
    /// Identifier of the source (result package ID, workspace ID, layer ID, or dataset ID).
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Specific artifact identifiers to publish from the source. Empty means publish all eligible artifacts.
    /// </summary>
    public IReadOnlyList<string> ArtifactIds { get; init; } = [];

    /// <summary>
    /// Category of the target surface.
    /// </summary>
    public required PublishTargetKind TargetKind { get; init; }

    /// <summary>
    /// Target-specific configuration such as service name, endpoint path, or visibility settings.
    /// </summary>
    public IReadOnlyDictionary<string, string> TargetConfig { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Current lifecycle status of this intent.
    /// </summary>
    public required PublishIntentStatus Status { get; init; }

    /// <summary>
    /// Operator identity and request metadata captured when the intent was created.
    /// </summary>
    public OperationAuditInfo Audit { get; init; } = new();

    /// <summary>
    /// When the intent was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the intent was most recently updated.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Reason the intent was rejected, when applicable.
    /// </summary>
    public string? RejectionReason { get; init; }

    /// <summary>
    /// Identifier of the published service created upon successful completion.
    /// </summary>
    public string? PublishedServiceId { get; init; }

    /// <summary>
    /// Creates a new draft publish intent for the given source and target.
    /// </summary>
    public static PublishIntent CreateDraft(
        string intentId,
        PublishSourceKind sourceKind,
        string sourceId,
        PublishTargetKind targetKind,
        OperationAuditInfo? audit = null,
        IReadOnlyList<string>? artifactIds = null,
        IReadOnlyDictionary<string, string>? targetConfig = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new PublishIntent
        {
            IntentId = intentId,
            SourceKind = sourceKind,
            SourceId = sourceId,
            ArtifactIds = artifactIds ?? [],
            TargetKind = targetKind,
            TargetConfig = targetConfig ?? new Dictionary<string, string>(),
            Status = PublishIntentStatus.Draft,
            Audit = audit ?? new OperationAuditInfo(),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>
    /// Transitions the intent to validated status.
    /// </summary>
    public PublishIntent WithValidated()
        => this with { Status = PublishIntentStatus.Validated, UpdatedAt = DateTimeOffset.UtcNow };

    /// <summary>
    /// Transitions the intent to awaiting-approval status.
    /// </summary>
    public PublishIntent WithAwaitingApproval()
        => this with { Status = PublishIntentStatus.AwaitingApproval, UpdatedAt = DateTimeOffset.UtcNow };

    /// <summary>
    /// Transitions the intent to approved status.
    /// </summary>
    public PublishIntent WithApproved()
        => this with { Status = PublishIntentStatus.Approved, UpdatedAt = DateTimeOffset.UtcNow };

    /// <summary>
    /// Transitions the intent to executing status.
    /// </summary>
    public PublishIntent WithExecuting()
        => this with { Status = PublishIntentStatus.Executing, UpdatedAt = DateTimeOffset.UtcNow };

    /// <summary>
    /// Transitions the intent to completed status with the resulting service identifier.
    /// </summary>
    public PublishIntent WithCompleted(string publishedServiceId)
        => this with { Status = PublishIntentStatus.Completed, PublishedServiceId = publishedServiceId, UpdatedAt = DateTimeOffset.UtcNow };

    /// <summary>
    /// Transitions the intent to rejected status with a reason.
    /// </summary>
    public PublishIntent WithRejected(string reason)
        => this with { Status = PublishIntentStatus.Rejected, RejectionReason = reason, UpdatedAt = DateTimeOffset.UtcNow };

    /// <summary>
    /// Transitions the intent to failed status.
    /// </summary>
    public PublishIntent WithFailed(string? reason = null)
        => this with { Status = PublishIntentStatus.Failed, RejectionReason = reason, UpdatedAt = DateTimeOffset.UtcNow };

    /// <summary>
    /// Transitions the intent to cancelled status.
    /// </summary>
    public PublishIntent WithCancelled()
        => this with { Status = PublishIntentStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };
}
