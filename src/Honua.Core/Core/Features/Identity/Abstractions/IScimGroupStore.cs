// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Identity.Domain;

namespace Honua.Core.Features.Identity.Abstractions;

public interface IScimGroupStore
{
    Task<ScimGroup?> CreateGroupAsync(ScimGroupProvisioning provisioning, CancellationToken cancellationToken = default);

    Task<ScimGroup?> GetGroupAsync(string groupId, CancellationToken cancellationToken = default);

    Task<ScimGroupPage> ListGroupsAsync(ScimGroupQuery query, CancellationToken cancellationToken = default);

    Task<ScimGroup?> ReplaceGroupAsync(string groupId, ScimGroupProvisioning provisioning, CancellationToken cancellationToken = default);

    Task<ScimGroup?> UpdateMembersAsync(string groupId, ScimGroupMemberChange change, CancellationToken cancellationToken = default);

    Task<bool> DeleteGroupAsync(string groupId, CancellationToken cancellationToken = default);
}

public sealed class ScimGroupProvisioning
{
    public required string DisplayName { get; init; }

    public IReadOnlyList<string> MemberUserIds { get; init; } = [];
}

public sealed class ScimGroupMemberChange
{
    public IReadOnlyList<string> Add { get; init; } = [];

    public IReadOnlyList<string> Remove { get; init; } = [];
}

public sealed class ScimGroupQuery
{
    public string? DisplayNameEquals { get; init; }

    public int StartIndex { get; init; } = 1;

    public int Count { get; init; } = 100;
}

public sealed class ScimGroupPage
{
    public required IReadOnlyList<ScimGroup> Groups { get; init; }

    public required int TotalCount { get; init; }
}
