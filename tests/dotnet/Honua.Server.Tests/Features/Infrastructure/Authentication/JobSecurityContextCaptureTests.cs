// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
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
