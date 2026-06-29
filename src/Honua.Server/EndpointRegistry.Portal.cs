// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> PortalSharingEndpoints =>
    [
        // ArcGIS Portal Sharing token issuance (#1241).
        new("POST", "/sharing/rest/generateToken"),
        new("GET", "/sharing/rest/generateToken"),

        // ArcGIS Portal Sharing read surface (#1243).
        new("GET", "/sharing/rest/info"),
        new("GET", "/sharing/rest/portals/self"),
        new("GET", "/sharing/rest/community/self"),
        new("GET", "/sharing/rest/search"),
        new("GET", "/sharing/rest/content/items/{id}"),
        new("GET", "/sharing/rest/content/items/{id}/data"),

        // ArcGIS Portal OAuth2 named-user bridge (#1242).
        new("GET", "/sharing/rest/oauth2/authorize"),
        new("GET", "/sharing/rest/oauth2/callback"),
        new("POST", "/sharing/rest/oauth2/token"),
        new("GET", "/sharing/rest/oauth2/token"),

        // OAuth2 RFC 7662 token introspection (#1890).
        new("POST", "/sharing/rest/oauth2/introspect"),

        // OAuth2 RFC 7009 per-token revocation (#2155).
        new("POST", "/sharing/rest/oauth2/revoke"),

        // ArcGIS Portal community group + item sharing surface (#1868).
        new("POST", "/sharing/rest/community/createGroup"),
        new("GET", "/sharing/rest/community/groups/{groupId}"),
        new("POST", "/sharing/rest/community/groups/{groupId}/delete"),
        new("POST", "/sharing/rest/community/groups/{groupId}/addUsers"),
        new("POST", "/sharing/rest/community/groups/{groupId}/removeUsers"),
        new("POST", "/sharing/rest/content/items/{itemId}/share"),
        new("POST", "/sharing/rest/content/items/{itemId}/unshare"),
    ];
}
