// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Abstractions;

/// <summary>Canonical durable operation boundary for Studio draft mutations.</summary>
public interface IStudioDraftMutationRuntime
{
    Task<StudioDraftMutationReceipt<StudioPackageDraft>> CreateAsync(
        CreateStudioPackageDraftCommand command,
        StudioDraftMutationContext context,
        CancellationToken cancellationToken = default);

    Task<StudioDraftMutationReceipt<StudioPackageDraft>> UpdateAsync(
        Guid draftId,
        UpdateStudioPackageDraftCommand command,
        StudioDraftMutationContext context,
        CancellationToken cancellationToken = default);

    Task<StudioDraftMutationReceipt<bool>> DeleteAsync(
        Guid draftId,
        StudioDraftMutationContext context,
        CancellationToken cancellationToken = default);

    Task<StudioDraftMutationReceipt<StudioValidationSummary>> ValidateAsync(
        Guid draftId,
        string? actorId,
        StudioDraftMutationContext context,
        CancellationToken cancellationToken = default);

    Task<StudioDraftMutationReceipt<StudioPreviewPlan>> PreviewAsync(
        Guid draftId,
        string? actorId,
        StudioDraftMutationContext context,
        CancellationToken cancellationToken = default);

    Task<StudioDraftMutationReceipt<StudioContentVersion>> SaveVersionAsync(
        Guid draftId, long expectedGeneration, string? changeNote, string? actorId, StudioDraftMutationContext context,
        CancellationToken cancellationToken = default);

    Task<StudioDraftMutationReceipt<StudioPublicationRequest>> CreatePublicationRequestAsync(
        Guid itemId, Guid versionId, StudioPublicationIntent? intent, string? warningAcknowledgement, string? actorId,
        StudioDraftMutationContext context, CancellationToken cancellationToken = default);

    Task<StudioDraftMutationReceipt<StudioPublicationRequest>> CreatePublicationRequestAsync(
        Guid itemId, Guid versionId, string contentHash, StudioPublicationIntent? intent,
        string? warningAcknowledgement, string? actorId,
        StudioDraftMutationContext context, CancellationToken cancellationToken = default)
        => CreatePublicationRequestAsync(
            itemId, versionId, intent, warningAcknowledgement, actorId, context, cancellationToken);

    Task<StudioDraftMutationReceipt<StudioPackageDraft>> ReopenVersionAsync(
        Guid itemId, Guid versionId, string? actorId, StudioDraftMutationContext context,
        CancellationToken cancellationToken = default);

    Task<StudioDraftMutationReceipt<StudioRollbackRequest>> RollbackAsync(
        Guid itemId, Guid targetVersionId, StudioRollbackPointer target, string? actorId, string? reason,
        StudioDraftMutationContext context, CancellationToken cancellationToken = default);
}

/// <summary>Trusted evidence supplied by an authorized protocol adapter.</summary>
public sealed record StudioDraftMutationContext
{
    public string? PrincipalId { get; init; }
    public string? TenantId { get; init; }
    public string? SchemaName { get; init; }
    public string? CorrelationId { get; init; }
    public string? IdempotencyKey { get; init; }
    public string AuthorizationOutcome { get; init; } = "authorized";
    public IReadOnlyList<string> Roles { get; init; } = [];
    public bool ScopeGoverned { get; init; }
    public IReadOnlyList<string> RecognizedScopes { get; init; } = [];

    /// <summary>
    /// Optional guardrail action the adapter declares for this mutation (for example
    /// <c>studio.publication_proposal</c>). The action tier can only tighten the edition policy.
    /// </summary>
    public string? ActionDiscriminator { get; init; }

    /// <summary>
    /// When <see langword="true"/>, a Studio publication request executes in this call.
    /// Only an adapter that has already established the caller is an admin may set it.
    /// The runtime then publishes through the same actuator an approved proposal would
    /// run, before the <c>studio.publication_proposal</c> guardrail is applied, and it
    /// does not send that principal through the proposal approve route (honua-server#5207).
    /// </summary>
    public bool PublishImmediately { get; init; }

    /// <summary>Operation id of a Studio publication request.</summary>
    public const string PublicationOperationId = "studio.content.create-publication-request";

    /// <summary>
    /// Authorization outcome that lets a Studio publication request execute before the
    /// guardrail ladder. Meaningful only for <see cref="PublicationOperationId"/>.
    /// </summary>
    public const string AdminDirectPublicationOutcome = "admin-direct-publication";
}

/// <summary>Durable envelope plus the typed projection produced by its actuator.</summary>
public sealed record StudioDraftMutationReceipt<T>
{
    public required OperationHandle Operation { get; init; }
    public T? Value { get; init; }
}
