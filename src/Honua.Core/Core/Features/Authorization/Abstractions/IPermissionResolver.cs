// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

public interface IPermissionResolver
{
    PermissionDecision Authorize(
        EffectivePermissions effectivePermissions,
        string service,
        string? layer,
        AuthorizationOperation operation);

    Task<PermissionDecision> AuthorizeAsync(
        string userId,
        IReadOnlyList<string> roles,
        string service,
        string? layer,
        AuthorizationOperation operation,
        bool isAuthenticated,
        CancellationToken cancellationToken = default);
}
