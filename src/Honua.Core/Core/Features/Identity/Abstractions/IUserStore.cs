// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Identity.Domain;

namespace Honua.Core.Features.Identity.Abstractions;

public interface IUserStore
{
    Task<UserListResult> ListUsersAsync(UserListFilter filter, CancellationToken cancellationToken = default);

    Task<ManagedUser?> GetUserAsync(string userId, CancellationToken cancellationToken = default);

    Task<ManagedUser?> GetUserByPrincipalIdAsync(
        string principalId,
        string? issuer = null,
        CancellationToken cancellationToken = default);

    Task<ManagedUser?> UpdateUserRolesAsync(string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken = default);

    Task<bool> DeprovisionUserAsync(string userId, CancellationToken cancellationToken = default);
}

public sealed class UserListFilter
{
    public string? ProvisioningSource { get; init; }

    public string? Role { get; init; }

    public bool? IsActive { get; init; }

    public int Limit { get; init; } = 100;

    public int Offset { get; init; }
}

public sealed class UserListResult
{
    public required IReadOnlyList<ManagedUser> Users { get; init; }

    public required int TotalCount { get; init; }
}
