// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> IdentityProvisioningEndpoints =>
    [
        // SCIM 2.0 provisioning endpoints (#510)
        new("GET", "/scim/v2/Users"),
        new("POST", "/scim/v2/Users"),
        new("GET", "/scim/v2/Users/{id}"),
        new("PUT", "/scim/v2/Users/{id}"),
        new("PATCH", "/scim/v2/Users/{id}"),
        new("DELETE", "/scim/v2/Users/{id}"),
        new("GET", "/scim/v2/Groups"),
        new("POST", "/scim/v2/Groups"),
        new("GET", "/scim/v2/Groups/{id}"),
        new("PUT", "/scim/v2/Groups/{id}"),
        new("PATCH", "/scim/v2/Groups/{id}"),
        new("DELETE", "/scim/v2/Groups/{id}"),

        // SCIM 2.0 discovery documents (#2154, RFC 7643 §5-7)
        new("GET", "/scim/v2/ServiceProviderConfig"),
        new("GET", "/scim/v2/ResourceTypes"),
        new("GET", "/scim/v2/ResourceTypes/{id}"),
        new("GET", "/scim/v2/Schemas"),
        new("GET", "/scim/v2/Schemas/{id}"),

        // SAML 2.0 Service Provider endpoints (#508; SLO #2154)
        new("GET", "/saml/metadata"),
        new("POST", "/saml/acs"),
        new("POST", "/saml/slo"),
    ];
}
