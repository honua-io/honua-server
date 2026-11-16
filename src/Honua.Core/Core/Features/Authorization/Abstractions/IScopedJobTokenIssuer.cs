// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

public interface IScopedJobTokenIssuer
{
    Task<ScopedJobTokenIssuance> IssueAsync(
        ScopedJobTokenRequest request,
        CancellationToken cancellationToken);

    Task<ScopedJobTokenValidation?> ValidateAsync(
        string token,
        string jobId,
        CancellationToken cancellationToken);

    Task RevokeAsync(string token, CancellationToken cancellationToken);
}

public sealed record ScopedJobTokenRequest(
    string PrincipalId,
    string? TenantId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Grants,
    string JobId,
    IReadOnlyList<JobResourceScopeEntry> ResourceScope,
    DateTimeOffset ExpiresAt)
{
    public int Attempt { get; init; }
}

public sealed record ScopedJobTokenIssuance(string Token, DateTimeOffset ExpiresAt);

public sealed record ScopedJobTokenValidation(
    ClaimsPrincipal Principal,
    string JobId,
    DateTimeOffset ExpiresAt)
{
    public int Attempt { get; init; }
}
