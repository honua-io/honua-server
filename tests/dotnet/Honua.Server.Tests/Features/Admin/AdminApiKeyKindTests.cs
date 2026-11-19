// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Admin;

// An agent key always carries the agent grant, and an agent caller cannot obtain a key without it.
public sealed class AdminApiKeyKindTests
{
    [UnitTheory]
    [InlineData(null, new[] { "admin:read" }, new[] { "admin:read" })]
    [InlineData("standard", new[] { "admin:read" }, new[] { "admin:read" })]
    [InlineData("agent", new[] { "admin:read" }, new[] { "agent", "admin:read" })]
    [InlineData("agent", new[] { "agent", "read:*" }, new[] { "agent", "read:*" })]
    [InlineData(null, new[] { "agent", "read:*" }, new[] { "agent", "read:*" })]
    [InlineData("agent", new string[0], new[] { "agent", "admin:*" })]
    public void Person_GetsTheRequestedKind(string? kind, string[] requested, string[] expected)
    {
        var result = AdminApiKeyKind.Resolve(Request(kind, requested, AdminApiKeyCaller.Person));

        result.Refusal.Should().BeNull();
        result.Permissions.Should().Equal(expected);
    }

    [UnitTheory]
    [InlineData(null, new[] { "admin:write" }, new[] { "agent", "admin:write" })]
    [InlineData("agent", new[] { "admin:write" }, new[] { "agent", "admin:write" })]
    [InlineData(null, new string[0], new[] { "agent", "admin:*" })]
    public void Agent_AlwaysGetsAnAgentKey(string? kind, string[] requested, string[] expected)
    {
        var result = AdminApiKeyKind.Resolve(Request(kind, requested, AdminApiKeyCaller.Agent));

        result.Refusal.Should().BeNull();
        result.Permissions.Should().Equal(expected);
        AdminApiKeyKind.Of(result.Permissions).Should().Be(AdminApiKeyKind.Agent);
    }

    [UnitTheory]
    [InlineData("standard", new[] { "agent", "admin:read" }, "cannot carry")]
    [InlineData("robot", new[] { "admin:read" }, "must be 'standard' or 'agent'")]
    [InlineData("AGENT", new[] { "admin:read" }, "must be 'standard' or 'agent'")]
    public void ConflictingKind_IsRefused(string kind, string[] requested, string reason)
    {
        AdminApiKeyKind.Resolve(Request(kind, requested, AdminApiKeyCaller.Person)).Refusal.Should().Contain(reason);
    }

    [UnitTest]
    public void StandardKindForAnAgent_IsRefused()
    {
        var result = AdminApiKeyKind.Resolve(Request("standard", ["admin:write"], AdminApiKeyCaller.Agent));

        result.Refusal.Should().Contain("agent key");
    }

    [UnitTest]
    public void Agent_MayRotateAnAgentKey()
    {
        AdminApiKeyKind.MayRotate(AdminApiKeyCaller.Agent, ["agent", "admin:write"]).Should().BeTrue();
    }

    [UnitTest]
    public void Agent_MayNotRotateAStandardKey_ButAPersonMay()
    {
        AdminApiKeyKind.MayRotate(AdminApiKeyCaller.Agent, ["admin:*"]).Should().BeFalse();
        AdminApiKeyKind.MayRotate(AdminApiKeyCaller.Person, ["admin:*"]).Should().BeTrue();
    }

    [UnitTest]
    public void UnattestedApprovedOperationCredential_IsAnAgentCaller()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AdminApiKeyPermission.PermissionClaimType, "admin:operation:POST:/api/v1/admin/api-keys")], "ApiKey")),
        };

        AdminApiKeyKind.CallerOf(context).Should().Be(AdminApiKeyCaller.Agent);
    }

    [UnitTest]
    public void AgentGrantPrincipal_IsAnAgentCaller()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AdminApiKeyPermission.PermissionClaimType, AgentPrincipal.AgentGrant)], "ApiKey")),
        };

        AdminApiKeyKind.CallerOf(context).Should().Be(AdminApiKeyCaller.Agent);
    }

    [UnitTest]
    public void AttestedHumanHop_IsAPersonCaller()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "ApiKey")),
        };
        context.Features.Set(AttestedOperationLineage.Instance);

        AdminApiKeyKind.CallerOf(context).Should().Be(AdminApiKeyCaller.Person);
    }

    private static AdminApiKeyKindRequest Request(string? kind, IReadOnlyList<string> permissions, AdminApiKeyCaller caller)
        => new() { Kind = kind, Permissions = permissions, Caller = caller };
}
