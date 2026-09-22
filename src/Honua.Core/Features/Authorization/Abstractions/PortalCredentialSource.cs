// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// The kind of credential a portal token was minted from (SEC-9). The issued token is a
/// derived credential, so the issuer records which credential produced it and re-checks
/// that credential on every restore instead of honouring a snapshot taken at issuance.
/// </summary>
public enum PortalCredentialSourceKind
{
    /// <summary>
    /// No separately revocable credential backs the token: the issuing flow already
    /// consumed its own single-use grant (for example the OAuth2 authorization-code and
    /// refresh-token bridges, whose grant records are removed as they are redeemed).
    /// There is nothing to re-read, so the token's own lifetime remains the bound.
    /// </summary>
    None = 0,

    /// <summary>
    /// A managed admin API key. <see cref="PortalCredentialSource.Reference"/> is the key
    /// identifier and <see cref="PortalCredentialSource.Version"/> a non-reversible digest
    /// of the key material, so a revoked, expired or rotated key stops its tokens.
    /// </summary>
    ManagedApiKey = 1,

    /// <summary>
    /// The configured admin password (or the bootstrap secret it resolves from).
    /// <see cref="PortalCredentialSource.Version"/> is a non-reversible rotation marker,
    /// so rotating the password stops the tokens minted from the previous value.
    /// </summary>
    AdminPassword = 2,

    /// <summary>
    /// A token bridged from the configured identity provider.
    /// <see cref="PortalCredentialSource.Reference"/> names the issuer and subject, and
    /// <see cref="PortalCredentialSource.ExpiresAt"/> carries the bridged token's own
    /// expiry so the portal token can never outlive it.
    /// </summary>
    FederatedToken = 3,
}

/// <summary>
/// Reference to the credential a portal token was minted from. Carries identifiers and
/// non-reversible version markers only — never credential material (SEC-9).
/// </summary>
/// <param name="Kind">Which credential family produced the token.</param>
/// <param name="Reference">
/// Stable identifier of the source credential (a managed key identifier, or
/// <c>issuer|subject</c> for a bridged token). <see langword="null"/> when the kind needs
/// no identifier.
/// </param>
/// <param name="Version">
/// Opaque, non-reversible marker of the credential's current value. The issuer compares it
/// against the live credential on every restore, so rotating the credential stops the tokens
/// minted from the previous value. <see langword="null"/> when the kind carries no version.
/// </param>
/// <param name="ExpiresAt">
/// Absolute expiry of the source credential, when it has one. The issued token's lifetime is
/// clamped to it so the derived credential never outlives the credential it came from.
/// </param>
public sealed record PortalCredentialSource(
    PortalCredentialSourceKind Kind,
    string? Reference = null,
    string? Version = null,
    DateTimeOffset? ExpiresAt = null)
{
    /// <summary>
    /// Source for an issuance flow that has no separately revocable backing credential.
    /// </summary>
    public static PortalCredentialSource None { get; } = new(PortalCredentialSourceKind.None);
}
