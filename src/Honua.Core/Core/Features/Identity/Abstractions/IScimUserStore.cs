// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Identity.Domain;

namespace Honua.Core.Features.Identity.Abstractions;

public interface IScimUserStore
{
    Task<ManagedUser?> CreateUserAsync(ScimUserProvisioning provisioning, CancellationToken cancellationToken = default);

    Task<ManagedUser?> GetUserAsync(string userId, CancellationToken cancellationToken = default);

    Task<ManagedUser?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default);

    Task<ScimUserPage> ListUsersAsync(ScimUserQuery query, CancellationToken cancellationToken = default);

    Task<ManagedUser?> ReplaceUserAsync(string userId, ScimUserProvisioning provisioning, CancellationToken cancellationToken = default);

    Task<ManagedUser?> SetActiveAsync(string userId, bool active, CancellationToken cancellationToken = default);

    Task<bool> DeleteUserAsync(string userId, CancellationToken cancellationToken = default);
}

public sealed class ScimUserProvisioning
{
    public required string UserName { get; init; }

    public string? ExternalId { get; init; }

    public string? ExternalIssuer { get; init; }

    public string? DisplayName { get; init; }

    public string? Email { get; init; }

    public bool Active { get; init; } = true;

    public IReadOnlyList<string> Roles { get; init; } = [];
}

public sealed class ScimUserQuery
{
    public string? UserNameEquals { get; init; }

    public int StartIndex { get; init; } = 1;

    public int Count { get; init; } = 100;
}

public sealed class ScimUserPage
{
    public required IReadOnlyList<ManagedUser> Users { get; init; }

    public required int TotalCount { get; init; }
}
