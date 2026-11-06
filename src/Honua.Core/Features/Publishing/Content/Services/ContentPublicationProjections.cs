// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Publishing.Content.Domain;

namespace Honua.Core.Features.Publishing.Content.Services;

public static class ContentPublicationProjections
{
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
