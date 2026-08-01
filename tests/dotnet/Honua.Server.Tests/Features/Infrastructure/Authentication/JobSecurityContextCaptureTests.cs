// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Authentication;

/// <summary>
/// Contract tests for the durable submitter-identity snapshot that carries row-level security
/// and field masking onto the background job worker (honua-server#3068). The snapshot is the
/// whole mechanism: if capture or restore loses a claim, a restricted caller silently gets
/// unrestricted job output.
/// </summary>
public sealed class JobSecurityContextCaptureTests
{
    [UnitTest]
    public void Capture_ThenRestore_PreservesRoleAndPolicyClaims()
    {
        var principal = BuildPrincipal(
            ("name", "analyst@example.test"),
            (ClaimTypes.Role, "restricted-analyst"),
            ("roles", "field-crew"),
            ("category", "test"),
            ("category", "pilot"));

        var restored = JobSecurityContextCapture.Restore(
            JobSecurityContextCapture.Capture(principal, new RbacOptions()));

        // Roles drive field masking; both the standard and configured role claim types survive.
        restored.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().Contain("restricted-analyst");
        restored.FindAll("roles").Select(c => c.Value).Should().Contain("field-crew");

        // RLS predicates are built as `attribute IN (claim values)` over an arbitrary claim type,
        // so EVERY value of that claim must round-trip or the predicate silently narrows/widens.
        restored.FindAll("category").Select(c => c.Value).Should().BeEquivalentTo(["test", "pilot"]);
    }

    [UnitTest]
    public void Capture_ExcludesCredentialClaims()
    {
        // A durable job record is replicated to Redis and, for out-of-process backends, onto a
        // worker payload. Policy identity belongs there; bearer credentials do not.
        var principal = BuildPrincipal(
            (ClaimTypes.Role, "analyst"),
            ("access_token", "super-secret"),
            ("refresh_token", "also-secret"));

        var captured = JobSecurityContextCapture.Capture(principal, new RbacOptions());

        captured.Claims.Should().NotContain(claim => claim.Type == "access_token");
        captured.Claims.Should().NotContain(claim => claim.Type == "refresh_token");
        captured.Claims.Should().Contain(claim => claim.Value == "analyst");
    }

    [UnitTest]
    public void Capture_PrioritizesRoleClaims_SoMaskingIsNeverTruncatedAway()
    {
        // Field masking keys purely on roles, and a dropped role means a mask that should have
        // applied does not — the one truncation outcome that is NOT fail-secure. Roles are
        // therefore captured first, ahead of the (large) non-role claim set.
        var claims = new List<(string Type, string Value)>();
        for (var i = 0; i < 400; i++)
        {
            claims.Add(($"filler{i}", i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        claims.Add((ClaimTypes.Role, "restricted-analyst"));

        var captured = JobSecurityContextCapture.Capture(BuildPrincipal([.. claims]), new RbacOptions());

        captured.Claims.Should().Contain(claim => claim.Value == "restricted-analyst");
    }

    [UnitTest]
    public void Capture_MoreRolesThanTheClaimBudget_KeepsEveryRole()
    {
        // Capturing roles FIRST is not sufficient on its own: with more roles than the budget,
        // the role pass itself hit the ceiling and dropped roles. A dropped role that owns an
        // RLS or field-mask policy WIDENS access — RowLevelSecurityFilterSource returns no
        // filter when no policy matches and FieldMaskSource returns an empty mask — so roles
        // are exempt from the budget entirely (honua-server#3068 review).
        var claims = new List<(string Type, string Value)>();
        for (var i = 0; i < 400; i++)
        {
            claims.Add((ClaimTypes.Role, $"role-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        }

        var captured = JobSecurityContextCapture.Capture(BuildPrincipal([.. claims]), new RbacOptions());

        captured.Claims.Count(claim => claim.Type == ClaimTypes.Role).Should().Be(400);
        captured.Claims.Should().Contain(claim => claim.Value == "role-399",
            "the last role must survive as surely as the first");
    }

    [UnitTest]
    public void Capture_ConfiguredRoleClaimType_IsAlsoExemptFromTheBudget()
    {
        // The exemption follows RbacOptions.EffectiveRoleClaimType, not just ClaimTypes.Role,
        // or a deployment using a custom role claim would still truncate policy identity.
        var options = new RbacOptions { RoleClaimType = "honua_roles" };
        var claims = new List<(string Type, string Value)>();
        for (var i = 0; i < 300; i++)
        {
            claims.Add(("honua_roles", $"custom-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        }

        var captured = JobSecurityContextCapture.Capture(BuildPrincipal([.. claims]), options);

        captured.Claims.Count(claim => claim.Type == "honua_roles").Should().Be(300);
    }

    [UnitTest]
    public void Capture_ScopeGovernanceClaims_SurviveTheBudget()
    {
        // The mirror image of the role exemption. OperatorScopeCatalog.IsScopeGoverned decides
        // whether OAuth scope narrowing applies by looking for exactly these claims, so a
        // principal presenting enough other claims to push them past the budget would restore
        // as UNGOVERNED — and an approval resume or a triggered firing would then apply the
        // captured roles to operations the original token never delegated. Dropping them
        // removes a restriction, which the budget must never do (honua-server#3046 review).
        var claims = new List<(string Type, string Value)>();
        for (var i = 0; i < 400; i++)
        {
            claims.Add(($"filler{i}", i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        // Declared LAST, so enumeration order alone would have truncated them away.
        claims.Add((OperatorScopeCatalog.ScopeGovernedClaimType, "true"));
        claims.Add((OperatorScopeCatalog.ScopeClaimType, "honua.mcp.read"));
        claims.Add((OperatorScopeCatalog.ScpClaimType, "honua.mcp.execute"));

        var captured = JobSecurityContextCapture.Capture(BuildPrincipal([.. claims]), new RbacOptions());

        captured.Claims.Should().Contain(claim => claim.Type == OperatorScopeCatalog.ScopeGovernedClaimType);
        captured.Claims.Should().Contain(claim => claim.Type == OperatorScopeCatalog.ScopeClaimType);
        captured.Claims.Should().Contain(claim => claim.Type == OperatorScopeCatalog.ScpClaimType);

        // The restored identity must still read as scope-governed, which is the property that
        // actually matters — the claims are only the means.
        OperatorScopeCatalog.IsScopeGoverned(JobSecurityContextCapture.Restore(captured))
            .Should().BeTrue();
    }

    [UnitTest]
    public void Restore_SnapshotWhoseTenantClaimWasTruncated_KeepsTheTenantScope()
    {
        // Tenant is captured in its own field as well as among the claims. The deferred lanes
        // authorize against the restored identity, and the layer gate scopes on its tenant, so
        // a restored identity that lost its tenant would widen rather than narrow.
        var context = new JobSecurityContext(
            "subject-123", TenantId: "tenant-a", [new JobSecurityClaim(ClaimTypes.Role, "analyst")]);

        var restored = JobSecurityContextCapture.Restore(context);

        restored.FindFirst("tenant_id")?.Value.Should().Be("tenant-a");
    }

    [UnitTest]
    public void Capture_NonRoleClaims_AreStillBounded()
    {
        // The bloat guard the budget exists for must survive the exemption: descriptive claims
        // are still capped so a pathological token cannot inflate every durable job record.
        var claims = new List<(string Type, string Value)>();
        for (var i = 0; i < 2000; i++)
        {
            claims.Add(($"filler{i}", i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var captured = JobSecurityContextCapture.Capture(BuildPrincipal([.. claims]), new RbacOptions());

        captured.Claims.Should().HaveCountLessThan(2000);
    }

    [UnitTest]
    public void Capture_PrincipalWithNoClaims_ProducesEmptySnapshotRatherThanNull()
    {
        // An empty snapshot is strictly more restrictive than a missing one: it resolves no
        // policies for the caller, whereas a missing snapshot is what the read seam refuses on.
        var captured = JobSecurityContextCapture.Capture(new ClaimsPrincipal(new ClaimsIdentity()), new RbacOptions());

        captured.Should().NotBeNull();
        captured.Claims.Should().BeEmpty();
    }

    private static ClaimsPrincipal BuildPrincipal(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(
            claims.Select(claim => new Claim(claim.Type, claim.Value)),
            "Test",
            ClaimTypes.Name,
            ClaimTypes.Role));
}
