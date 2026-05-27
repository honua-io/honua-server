// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Publishing.Content.Domain;

namespace Honua.Core.Features.Publishing.Content.Abstractions;

/// <summary>
/// Validates and applies content-publication operations. Owns route-slug
/// normalization, monotonic revision allocation, content hashing, dependency
/// validation, and atomic route-pointer plus event writes. Endpoints remain thin
/// adapters over this service.
/// </summary>
public interface IContentPublicationService
{
    /// <summary>
    /// Publishes a brand-new artifact version and claims a route.
    /// </summary>
    /// <exception cref="ContentPublicationValidationException">Invalid request.</exception>
    /// <exception cref="ContentPublicationConflictException">Route slug already claimed.</exception>
    /// <exception cref="ContentPublicationDependencyException">A dependency could not be validated.</exception>
    Task<ContentPublicationDetail> PublishAsync(
        PublishContentRequest request,
        string actor,
        string? correlationId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns route state plus versions for a publication, or null when not found.</summary>
    /// <exception cref="ContentPublicationValidationException">Publication id is not a valid GUID.</exception>
    Task<ContentPublicationDetail?> GetAsync(
        string publicationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one immutable version selected by revision (numeric or <c>v{n}</c>) or version id.
    /// </summary>
    /// <exception cref="ContentPublicationValidationException">Publication id or selector is not valid.</exception>
    Task<ContentPublicationVersion?> GetVersionAsync(
        string publicationId,
        string versionSelector,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new immutable version and moves the active route pointer to it.
    /// </summary>
    /// <exception cref="ContentPublicationValidationException">Invalid request.</exception>
    /// <exception cref="ContentPublicationConflictException">Publication missing or etag mismatch.</exception>
    /// <exception cref="ContentPublicationDependencyException">A dependency could not be validated.</exception>
    Task<ContentPublicationDetail> RepublishAsync(
        string publicationId,
        RepublishContentRequest request,
        string actor,
        string? correlationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the active route pointer to an earlier immutable version and records the
    /// rollback pointer/event. Does not create a new version.
    /// </summary>
    /// <exception cref="ContentPublicationValidationException">No target specified or target not found.</exception>
    /// <exception cref="ContentPublicationConflictException">Publication missing or etag mismatch.</exception>
    Task<ContentPublicationDetail> RollbackAsync(
        string publicationId,
        RollbackContentRequest request,
        string actor,
        string? correlationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates server-owned share/embed/visibility/public-link policy and records an
    /// audited event. Does not create a new version.
    /// </summary>
    /// <exception cref="ContentPublicationValidationException">Invalid policy request.</exception>
    /// <exception cref="ContentPublicationConflictException">Publication missing or etag mismatch.</exception>
    Task<ContentPublicationPolicyUpdateResult> UpdatePolicyAsync(
        string publicationId,
        UpdatePublicationPolicyRequest request,
        string actor,
        string? correlationId,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves the route state for a slug (active pointer), or null when not found.</summary>
    Task<ContentPublicationRouteState?> ResolveRouteAsync(
        string routeSlug,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a policy update. Carries the updated detail plus the one-time raw
/// public-link token when a link was created (returned once, never stored).
/// </summary>
public sealed record ContentPublicationPolicyUpdateResult
{
    /// <summary>Updated publication detail.</summary>
    public required ContentPublicationDetail Detail { get; init; }

    /// <summary>The stable id of a public link created by this update, if any.</summary>
    public string? CreatedPublicLinkId { get; init; }

    /// <summary>
    /// The raw token for a created public link, returned exactly once. Null unless the
    /// caller supplied a token to hash. Never persisted.
    /// </summary>
    public string? CreatedPublicLinkToken { get; init; }
}
