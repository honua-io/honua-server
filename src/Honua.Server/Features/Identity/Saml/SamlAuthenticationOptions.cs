// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Identity.Saml;

public sealed class SamlAuthenticationOptions
{
    public const string SectionName = "Saml";

    // When SAML is disabled the endpoints reject requests so the surface cannot be used before an identity provider is configured.
    public bool Enabled { get; set; }

    public string? EntityId { get; set; }

    public string? IdpEntityId { get; set; }

    public string? AssertionConsumerServiceUrl { get; set; }

    public string? SingleLogoutServiceUrl { get; set; }

    public string? IdpSingleLogoutServiceUrl { get; set; }

    // Signatures are verified against this certificate, and unsigned assertions are rejected.
    public string? IdpSigningCertificate { get; set; }

    public string RoleAttribute { get; set; } = "Role";

    public string EmailAttribute { get; set; } = "email";

    public string DisplayNameAttribute { get; set; } = "displayName";

    public string? DefaultRole { get; set; }

    public int AllowedClockSkewSeconds { get; set; } = 120;

    public int SessionLifetimeSeconds { get; set; } = 3600;
}
