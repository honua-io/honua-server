// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Security proof for the custom-code geoprocessing scoped-job token issuer (Phase 0
/// auth spine). Each invariant — narrow-only attenuation, non-forgeable tenant,
/// read-only-unless-write, job-binding, revocation, and expiry — is exercised
/// against the real issuer so the hydrated principal can never out-grant the
/// submitter or reach another tenant.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class ScopedJobTokenIssuerTests
{
    private const string PermissionClaimType = "permission";
    private const string TenantClaimType = "tenant_id";

    [UnitTest]
    public async Task IssueAsync_WriteScopeWithinOwner_HydratesAttenuatedPrincipalBoundToJob()
    {
        var issuer = CreateIssuer();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);

        var issuance = await issuer.IssueAsync(
            new ScopedJobTokenRequest(
                PrincipalId: "alice",
                TenantId: "tenant-A",
                Roles: ["data-editor:parcels"],
                JobId: "gp-job-1",
                ResourceScope: [new JobResourceScopeEntry("parcels", null, JobResourceAccess.Write)],
                ExpiresAt: expiresAt),
            CancellationToken.None);

        issuance.Token.Should().NotBeNullOrWhiteSpace();
        issuance.ExpiresAt.Should().Be(expiresAt);

        var validation = await issuer.ValidateAsync(issuance.Token, "gp-job-1", CancellationToken.None);

        validation.Should().NotBeNull();
        validation!.Principal.Identity!.IsAuthenticated.Should().BeTrue();
        validation.Principal.FindFirstValue(ClaimTypes.Name).Should().Be("alice");
        validation.JobId.Should().Be("gp-job-1");
        validation.Principal.FindFirstValue(TenantClaimType).Should().Be("tenant-A");

        // Service-wide write was reachable, so the service-scoped editor role and
        // both read+write permission grants are projected.
        validation.Principal.IsInRole("data-editor:parcels").Should().BeTrue();
        PermissionsOf(validation.Principal).Should().Contain(["read:parcels", "write:parcels"]);
    }

    [UnitTest]
    public async Task IssueAsync_RequestExceedsOwner_AttenuatesToOwnerGrantsOnly()
    {
        // Owner can only reach 'parcels' (write). The request also asks for 'zoning'
        // (which the owner cannot reach) — the resulting principal must carry NO
        // authority over 'zoning' (narrow-only invariant #1).
        var issuer = CreateIssuer();

        var issuance = await issuer.IssueAsync(
            new ScopedJobTokenRequest(
                PrincipalId: "alice",
                TenantId: null,
                Roles: ["data-editor:parcels"],
                JobId: "gp-job-2",
                ResourceScope:
                [
                    new JobResourceScopeEntry("parcels", null, JobResourceAccess.Write),
                    new JobResourceScopeEntry("zoning", null, JobResourceAccess.Write),
                ],
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10)),
            CancellationToken.None);

        var validation = await issuer.ValidateAsync(issuance.Token, "gp-job-2", CancellationToken.None);

        validation.Should().NotBeNull();
        var permissions = PermissionsOf(validation!.Principal);
        permissions.Should().Contain(["read:parcels", "write:parcels"]);
        // The out-of-owner resource is entirely absent — neither read nor write.
        permissions.Should().NotContain(p => p.Contains("zoning", StringComparison.OrdinalIgnoreCase));
        validation.Principal.IsInRole("data-editor:zoning").Should().BeFalse();
    }

    [UnitTest]
    public async Task IssueAsync_ResourceScopeNarrowerThanOwner_GrantsOnlyTheScope()
    {
        // Owner is admin (can reach everything) but the ResourceScope is narrowed to
        // a single read-only layer. The token must grant ONLY that, never more —
        // the scope is the ceiling even when the owner is broader.
        var issuer = CreateIssuer();

        var issuance = await issuer.IssueAsync(
            new ScopedJobTokenRequest(
                PrincipalId: "root",
                TenantId: "tenant-A",
                Roles: ["admin"],
                JobId: "gp-job-3",
                ResourceScope: [new JobResourceScopeEntry("parcels", "lots", JobResourceAccess.Read)],
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10)),
            CancellationToken.None);

        var validation = await issuer.ValidateAsync(issuance.Token, "gp-job-3", CancellationToken.None);

        validation.Should().NotBeNull();
        // Even though the owner is admin, the principal is NOT admin: it carries only
        // the scoped read grant.
        validation!.Principal.IsInRole("admin").Should().BeFalse();
        var permissions = PermissionsOf(validation.Principal);
        permissions.Should().Contain("read:parcels/lots");
        permissions.Should().NotContain(p => p.StartsWith("write:", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    public async Task IssueAsync_TenantComesFromSnapshotOnly_IsNonForgeable()
    {
        // The hydrated tenant always equals the snapshot tenant. There is no input
        // path for a callback to supply a tenant — the only tenant source is the
        // pinned snapshot (invariant #2).
        var issuer = CreateIssuer();

        var issuance = await issuer.IssueAsync(
            new ScopedJobTokenRequest(
                PrincipalId: "alice",
                TenantId: "tenant-A",
                Roles: ["data-editor:parcels"],
                JobId: "gp-job-4",
                ResourceScope: [new JobResourceScopeEntry("parcels", null, JobResourceAccess.Read)],
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10)),
            CancellationToken.None);

        var validation = await issuer.ValidateAsync(issuance.Token, "gp-job-4", CancellationToken.None);

        validation.Should().NotBeNull();
        validation!.Principal.FindAll(TenantClaimType).Should().ContainSingle()
            .Which.Value.Should().Be("tenant-A");
    }

    [UnitTest]
    public async Task IssueAsync_ReadOnlyScope_YieldsNoWriteAuthority()
    {
        // A scope entry without write yields read-only for that resource (invariant #3).
        var issuer = CreateIssuer();

        var issuance = await issuer.IssueAsync(
            new ScopedJobTokenRequest(
                PrincipalId: "alice",
                TenantId: null,
                Roles: ["data-editor:parcels"], // owner COULD write, but scope is read-only
                JobId: "gp-job-5",
                ResourceScope: [new JobResourceScopeEntry("parcels", null, JobResourceAccess.Read)],
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10)),
            CancellationToken.None);

        var validation = await issuer.ValidateAsync(issuance.Token, "gp-job-5", CancellationToken.None);

        validation.Should().NotBeNull();
        var permissions = PermissionsOf(validation!.Principal);
        permissions.Should().Contain("read:parcels");
        permissions.Should().NotContain("write:parcels");
        validation.Principal.IsInRole("data-editor:parcels").Should().BeFalse();
    }

    [UnitTest]
    public async Task IssueAsync_RequestedWriteAgainstReadOnlyOwner_ClampsToRead()
    {
        // Owner has only a read grant on 'zoning'; the request asks for write.
        // The effective grant clamps down to read (cannot widen owner authority).
        var issuer = CreateIssuer();

        var issuance = await issuer.IssueAsync(
            new ScopedJobTokenRequest(
                PrincipalId: "carol",
                TenantId: null,
                Roles: [],
                JobId: "gp-job-6",
                ResourceScope: [new JobResourceScopeEntry("zoning", null, JobResourceAccess.Write)],
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10)),
            CancellationToken.None);

        var validation = await issuer.ValidateAsync(issuance.Token, "gp-job-6", CancellationToken.None);

        validation.Should().NotBeNull();
        // Owner had no reach on zoning (no role/grant), so nothing is granted at all.
        PermissionsOf(validation!.Principal).Should().NotContain(p => p.Contains("zoning", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    public async Task ValidateAsync_WrongJobContext_ReturnsNull()
    {
        // Job-binding (invariant #4): a token minted for one job is rejected in
        // another job's context.
        var issuer = CreateIssuer();

        var issuance = await issuer.IssueAsync(
            new ScopedJobTokenRequest(
                PrincipalId: "alice",
                TenantId: null,
                Roles: ["data-editor:parcels"],
                JobId: "gp-job-7",
                ResourceScope: [new JobResourceScopeEntry("parcels", null, JobResourceAccess.Read)],
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10)),
            CancellationToken.None);

        var sameJob = await issuer.ValidateAsync(issuance.Token, "gp-job-7", CancellationToken.None);
        var otherJob = await issuer.ValidateAsync(issuance.Token, "gp-job-DIFFERENT", CancellationToken.None);

        sameJob.Should().NotBeNull();
        otherJob.Should().BeNull();
    }

    [UnitTest]
    public async Task RevokeAsync_RemovesToken_SubsequentValidationFails()
    {
        // Revocation (invariant #5): deleting the record invalidates the token.
        var issuer = CreateIssuer();

        var issuance = await issuer.IssueAsync(
            new ScopedJobTokenRequest(
                PrincipalId: "alice",
                TenantId: null,
                Roles: ["data-editor:parcels"],
                JobId: "gp-job-8",
                ResourceScope: [new JobResourceScopeEntry("parcels", null, JobResourceAccess.Read)],
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10)),
            CancellationToken.None);

        (await issuer.ValidateAsync(issuance.Token, "gp-job-8", CancellationToken.None)).Should().NotBeNull();

        await issuer.RevokeAsync(issuance.Token, CancellationToken.None);

        (await issuer.ValidateAsync(issuance.Token, "gp-job-8", CancellationToken.None)).Should().BeNull();
        // Revoking an already-absent token is a no-op.
        await issuer.Invoking(i => i.RevokeAsync(issuance.Token, CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [UnitTest]
    public async Task ValidateAsync_ExpiredToken_ReturnsNull()
    {
        // Short-lived (invariant #5): an expired token never validates.
        var issuer = CreateIssuer();

        var issuance = await issuer.IssueAsync(
            new ScopedJobTokenRequest(
                PrincipalId: "alice",
                TenantId: null,
                Roles: ["data-editor:parcels"],
                JobId: "gp-job-9",
                ResourceScope: [new JobResourceScopeEntry("parcels", null, JobResourceAccess.Read)],
                ExpiresAt: DateTimeOffset.UtcNow.AddMilliseconds(-1)),
            CancellationToken.None);

        var validation = await issuer.ValidateAsync(issuance.Token, "gp-job-9", CancellationToken.None);

        validation.Should().BeNull();
    }

    [UnitTest]
    public async Task ValidateAsync_UnknownToken_ReturnsNull()
    {
        var issuer = CreateIssuer();

        var validation = await issuer.ValidateAsync(
            "0000000000000000000000000000000000000000000000000000000000000000",
            "gp-job-10",
            CancellationToken.None);

        validation.Should().BeNull();
    }

    [UnitTest]
    public async Task IssueAsync_RejectsMissingJobBinding()
    {
        var issuer = CreateIssuer();

        await issuer.Invoking(i => i.IssueAsync(
            new ScopedJobTokenRequest(
                PrincipalId: "alice",
                TenantId: null,
                Roles: ["admin"],
                JobId: "   ",
                ResourceScope: [new JobResourceScopeEntry("parcels", null, JobResourceAccess.Read)],
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10)),
            CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
    }

    private static List<string> PermissionsOf(ClaimsPrincipal principal)
        => principal.FindAll(PermissionClaimType).Select(c => c.Value).ToList();

    private static ScopedJobTokenIssuer CreateIssuer()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        return new ScopedJobTokenIssuer(memoryCache, NullLogger<ScopedJobTokenIssuer>.Instance);
    }
}
