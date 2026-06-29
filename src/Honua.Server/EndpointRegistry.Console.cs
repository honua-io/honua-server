// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> ConsoleEndpoints =>
    [
        // v1 console metadata v2 content + RBAC baseline (#1162)
        new("GET", "/api/v1/console/session"),
        new("GET", "/api/v1/console/content"),
        new("POST", "/api/v1/console/content"),
        new("GET", "/api/v1/console/content/search"),
        new("GET", "/api/v1/console/content/{id}"),
        new("PUT", "/api/v1/console/content/{id}"),
        new("PATCH", "/api/v1/console/content/{id}"),
        new("DELETE", "/api/v1/console/content/{id}"),
        new("GET", "/api/v1/console/content/{id}/provenance"),
        new("POST", "/api/v1/console/actions/check"),
        // v1 Console Share access public-link + embed API (#1215)
        new("GET", "/api/v1/console/content/{id}/share"),
        new("PUT", "/api/v1/console/content/{id}/share/access"),
        new("GET", "/api/v1/console/content/{id}/share/dependencies"),
        new("GET", "/api/v1/console/content/{id}/share/link"),
        new("POST", "/api/v1/console/content/{id}/share/link"),
        new("DELETE", "/api/v1/console/content/{id}/share/link/{tokenId}"),
        new("PUT", "/api/v1/console/content/{id}/share/embed"),
        new("POST", "/api/v1/console/content/{id}/share/embed"),
        new("GET", "/api/v1/console/share/content/{id}"),
        new("GET", "/api/v1/console/share/link/{token}"),
        new("POST", "/api/v1/console/share/embed/{token}/redeem"),
        // v1 Console open-data DCAT + STAC publication API (#1214)
        new("GET", "/api/v1/console/content/{id}/open-data"),
        new("PUT", "/api/v1/console/content/{id}/open-data"),
        new("GET", "/api/v1/console/content/{id}/open-data/eligibility"),
        new("GET", "/api/v1/console/content/{id}/open-data/dcat"),
        new("GET", "/api/v1/console/content/{id}/open-data/stac"),
        new("POST", "/api/v1/console/content/{id}/open-data/stac/publish"),
        new("DELETE", "/api/v1/console/content/{id}/open-data/stac"),
        new("GET", "/api/v1/open-data/datasets/{id}"),
        new("GET", "/api/v1/open-data/datasets/{id}/data.json"),
        new("GET", "/api/v1/open-data/datasets/{id}/schema.org"),
        new("GET", "/api/v1/open-data/stac"),
        new("GET", "/api/v1/open-data/stac/collections/{collectionId}"),
        new("GET", "/api/v1/open-data/stac/collections/{collectionId}/items/{itemId}"),
        // v1 Console catalog discovery-endpoints registry read API (#1279)
        new("GET", "/api/v1/console/catalog-endpoints/{workspaceId}"),
        new("GET", "/api/v1/console/catalog-endpoints/{workspaceId}/{endpointKey}"),
        new("GET", "/api/v1/console/catalog-endpoints/{workspaceId}/{endpointKey}/items/{itemId}"),
        new("POST", "/api/v1/admin/packages/validate"),
        new("POST", "/api/v1/admin/packages/preview"),
        new("GET", "/api/v1/console/workflow-node-registry"),
        new("GET", "/api/v1/console/workflow-node-registry/{nodeTypeId}"),

        // Honua Operations Toolset (descriptor/executor split, policy-gated dispatcher)
        new("GET", "/api/v1/operations"),
        new("POST", "/api/v1/operations/{id}/validate"),
        new("POST", "/api/v1/operations/{id}/submit"),
        new("GET", "/api/v1/operations/handles/{handleId}"),
        new("GET", "/api/v1/console/workflow-packages"),
        new("POST", "/api/v1/console/workflow-packages"),
        new("GET", "/api/v1/console/workflow-packages/{packageId}"),
        new("PUT", "/api/v1/console/workflow-packages/{packageId}"),
        new("GET", "/api/v1/console/workflow-packages/{packageId}/versions"),
        new("POST", "/api/v1/console/workflow-packages/{packageId}/versions"),
        new("GET", "/api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}"),
        new("POST", "/api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}/validate"),
        new("POST", "/api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}/dry-run"),
        new("POST", "/api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}/publish"),
        new("GET", "/api/v1/console/workflow-publications"),
        new("POST", "/api/v1/console/workflow-publications/{publicationId}/runs"),
        new("GET", "/api/v1/console/workflow-generation/providers"),
        new("POST", "/api/v1/console/workflow-packages/generate"),
    ];
}
