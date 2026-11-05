// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Identity.Scim;

public sealed class ScimProvisioningOptions
{
    public const string SectionName = "Scim";

    // An empty token rejects every request, so provisioning has no anonymous access.
    public string? BearerToken { get; set; }

    // Identical subjects from different issuers cannot share membership when this issuer is configured.
    public string? OidcIssuer { get; set; }
}
