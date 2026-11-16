// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

public enum JobResourceAccess
{
    Read = 0,

    Write = 1,
}

public sealed record JobResourceScopeEntry(
    string ServiceId,
    string? LayerId,
    JobResourceAccess Access);

public sealed record CustomCodeOwnerScope(
    string PrincipalId,
    string? TenantId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Grants,
    IReadOnlyList<JobResourceScopeEntry> DeclaredScope);
