// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

public sealed record JobSecurityClaim(string Type, string Value);

public sealed record JobSecurityContext(
    string? PrincipalId,
    string? TenantId,
    IReadOnlyList<JobSecurityClaim> Claims,
    string? RoleClaimType = null)
{
    public string? OwnerActorId { get; init; }

    public string? WorkspaceOwnerId { get; init; }
}

public static class JobSecurityContextClaimTypes
{
    public const string MembershipPrincipalId = "honua:membership-principal-id";

    public const string ManagedMembershipMarker = "honua:managed-membership";

    public const string ManagedMembershipMarkerValue = "true";
}
