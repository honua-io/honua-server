// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Publishing.Content.Domain;

namespace Honua.Core.Features.Publishing.Content.Services;

/// <summary>
/// Pure projections from registry records to client-safe views. Kept in Core so the
/// redaction rules are unit-testable independent of the HTTP layer.
/// </summary>
public static class ContentPublicationProjections
{
    /// <summary>
    /// Projects a route state plus a resolved immutable version into a client-safe
    /// <see cref="PublishedArtifactView"/>. Excludes role lists and public-link
    /// hashes; surfaces the embed contract the client needs.
    /// </summary>
    /// <param name="route">The current route state (governs visibility/embed policy).</param>
    /// <param name="version">The resolved immutable version (governs content/app refs).</param>
    /// <param name="includeDependencies">When true, includes the dependency references.</param>
    public static PublishedArtifactView ToPublishedView(
        ContentPublicationRouteState route,
        ContentPublicationVersion version,
        bool includeDependencies)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(version);

        var embed = route.Policy.Embed;
        return new PublishedArtifactView
        {
            PublicationId = version.PublicationId,
            RouteSlug = route.RouteSlug,
            Kind = version.Kind,
            Revision = version.Revision,
            VersionId = version.VersionId,
            Title = version.Title,
            Visibility = route.Policy.Visibility,
            DefaultViewBbox = version.DefaultViewBbox,
            Embeddable = embed.AllowEmbedding,
            AllowedEmbedOrigins = embed.AllowEmbedding ? embed.AllowedOrigins : null,
            FrameAncestors = embed.AllowEmbedding ? embed.FrameAncestors : null,
            AppManifestId = version.AppManifestId,
            AppBundleArtifactId = version.AppBundleArtifactId,
            ContentHash = version.ContentHash,
            Dependencies = includeDependencies ? version.Dependencies : null,
            UpdatedAt = version.CreatedAt,
        };
    }
}
