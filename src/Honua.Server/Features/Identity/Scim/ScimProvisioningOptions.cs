// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Identity.Scim;

/// <summary>
/// Configuration for the SCIM 2.0 provisioning surface (#510). An identity provider (Okta,
/// Microsoft Entra, etc.) authenticates to the SCIM endpoints with a long-lived bearer token
/// it is configured with out-of-band; the token is compared in constant time.
/// </summary>
public sealed class ScimProvisioningOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Scim";

    /// <summary>
    /// The bearer token the identity provider must present in the <c>Authorization</c> header.
    /// When null/empty the SCIM endpoints reject all requests (provisioning is disabled by
    /// default — there is no implicit anonymous access).
    /// </summary>
    public string? BearerToken { get; set; }
}
