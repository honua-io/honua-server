// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Core.Features.Authorization.Abstractions;

public interface IPortalTokenIssuer
{
    Task<PortalTokenIssuance> IssueAsync(
        PortalTokenIssueRequest request,
        CancellationToken cancellationToken);

    Task<PortalTokenValidation?> ValidateAsync(
        string token,
        PortalTokenBinding binding,
        CancellationToken cancellationToken);

    Task<PortalTokenExchange?> ExchangeAsync(
        PortalTokenExchangeRequest request,
        CancellationToken cancellationToken);

    Task<PortalTokenIntrospection?> IntrospectAsync(
        string tokenReference,
        CancellationToken cancellationToken);

    Task RevokeAsync(
        string tokenReference,
        CancellationToken cancellationToken);
}

public sealed record PortalTokenIntrospection(
    string PrincipalId,
    IReadOnlyList<string> Roles,
    string? TenantId,
    DateTimeOffset ExpiresAt);

public sealed record PortalTokenIssueRequest(
    string PrincipalId,
    string? DisplayName,
    string? TenantId,
    IReadOnlyList<string> Roles,
    PortalTokenClientType ClientType,
    string BindingValue,
    DateTimeOffset ExpiresAt,
    bool? RolesRequireClaimsMappingEntitlement = false,
    bool? TenantRequiresClaimsMappingEntitlement = false,
    IReadOnlyList<string>? RolesWithoutClaimsMapping = null,
    bool IsClientCredentials = false,
    PortalCredentialSource? Source = null,
    bool AcceptMissingReferer = false)
{
    // Roles flagged by RolesRequireClaimsMappingEntitlement that differ from the fallback only through the configured role claim types are revalidated against identity.oidc instead of identity.claims-mapping.
    public bool RolesDependOnlyOnRoleClaimSelection { get; init; }
}

public sealed record PortalTokenExchangeRequest(
    string Token,
    PortalTokenBinding PresentedBinding,
    PortalTokenClientType? ClientType,
    string? BindingValue,
    DateTimeOffset ExpiresAt);

public sealed record PortalTokenExchange(
    PortalTokenIssuance Issuance,
    string PrincipalId,
    string? TenantId,
    PortalTokenClientType ClientType);

public sealed record PortalTokenIssuance(string Token, DateTimeOffset ExpiresAt);

public sealed record PortalTokenValidation(ClaimsPrincipal Principal, DateTimeOffset ExpiresAt);

public sealed record PortalTokenBinding(string? Referer, string? ClientIp);

public enum PortalTokenClientType
{
    Referer = 0,

    Ip = 1,

    Bearer = 2,
}
