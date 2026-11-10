// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Publishing.Domain;

public sealed record PublishIntent
{
    public required string IntentId { get; init; }

    public required PublishSourceKind SourceKind { get; init; }

    public required string SourceId { get; init; }

    public IReadOnlyList<string> ArtifactIds { get; init; } = [];

    public required PublishTargetKind TargetKind { get; init; }

    public IReadOnlyDictionary<string, string> TargetConfig { get; init; } = new Dictionary<string, string>();

    public required PublishIntentStatus Status { get; init; }

    public OperationAuditInfo Audit { get; init; } = new();

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public string? RejectionReason { get; init; }

    public string? FailureReason { get; init; }

    public string? PublishedServiceId { get; init; }

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

    public PublishIntent WithValidated()
        => WithStatus(PublishIntentStatus.Validated);

    public PublishIntent WithAwaitingApproval()
        => WithStatus(PublishIntentStatus.AwaitingApproval);

    public PublishIntent WithApproved()
        => WithStatus(PublishIntentStatus.Approved);

    public PublishIntent WithExecuting()
        => WithStatus(PublishIntentStatus.Executing);

    public PublishIntent WithCompleted(string publishedServiceId)
        => this with
        {
            Status = PublishIntentStatus.Completed,
            PublishedServiceId = publishedServiceId,
            RejectionReason = null,
            FailureReason = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    public PublishIntent WithRejected(string reason)
        => this with
        {
            Status = PublishIntentStatus.Rejected,
            RejectionReason = reason,
            FailureReason = null,
            PublishedServiceId = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    public PublishIntent WithFailed(string? reason = null)
        => this with
        {
            Status = PublishIntentStatus.Failed,
            FailureReason = reason,
            RejectionReason = null,
            PublishedServiceId = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    public PublishIntent WithCancelled()
        => WithStatus(PublishIntentStatus.Cancelled);

    private PublishIntent WithStatus(PublishIntentStatus status)
        => this with
        {
            Status = status,
            RejectionReason = null,
            FailureReason = null,
            PublishedServiceId = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };
}
