// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

public sealed class PortalCredentialVerifierOptions
{
    public const string SectionName = "Authentication:PortalCredentialVerifier";

    public bool UseOidc { get; set; }

    // When the OIDC verifier is enabled, falling back to the admin verifier stays on by default so API-key access keeps working during migration.
    public bool FallBackToAdminVerifier { get; set; } = true;
}
