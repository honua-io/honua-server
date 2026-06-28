// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> StudioEndpoints =>
    [
        // v1 Studio package lifecycle endpoints (#1180)
        new("GET", "/api/v1/studio/package-families"),
        new("POST", "/api/v1/studio/package-drafts"),
        new("GET", "/api/v1/studio/package-drafts/{draftId}"),
        new("PUT", "/api/v1/studio/package-drafts/{draftId}"),
        new("DELETE", "/api/v1/studio/package-drafts/{draftId}"),
        new("POST", "/api/v1/studio/package-drafts/{draftId}/validate"),
        new("POST", "/api/v1/studio/package-drafts/{draftId}/preview-plan"),
        new("POST", "/api/v1/studio/package-drafts/{draftId}/content-versions"),
        new("GET", "/api/v1/studio/content-items/{itemId}/versions"),
        new("GET", "/api/v1/studio/content-items/{itemId}/versions/{versionId}"),
        new("POST", "/api/v1/studio/content-items/{itemId}/version-comparisons"),
        new("POST", "/api/v1/studio/content-items/{itemId}/versions/{versionId}/publish-requests"),
        new("POST", "/api/v1/studio/content-items/{itemId}/versions/{versionId}/reopen"),
        new("POST", "/api/v1/studio/content-items/{itemId}/rollback-requests"),
        // NL-assisted map package generation (#1180).
        new("POST", "/api/v1/studio/app-packages/generate"),
        new("POST", "/api/v1/studio/map-packages/generate"),
        // Studio deliverable export: render a map/dashboard/report content item to PDF/PNG.
        new("POST", "/api/v1/studio/{kind}/{id}/export"),

        // v1 Studio map collaboration: comment threads + activity feed (#1278, slice 1)
        new("GET", "/api/v1/console/maps/{mapId}/collab/comments"),
        new("POST", "/api/v1/console/maps/{mapId}/collab/comments"),
        new("POST", "/api/v1/console/maps/{mapId}/collab/comments/{threadId}/replies"),
        new("POST", "/api/v1/console/maps/{mapId}/collab/comments/{threadId}/resolve"),
        new("GET", "/api/v1/console/maps/{mapId}/collab/activity"),

        // v1 content publication registry for Studio-generated artifacts (#1183)
        new("POST", "/api/v1/console/publications"),
        new("GET", "/api/v1/console/publications/{publicationId}"),
        new("GET", "/api/v1/console/publications/{publicationId}/versions/{versionSelector}"),
        new("POST", "/api/v1/console/publications/{publicationId}/republish"),
        new("POST", "/api/v1/console/publications/{publicationId}/rollback"),
        new("PATCH", "/api/v1/console/publications/{publicationId}/policy"),
        // NL-assisted report/dashboard content generation (#1183).
        new("POST", "/api/v1/console/publications/generate"),

    ];
}
