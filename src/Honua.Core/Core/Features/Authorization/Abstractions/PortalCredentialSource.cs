// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Abstractions;

public enum PortalCredentialSourceKind
{
    None = 0,

    ManagedApiKey = 1,

    AdminPassword = 2,

    FederatedToken = 3,
}

public sealed record PortalCredentialSource(
    PortalCredentialSourceKind Kind,
    string? Reference = null,
    string? Version = null,
    DateTimeOffset? ExpiresAt = null)
{
    public static PortalCredentialSource None { get; } = new(PortalCredentialSourceKind.None);
}
