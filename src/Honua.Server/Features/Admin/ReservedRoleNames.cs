// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Operations.Policy;
using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.Admin;

// The server gives these role names to principals itself (built-in roles and the roles stamped on
// API keys at authentication), so a stored role under one of them would change the authority of
// every principal that carries it. Comparison ignores case because role-store lookups do.
internal static class ReservedRoleNames
{
    private static readonly FrozenSet<string> Names = new[]
    {
        "admin",
        "viewer",
        AdminApiKeyPermission.ApprovedOperationRole,
        AdminApiKeyPermission.ScopedAdminRole,
        LayerScopedWriteKey.Role,
        LayerScopedWriteKey.ScopedKeyRole,
        AgentApprovalRule.AgentRole,
        "orchestrator",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string? name) => name is not null && Names.Contains(name.Trim());

    public static string Refusal(string name) => $"Role name '{name.Trim()}' is reserved by the server.";
}
