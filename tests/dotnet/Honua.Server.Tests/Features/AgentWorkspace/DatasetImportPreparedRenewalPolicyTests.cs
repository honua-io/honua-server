// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.AgentWorkspace;

// Exact context-comparison kernel only. Production JWT renewal/admission
// requires the separate canonical HTTP journey qualification.
public sealed class DatasetImportPreparedRenewalPolicyTests
{
    [UnitTheory]
    [InlineData("transport", true)]
    [InlineData("tenant", false)]
    [InlineData("policy-claim", false)]
    [InlineData("role", false)]
    [InlineData("unprepared-expiry", false)]
    [InlineData("profile-generation", false)]
    [InlineData("scope", false)]
    public void RefreshedTransportRetainsOriginalPolicyInputsAndExactPreparedCredential(string change, bool allowed)
    {
        var credential = new AgentCredentialSnapshot
        {
            Kind = "oidc-delegated-agent",
            Id = "profile",
            Generation = 1,
            Binding = ["issuer", "audience", "subject", "tenant"],
            Permissions = ["agent:workspace", "admin:write"],
            OAuthScopes = ["honua.admin.write"],
            MembershipRoles = ["editor"],
            TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        var original = new JobSecurityContext("actor", "tenant", [new("exp", "original"), new("iat", "original"),
            new("jti", "original"), new("department", "roads"), new("roles", "editor")], "roles")
        { OwnerActorId = "actor", AgentCredential = credential, AgentPolicyRoleClaimTypes = ["roles"] };
        var originalFingerprint = JobSecurityContextFingerprint.Compute(original);
        var fresh = original with
        {
            Claims = [new("exp", "refreshed"), new("iat", "refreshed"), new("jti", "refreshed"),
            new("department", "roads"), new("roles", "editor")]
        };
        fresh = change switch
        {
            "tenant" => fresh with { TenantId = "other" },
            "policy-claim" => fresh with { Claims = [new("department", "all"), new("roles", "editor")] },
            "role" => fresh with { Claims = [new("department", "roads"), new("roles", "admin")] },
            "unprepared-expiry" => fresh with { AgentCredential = credential with { TokenExpiresAt = credential.TokenExpiresAt.AddMinutes(1) } },
            "profile-generation" => fresh with { AgentCredential = credential with { Generation = 2 } },
            "scope" => fresh with { AgentCredential = credential with { OAuthScopes = ["honua.admin.read"] } },
            _ => fresh
        };
        Assert.Equal(allowed, FileImportExecutionLeaseScopeProvider.MatchesPreparedContext(fresh, original));
        Assert.Equal(originalFingerprint, JobSecurityContextFingerprint.Compute(original));
        Assert.Equal("original", original.Claims.Single(claim => claim.Type == "exp").Value);
    }
}
