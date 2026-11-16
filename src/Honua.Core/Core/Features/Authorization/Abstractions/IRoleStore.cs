// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

public interface IRoleStore
{
    Task<IReadOnlyList<RoleDefinition>> ListRolesAsync(CancellationToken cancellationToken = default);

    Task<RoleDefinition?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken = default);

    Task<RoleDefinition> CreateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default);

    Task<RoleDefinition?> UpdateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default);

    Task<bool> DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PermissionGrant>> GetPermissionsAsync(Guid roleId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PermissionGrant>> SetPermissionsAsync(Guid roleId, IReadOnlyList<PermissionGrant> permissions, CancellationToken cancellationToken = default);

    Task<EffectivePermissions> GetEffectivePermissionsAsync(string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken = default);
}
