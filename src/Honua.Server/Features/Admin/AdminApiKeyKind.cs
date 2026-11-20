// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Features.Admin;

// An API key is a standard key or an agent key. An agent key always carries the agent grant, so
// every agent gate sees it as an agent. A key issued to an agent caller, or from a proposal an
// agent made, is an agent key: an agent cannot obtain a credential that drops the marker.
internal static class AdminApiKeyKind
{
    public const string Standard = "standard";
    public const string Agent = "agent";

    private const string FullAdminGrant = "admin:*";

    public static string Of(IReadOnlyList<string> permissions)
        => HasAgentGrant(permissions) ? Agent : Standard;

    // The caller is an agent, or the loopback hop runs a proposal an agent made. A server-minted
    // approved-operation credential without its attestation cannot say whose proposal it runs, so
    // it counts as an agent.
    public static AdminApiKeyCaller CallerOf(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lineage = context.Features.Get<AttestedOperationLineage>();
        return AgentPrincipal.IsAgent(context.User)
            || lineage?.AgentRequested == true
            || (lineage is null && IsApprovedOperationCredential(context.User))
            ? AdminApiKeyCaller.Agent
            : AdminApiKeyCaller.Person;
    }

    // Rotation returns the new secret, so an agent may rotate only an agent key.
    public static bool MayRotate(AdminApiKeyCaller caller, IReadOnlyList<string> permissions)
        => caller == AdminApiKeyCaller.Person || HasAgentGrant(permissions);

    // The grants to store, with the agent grant added when the key is an agent key, or the refusal.
    public static AdminApiKeyKindResult Resolve(AdminApiKeyKindRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refusal(request) is { } refusal)
        {
            return new AdminApiKeyKindResult { Refusal = refusal };
        }

        var permissions = request.Permissions;
        var isAgent = request.Caller == AdminApiKeyCaller.Agent
            || string.Equals(request.Kind, Agent, StringComparison.Ordinal);
        if (!isAgent || HasAgentGrant(permissions))
        {
            return new AdminApiKeyKindResult { Permissions = permissions };
        }

        // An empty grant list means full admin; the agent grant alone would narrow it.
        return new AdminApiKeyKindResult
        {
            Permissions = permissions.Count == 0
                ? [AgentPrincipal.AgentGrant, FullAdminGrant]
                : [AgentPrincipal.AgentGrant, .. permissions],
        };
    }

    private static string? Refusal(AdminApiKeyKindRequest request)
    {
        var kind = request.Kind;
        if (kind is null || string.Equals(kind, Agent, StringComparison.Ordinal))
        {
            return null;
        }

        if (!string.Equals(kind, Standard, StringComparison.Ordinal))
        {
            return "Validation failed: kind must be 'standard' or 'agent'";
        }

        if (HasAgentGrant(request.Permissions))
        {
            return "Validation failed: a standard key cannot carry the 'agent' grant; create it with kind 'agent'";
        }

        return request.Caller == AdminApiKeyCaller.Agent
            ? "Validation failed: a key issued to an agent, or from a proposal an agent made, is an agent key; omit kind or use 'agent'"
            : null;
    }

    private static bool IsApprovedOperationCredential(ClaimsPrincipal principal)
        => principal.FindAll(AdminApiKeyPermission.PermissionClaimType)
            .Any(static claim => AdminApiKeyPermission.IsApprovedOperationGrant(claim.Value));

    private static bool HasAgentGrant(IReadOnlyList<string> permissions)
        => permissions.Any(static permission => string.Equals(
            permission?.Trim(), AgentPrincipal.AgentGrant, StringComparison.OrdinalIgnoreCase));
}

internal enum AdminApiKeyCaller
{
    Person,
    Agent,
}

internal sealed record AdminApiKeyKindRequest
{
    public string? Kind { get; init; }

    public IReadOnlyList<string> Permissions { get; init; } = [];

    public AdminApiKeyCaller Caller { get; init; }
}

internal sealed record AdminApiKeyKindResult
{
    public IReadOnlyList<string> Permissions { get; init; } = [];

    public string? Refusal { get; init; }
}
