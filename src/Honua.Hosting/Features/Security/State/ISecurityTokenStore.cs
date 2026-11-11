// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Security;

internal enum SecurityTokenKind
{
    AdminSession,
    AdminPendingSignIn,
    PortalBrokerSession,
    PortalAuthorizationCode,
    PortalRefreshToken,
    PortalPrincipalRevocation,
    PortalToken,
    JobToken,
    TemporaryFile
}

internal readonly record struct SecurityTokenKey(SecurityTokenKind Kind, string Value);

internal sealed record StoredSecurityToken(byte[] Payload, DateTimeOffset ExpiresAt);

// The store keeps a digest of the value, never the value. A token past its expiry is not found,
// whether or not it has been swept. Expiry is decided by the store's clock.
internal interface ISecurityTokenStore
{
    Task StoreAsync(SecurityTokenKey key, ReadOnlyMemory<byte> payload, TimeSpan lifetime, CancellationToken cancellationToken);

    Task<StoredSecurityToken?> FindAsync(SecurityTokenKey key, CancellationToken cancellationToken);

    // Returns the token to exactly one caller and deletes it.
    Task<StoredSecurityToken?> ConsumeAsync(SecurityTokenKey key, CancellationToken cancellationToken);

    // Puts back a token that was consumed for an exchange that then failed, with the expiry it had.
    Task RestoreAsync(SecurityTokenKey key, StoredSecurityToken token, CancellationToken cancellationToken);

    Task RevokeAsync(SecurityTokenKey key, CancellationToken cancellationToken);
}
