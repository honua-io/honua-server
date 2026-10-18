// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class DatasetImportExecutionAuthorityTests
{
    [UnitTheory]
    [InlineData("kind")]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("generation")]
    [InlineData("actor")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("subject")]
    [InlineData("tenant")]
    [InlineData("scope")]
    [InlineData("grant")]
    [InlineData("role")]
    [InlineData("permission")]
    public void RenewalCannotChangeOriginalExecutorAuthorityLineage(string change)
    {
        var original = Snapshot();
        var fresh = change switch
        {
            "kind" => original with { Kind = "api-key" },
            "id" => original with { Id = "another-profile" },
            "version" => original with { CredentialVersion = "rotated" },
            "generation" => original with { Generation = 2 },
            "actor" => original with { ActorId = "another-executor" },
            "issuer" => original with { Binding = ["other-issuer", "audience", "subject", "tenant"] },
            "audience" => original with { Binding = ["issuer", "other-audience", "subject", "tenant"] },
            "subject" => original with { Binding = ["issuer", "audience", "other-subject", "tenant"] },
            "tenant" => original with { Binding = ["issuer", "audience", "subject", "other-tenant"] },
            "scope" => original with { OAuthScopes = ["other-scope"] },
            "grant" => original with { RoleGrantFingerprints = ["changed-grant"] },
            "role" => original with { MembershipRoles = ["other-role"] },
            _ => original with { Permissions = ["admin:read"] },
        };
        DatasetImportExecutionAuthority.SameLineage(original, fresh).Should().BeFalse();
    }

    [UnitTest]
    public void RefreshedExpiryAndGrantOrderingPreserveExactAuthority()
    {
        var original = Snapshot();
        var fresh = original with
        {
            TokenExpiresAt = original.TokenExpiresAt.AddHours(1),
            Permissions = original.Permissions.Reverse().ToArray(),
        };
        DatasetImportExecutionAuthority.SameLineage(original, fresh).Should().BeTrue();
    }

    private static AgentCredentialSnapshot Snapshot() => new()
    {
        Kind = "oidc-delegated-agent", Id = "profile", Generation = 1, ActorId = "executor",
        Binding = ["issuer", "audience", "subject", "tenant"], OAuthScopes = ["agent:workspace"],
        RoleGrantFingerprints = ["grant-sha256"], MembershipRoles = ["managed-agent"],
        Permissions = ["agent:workspace", "admin:write"], TokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
    };
}
