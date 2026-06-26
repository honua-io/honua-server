// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// Issues and verifies opaque ArcGIS-compatible portal tokens that bind a request
/// principal to a referer or client IP for the lifetime of the token.
/// </summary>
/// <remarks>
/// Tokens are short-lived bearer credentials backed by the distributed cache; the
/// issued value carries no claims of its own, so revocation is achieved by removing
/// the cache entry. Validation hydrates a <see cref="ClaimsPrincipal"/> compatible
/// with the existing access-policy pipeline.
/// </remarks>
public interface IPortalTokenIssuer
{
    /// <summary>
    /// Issues a new portal token bound to the supplied credential context.
    /// </summary>
    /// <param name="request">Issuance request describing the authenticated principal and binding.</param>
    /// <param name="cancellationToken">Token used to abort the issuance.</param>
    /// <returns>The issued token alongside its expiry instant.</returns>
    Task<PortalTokenIssuance> IssueAsync(
        PortalTokenIssueRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Validates an opaque token and returns the bound principal when the token is
    /// active and its binding matches the current request, otherwise returns
    /// <see langword="null"/>.
    /// </summary>
    /// <param name="token">Opaque token value supplied by the client.</param>
    /// <param name="binding">Binding observed on the current request.</param>
    /// <param name="cancellationToken">Token used to abort the lookup.</param>
    /// <returns>The validated session, or <see langword="null"/> when the token is unusable.</returns>
    Task<PortalTokenValidation?> ValidateAsync(
        string token,
        PortalTokenBinding binding,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves an active token reference for RFC 7662 introspection (#1890),
    /// <em>without</em> the referer/IP binding check that
    /// <see cref="ValidateAsync"/> applies. Introspection is a trusted,
    /// authorization-protected server-to-server query made by a party that is not
    /// the bound client, so the binding cannot match; the active/expired/revoked
    /// state is determined solely by the cache entry's presence and lifetime.
    /// Returns the introspection metadata when the token is active, otherwise
    /// <see langword="null"/> (the caller maps that to <c>{ "active": false }</c>).
    /// </summary>
    /// <param name="tokenReference">
    /// The opaque token value (for the opaque format) or the JWT <c>jti</c> (for the
    /// JWT format) that keys the cache entry.
    /// </param>
    /// <param name="cancellationToken">Token used to abort the lookup.</param>
    /// <returns>The active token's introspection metadata, or <see langword="null"/>.</returns>
    Task<PortalTokenIntrospection?> IntrospectAsync(
        string tokenReference,
        CancellationToken cancellationToken);

    /// <summary>
    /// Revokes a single issued access token by removing its backing cache entry so
    /// every subsequent <see cref="ValidateAsync"/> and <see cref="IntrospectAsync"/>
    /// call for that token fails immediately (RFC 7009). Because the issued value
    /// carries no claims of its own, dropping the cache entry is the single,
    /// authoritative revocation point for both the opaque and JWT token formats
    /// (the JWT's <c>jti</c> keys the same entry). The operation is idempotent:
    /// revoking an unknown, already-revoked, or expired reference is a no-op.
    /// </summary>
    /// <param name="tokenReference">
    /// The opaque token value (for the opaque format) or the JWT <c>jti</c> (for the
    /// JWT format) that keys the cache entry.
    /// </param>
    /// <param name="cancellationToken">Token used to abort the removal.</param>
    /// <returns>A task that completes when the revocation has been applied.</returns>
    Task RevokeAsync(
        string tokenReference,
        CancellationToken cancellationToken);
}

/// <summary>
/// Active-token metadata returned for RFC 7662 introspection (#1890).
/// </summary>
/// <param name="PrincipalId">Subject (<c>sub</c>/<c>username</c>) the token was issued to.</param>
/// <param name="Roles">Roles carried by the token, surfaced as the introspection <c>scope</c>.</param>
/// <param name="TenantId">Tenant the token is scoped to, when present.</param>
/// <param name="ExpiresAt">Absolute expiry instant (<c>exp</c>).</param>
public sealed record PortalTokenIntrospection(
    string PrincipalId,
    IReadOnlyList<string> Roles,
    string? TenantId,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Inputs required to issue an opaque portal token.
/// </summary>
/// <param name="PrincipalId">Stable identifier (e.g. username) of the authenticated principal.</param>
/// <param name="DisplayName">Optional display name surfaced through the hydrated principal.</param>
/// <param name="TenantId">Tenant the token is scoped to, or <see langword="null"/> for the tenant-less default.</param>
/// <param name="Roles">Roles granted to the principal for the lifetime of the token.</param>
/// <param name="ClientType">Whether the binding is a referer or an IP address.</param>
/// <param name="BindingValue">Bound referer URL or client IP address.</param>
/// <param name="ExpiresAt">Absolute expiry instant for the token.</param>
public sealed record PortalTokenIssueRequest(
    string PrincipalId,
    string? DisplayName,
    string? TenantId,
    IReadOnlyList<string> Roles,
    PortalTokenClientType ClientType,
    string BindingValue,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Token issuance result.
/// </summary>
/// <param name="Token">Opaque token to return to the caller.</param>
/// <param name="ExpiresAt">Absolute expiry of the token.</param>
public sealed record PortalTokenIssuance(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Verified portal-token session and its hydrated principal.
/// </summary>
/// <param name="Principal">Hydrated <see cref="ClaimsPrincipal"/> for the bound user.</param>
/// <param name="ExpiresAt">Absolute expiry instant of the token.</param>
public sealed record PortalTokenValidation(ClaimsPrincipal Principal, DateTimeOffset ExpiresAt);

/// <summary>
/// Observed binding for the current request.
/// </summary>
/// <param name="Referer">Referer header value when present.</param>
/// <param name="ClientIp">Client IP address when known.</param>
public sealed record PortalTokenBinding(string? Referer, string? ClientIp);

/// <summary>
/// Type of binding requested by the caller when issuing a token.
/// </summary>
public enum PortalTokenClientType
{
    /// <summary>
    /// Bind to a referer URL. The token is honored only when the request supplies a
    /// matching <c>Referer</c> header.
    /// </summary>
    Referer = 0,

    /// <summary>
    /// Bind to a client IP address. The token is honored only from that address.
    /// </summary>
    Ip = 1,
}
