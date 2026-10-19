// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Xunit;

namespace Honua.Core.Tests.Features.Authorization;

public sealed class JobSecurityContextFingerprintTests
{
    [Fact]
    public void PolicyFingerprint_IsStableAcrossClaimOrderButSeparateFromCredentialRenewal()
    {
        var context = Context([new("department", "planning"), new("exp", "100")]);
        var reordered = context with { Claims = context.Claims.Reverse().ToArray() };
        Assert.Equal(JobSecurityContextFingerprint.Compute(context), JobSecurityContextFingerprint.Compute(reordered));
        var refreshed = context with { AgentCredential = new AgentCredentialSnapshot { Kind = "api-key", Id = "renewed", Permissions = ["agent:workspace"], TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1) } };
        Assert.Equal(JobSecurityContextFingerprint.Compute(context), JobSecurityContextFingerprint.Compute(refreshed));
        var changedPolicyExpiryClaim = context with { Claims = [new("department", "planning"), new("exp", "200")] };
        Assert.NotEqual(JobSecurityContextFingerprint.Compute(context), JobSecurityContextFingerprint.Compute(changedPolicyExpiryClaim));
    }

    [Theory]
    [InlineData("department")]
    [InlineData("tenant")]
    [InlineData("owner")]
    [InlineData("workspace")]
    [InlineData("role-alias")]
    public void PolicyFingerprint_BindsEveryOriginalPolicyIdentityInput(string change)
    {
        var original = Context([new("department", "planning")]);
        var changed = change switch
        {
            "department" => original with { Claims = [new("department", "administration")] },
            "tenant" => original with { TenantId = "other" },
            "owner" => original with { OwnerActorId = "other" },
            "workspace" => original with { WorkspaceOwnerId = "other" },
            _ => original with { AgentPolicyRoleClaimTypes = ["other-role-alias"] }
        };
        Assert.NotEqual(JobSecurityContextFingerprint.Compute(original), JobSecurityContextFingerprint.Compute(changed));
    }

    [Fact]
    public void PolicyFingerprint_BoundsInputsAndAvoidsScalarConcatenationAmbiguity()
    {
        Assert.NotEqual(JobSecurityContextFingerprint.Compute(Context([new("ab", "c")])), JobSecurityContextFingerprint.Compute(Context([new("a", "bc")])));
        Assert.Throws<UnauthorizedAccessException>(() => JobSecurityContextFingerprint.Compute(Context(Enumerable.Range(0, 257).Select(index => new JobSecurityClaim("claim", index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray())));
        Assert.Throws<UnauthorizedAccessException>(() => JobSecurityContextFingerprint.Compute(Context([new(new string('a', 513), "value")])));
        Assert.Throws<UnauthorizedAccessException>(() => JobSecurityContextFingerprint.Compute(Context([new("claim", new string('a', 16385))])));
    }

    [Fact]
    public void RenewalLineage_IgnoresOnlyIssuanceMetadataAndNeverReplacesOriginalPolicyInputs()
    {
        var original = Context([new("department", "planning"), new("exp", "100"), new("jti", "old")]);
        var fresh = Context([new("department", "planning"), new("exp", "200"), new("jti", "new"), new("iat", "101")]);
        Assert.Equal(JobSecurityContextFingerprint.ComputeRenewalLineage(original), JobSecurityContextFingerprint.ComputeRenewalLineage(fresh));
        Assert.NotEqual(JobSecurityContextFingerprint.Compute(original), JobSecurityContextFingerprint.Compute(fresh));
        Assert.NotEqual(JobSecurityContextFingerprint.ComputeRenewalLineage(original), JobSecurityContextFingerprint.ComputeRenewalLineage(
            fresh with { Claims = [new("department", "administration"), new("exp", "200")] }));
        Assert.Contains(new JobSecurityClaim("exp", "100"), original.Claims);
    }

    private static JobSecurityContext Context(IReadOnlyList<JobSecurityClaim> claims)
        => new("actor", "tenant", claims, ClaimTypes.Role)
        {
            OwnerActorId = "actor",
            WorkspaceOwnerId = "workspace",
            AgentPolicyRoleClaimTypes = [ClaimTypes.Role, "roles"]
        };
}
