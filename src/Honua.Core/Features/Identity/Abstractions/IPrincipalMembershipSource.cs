// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Identity.Abstractions;

// A source failure is an exception and must not be treated as an empty membership snapshot.
public interface IPrincipalMembershipSource
{
    Task<PrincipalMembership?> ResolveMembershipAsync(
        string principalId,
        CancellationToken cancellationToken = default);

    Task<PrincipalMembership?> ResolveMembershipAsync(
        string principalId,
        string? issuer,
        CancellationToken cancellationToken = default)
        => ResolveMembershipAsync(principalId, cancellationToken);
}

public sealed record PrincipalMembership(bool IsActive, IReadOnlyList<string> Roles);
