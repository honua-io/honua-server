// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Abstractions;

// Thrown by an IRoleStore when another role already holds the name. It stays an InvalidOperationException
// so callers that treated every duplicate as one keep working, while endpoints can answer 409.
public sealed class RoleNameConflictException : InvalidOperationException
{
    public string RoleName { get; }

    public RoleNameConflictException(string roleName)
        : base($"Role with name '{roleName}' already exists.")
        => RoleName = roleName;

    public RoleNameConflictException(string roleName, Exception innerException)
        : base($"Role with name '{roleName}' already exists.", innerException)
        => RoleName = roleName;
}
