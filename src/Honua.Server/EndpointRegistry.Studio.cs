// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> StudioEndpoints =>
    [
        new("POST", "/api/v1/agent/workspaces"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/processes/local/project/identity"),
        new("POST", "/api/v1/agent/workspaces/{workspaceId}/processes/local/project/bind"),
        new("DELETE", "/api/v1/agent/workspaces/{workspaceId}/processes/local/project/bindings/{bindingId}"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/processes/local/project"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/processes/local/sources/{**path}"),
        new("PUT", "/api/v1/agent/workspaces/{workspaceId}/processes/local/sources/{**path}"),
        new("POST", "/api/v1/agent/workspaces/{workspaceId}/processes/local/snapshots"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/processes/local/snapshots/{snapshotId}"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/processes/local/snapshots/{snapshotId}/sources/{**path}"),
        new("POST", "/api/v1/agent/workspaces/{workspaceId}/processes/local/runs"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/processes/local/runs"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/processes/local/runs/{runId}"),
        new("POST", "/api/v1/agent/workspaces/{workspaceId}/processes/local/runs/{runId}/cancel"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/processes/local/commands/{commandId}"),
        new("GET", "/api/v1/agent/workspaces/{workspaceId}/processes/local/runs/{runId}/artifacts/{**path}"),

        // v1 Studio package lifecycle endpoints
        new("GET", "/api/v1/studio/package-families"),
        new("GET", "/api/v1/studio/session"),
        new("POST", "/api/v1/studio/package-drafts"),
        // v1 Studio content-item and draft enumeration endpoints
        new("GET", "/api/v1/studio/content-items"),
        new("GET", "/api/v1/studio/package-drafts"),
        new("GET", "/api/v1/studio/package-drafts/{draftId}"),
        new("GET", "/api/v1/studio/drafts/{draftId}/revisions"),
        new("GET", "/api/v1/studio/drafts/{draftId}/revisions/{generation}"),
        new("POST", "/api/v1/studio/drafts/{draftId}/revisions/{generation}/restore"),
        new("PUT", "/api/v1/studio/package-drafts/{draftId}"),
        new("DELETE", "/api/v1/studio/package-drafts/{draftId}"),
        new("POST", "/api/v1/studio/package-drafts/{draftId}/validate"),
        new("POST", "/api/v1/studio/package-drafts/{draftId}/preview-plan"),
        new("POST", "/api/v1/studio/package-drafts/{draftId}/content-versions"),
        new("GET", "/api/v1/studio/content-items/{itemId}/versions"),
        new("GET", "/api/v1/studio/content-items/{itemId}/versions/{versionId}"),
        new("POST", "/api/v1/studio/content-items/{itemId}/version-comparisons"),
        new("POST", "/api/v1/studio/content-items/{itemId}/versions/{versionId}/publish-requests"),
        new("GET", "/api/v1/studio/content-items/{itemId}/versions/{versionId}/publish-requests/{requestId}"),
        new("POST", "/api/v1/studio/content-items/{itemId}/versions/{versionId}/reopen"),
        new("POST", "/api/v1/studio/content-items/{itemId}/rollback-requests"),
        // Studio published-route resolver: serves an Active publication's activeUrl.
        new("GET", "/api/v1/studio/published/{*route}"),
        // Deterministic Map/App draft creation (ADR-0076).
        new("POST", "/api/v1/studio/apps/generate"),
        new("POST", "/api/v1/studio/maps/generate"),
        // Studio deliverable export: render a map/dashboard/report content item to PDF/PNG.
        new("POST", "/api/v1/studio/{kind}/{id}/export"),

        // v1 Studio map collaboration: comment threads + activity feed
        new("GET", "/api/v1/console/maps/{mapId}/collab/comments"),
        new("POST", "/api/v1/console/maps/{mapId}/collab/comments"),
        new("POST", "/api/v1/console/maps/{mapId}/collab/comments/{threadId}/replies"),
        new("POST", "/api/v1/console/maps/{mapId}/collab/comments/{threadId}/resolve"),
        new("GET", "/api/v1/console/maps/{mapId}/collab/activity"),

        // v1 content publication registry for Studio-generated artifacts, including the list
        new("POST", "/api/v1/console/publications"),
        new("GET", "/api/v1/console/publications"),
        new("GET", "/api/v1/console/publications/{publicationId}"),
        new("GET", "/api/v1/console/publications/{publicationId}/versions/{versionSelector}"),
        new("POST", "/api/v1/console/publications/{publicationId}/republish"),
        new("POST", "/api/v1/console/publications/{publicationId}/rollback"),
        new("PATCH", "/api/v1/console/publications/{publicationId}/policy"),

        // Provider-agnostic Studio AI proxy adapters: Bedrock/Anthropic/OpenAI-compatible
        // behind one streaming chat contract, plus capability discovery.
        new("GET", "/api/v1/studio/ai/capabilities"),
        new("POST", "/api/v1/studio/ai/chat"),
    ];
}
