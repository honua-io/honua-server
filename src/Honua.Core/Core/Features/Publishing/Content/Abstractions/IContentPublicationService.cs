// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Publishing.Content.Domain;

namespace Honua.Core.Features.Publishing.Content.Abstractions;

public interface IContentPublicationService
{
    Task<ContentPublicationDetail> PublishAsync(
        PublishContentRequest request,
        string actor,
        string? correlationId,
        CancellationToken cancellationToken = default);

    Task<ContentPublicationDetail?> GetAsync(
        string publicationId,
        CancellationToken cancellationToken = default);

    Task<ContentPublicationListResponse> ListAsync(
        ContentPublicationListQuery query,
        CancellationToken cancellationToken = default);

    Task<ContentPublicationVersion?> GetVersionAsync(
        string publicationId,
        string versionSelector,
        CancellationToken cancellationToken = default);

    Task<ContentPublicationDetail> RepublishAsync(
        string publicationId,
        RepublishContentRequest request,
        string actor,
        string? correlationId,
        CancellationToken cancellationToken = default);

    Task<ContentPublicationDetail> RollbackAsync(
        string publicationId,
        RollbackContentRequest request,
        string actor,
        string? correlationId,
        CancellationToken cancellationToken = default);

    Task<ContentPublicationPolicyUpdateResult> UpdatePolicyAsync(
        string publicationId,
        UpdatePublicationPolicyRequest request,
        string actor,
        string? correlationId,
        CancellationToken cancellationToken = default);

    Task<ContentPublicationRouteState?> ResolveRouteAsync(
        string routeSlug,
        CancellationToken cancellationToken = default);
}

// A created public-link token is returned once and is never stored.
public sealed record ContentPublicationPolicyUpdateResult
{
    public required ContentPublicationDetail Detail { get; init; }

    public string? CreatedPublicLinkId { get; init; }

    public string? CreatedPublicLinkToken { get; init; }
}
