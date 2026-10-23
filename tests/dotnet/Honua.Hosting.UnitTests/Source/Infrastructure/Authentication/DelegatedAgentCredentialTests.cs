// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.AspNetCore.DataProtection;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Microsoft.AspNetCore.Http;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using NSubstitute;
using Xunit;

namespace Honua.Hosting.UnitTests.Infrastructure.Authentication;

public sealed class DelegatedAgentCredentialTests
{
    [Theory]
    [InlineData("studio-compose", "exact", true)]
    [InlineData("studio-propose-publication", "exact", true)]
    [InlineData("studio-compose", "wildcard", false)]
    [InlineData("studio-propose-publication", "wildcard", false)]
    [InlineData("studio-compose", "own", false)]
    [InlineData("studio-propose-publication", "foreign", false)]
    public async Task ExactStudioItemPermission_RequiresPersistedExactGrantAndRejectsRevokedReplay(
        string action, string grantKind, bool expected)
    {
        using var fixture = new Fixture();
        var item = Guid.NewGuid().ToString("D");
        var permission = $"agent:{action}:{item}";
        fixture.Profile.Permissions = ["agent:workspace", permission];
        var layer = grantKind switch { "exact" => item, "wildcard" => "*", "own" => "own", _ => Guid.NewGuid().ToString("D") };
        fixture.Roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new EffectivePermissions { UserId = "subject", Roles = ["worker"], Permissions =
            [new PermissionGrant { Service = "agent", Layer = "*", Operation = "workspace" },
             new PermissionGrant { Service = "agent", Layer = layer, Operation = action }] });
        var principal = Token();
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        principal.HasClaim("permission", permission).Should().Be(expected);
        var snapshot = await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default);
        snapshot.Should().NotBeNull();
        (await AgentCredentialAuthority.RevalidateAsync(snapshot!, fixture.Services, default)).Should().NotBeNull();
        fixture.Roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new EffectivePermissions { UserId = "subject", Roles = ["worker"], Permissions =
            [new PermissionGrant { Service = "agent", Layer = "*", Operation = "workspace" }] });
        (await AgentCredentialAuthority.RevalidateAsync(snapshot!, fixture.Services, default)).Should().BeNull();
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("subject")]
    [InlineData("tenant")]
    [InlineData("unvalidated")]
    [InlineData("expired")]
    [InlineData("multi-audience")]
    [InlineData("duplicate-subject")]
    [InlineData("duplicate-tenant")]
    [InlineData("canonical-subject")]
    [InlineData("ambiguous-alternate-tenant")]
    [InlineData("ambiguous-name-identifier")]
    public async Task Bind_RejectsMismatchedOrUnvalidatedToken(string mismatch)
    {
        using var fixture = new Fixture();
        var principal = Token(mismatch);
        if (mismatch == "duplicate-subject") ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("sub", "subject"));
        if (mismatch == "duplicate-tenant") ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("tenant_id", "tenant"));
        if (mismatch == "ambiguous-alternate-tenant") ((ClaimsIdentity)principal.Identity!).AddClaims([new("tid", "tenant"), new("tid", "tenant")]);
        if (mismatch == "ambiguous-name-identifier") ((ClaimsIdentity)principal.Identity!).AddClaims([new(ClaimTypes.NameIdentifier, "subject"), new(ClaimTypes.NameIdentifier, "subject")]);
        if (mismatch == "canonical-subject") ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ClaimTypes.NameIdentifier, "different-subject"));
        if (mismatch == "multi-audience") ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("aud", "other"));
        if (mismatch != "unvalidated") AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        AgentCredentialAuthority.IsAgent(principal).Should().BeFalse();
    }

    [Fact]
    public async Task Replay_RejectsExpiredOriginatingToken()
    {
        using var fixture = new Fixture();
        var principal = Token();
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        var snapshot = await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default);
        (await AgentCredentialAuthority.RevalidateAsync(snapshot! with { TokenExpiresAt = DateTimeOffset.MinValue }, fixture.Services, default)).Should().BeNull();
    }

    [Fact]
    public async Task ForgedProfileAndPermissionClaims_DoNotCaptureAuthority()
    {
        using var fixture = new Fixture();
        var principal = Token();
        ((ClaimsIdentity)principal.Identity!).AddClaims([new Claim("permission", "agent:workspace"), new Claim("honua:delegated_agent_profile", "worker")]);
        (await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default)).Should().BeNull();
    }

    [Fact]
    public async Task BoundProfile_RevalidatesMembershipAndRoleGrantsBeforeReplay()
    {
        using var fixture = new Fixture();
        var principal = Token();
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        var snapshot = await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default);
        snapshot.Should().NotBeNull();
        snapshot!.Kind.Should().Be("oidc-delegated-agent");
        principal.FindFirst("api_key_id").Should().BeNull();
        (await AgentCredentialAuthority.RevalidateAsync(snapshot, fixture.Services, default)).Should().NotBeNull();
        fixture.Membership.ResolveMembershipAsync("subject", "https://issuer.example", Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(false, []));
        (await AgentCredentialAuthority.RevalidateAsync(snapshot, fixture.Services, default)).Should().BeNull();
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("generation")]
    [InlineData("expired")]
    [InlineData("subject")]
    [InlineData("role")]
    [InlineData("ceiling")]
    public async Task SealedCredential_RejectsChangedCurrentAuthority(string change)
    {
        using var fixture = new Fixture();
        var principal = Token();
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        var snapshot = await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default);
        snapshot.Should().NotBeNull();
        switch (change)
        {
            case "disabled": fixture.Profile.Enabled = false; break;
            case "generation": fixture.Profile.Generation++; break;
            case "expired": fixture.Profile.ExpiresAt = DateTimeOffset.MinValue; break;
            case "subject": fixture.Profile.Subject = "other"; break;
            case "ceiling": fixture.Profile.Permissions = []; break;
            case "role":
                fixture.Roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                    .Returns(new EffectivePermissions { UserId = "subject", Roles = [], Permissions = [] }); break;
        }
        (await AgentCredentialAuthority.RevalidateAsync(snapshot!, fixture.Services, default)).Should().BeNull();
    }

    [Fact]
    public async Task Ceiling_BoundsManagedAdminAtActualResourceAndOperatorEvaluators()
    {
        using var fixture = new Fixture();
        fixture.Profile.Permissions = ["agent:workspace", "admin:read", "read:parcels/0"];
        fixture.Membership.ResolveMembershipAsync("subject", "https://issuer.example", Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(true, ["admin"]));
        fixture.Roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new EffectivePermissions
            {
                UserId = "subject",
                Roles = ["admin"],
                Permissions = [
                new PermissionGrant { Service = "agent", Layer = "*", Operation = "workspace" },
                new PermissionGrant { Service = "admin", Layer = "*", Operation = "read" },
                new PermissionGrant { Service = "parcels", Layer = "0", Operation = "*" }]
            });
        var principal = Token();
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ClaimTypes.Role, "admin"));
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        principal.IsInRole("admin").Should().BeFalse();
        AdminApiKeyPermission.IsAuthorized(principal, "POST").Should().BeFalse();
        AccessPolicyHelpers.EvaluateScopedKeyAccess(principal, "parcels", "0", AuthorizationOperation.Query)!.Value.IsAllowed.Should().BeTrue();
        AccessPolicyHelpers.EvaluateScopedKeyAccess(principal, "parcels", "1", AuthorizationOperation.Query)!.Value.IsAllowed.Should().BeFalse();
        AccessPolicyHelpers.EvaluateScopedKeyAccess(principal, "parcels", "0", AuthorizationOperation.Update)!.Value.IsAllowed.Should().BeFalse();
        var evaluator = new OperatorAuthorizationEvaluator(fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new RbacOptions()), NullLogger<OperatorAuthorizationEvaluator>.Instance, fixture.Services);
        (await evaluator.EvaluateAsync(principal, new OperatorAuthorizationRequest { ResourceType = OperatorResourceType.Deployment, Operation = OperatorOperation.Read })).IsAllowed.Should().BeTrue();
        (await evaluator.EvaluateAsync(principal, new OperatorAuthorizationRequest { ResourceType = OperatorResourceType.Deployment, Operation = OperatorOperation.Publish })).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task ExistingStoredApiKey_CaptureAndRevocationRemainEnforced()
    {
        using var fixture = new Fixture();
        var store = fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var created = await store.CreateAsync("key", ["agent:workspace", "admin:read"], null, null, default);
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record);
        var snapshot = await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default);
        snapshot.Should().NotBeNull();
        await store.RevokeAsync(created.Record.Id, default);
        (await AgentCredentialAuthority.RevalidateAsync(snapshot!, fixture.Services, default)).Should().BeNull();
    }

    [Fact]
    public async Task StoredAgent_TypedCaptureCannotSilentlyDropAdditionalReadSecurityRoles()
    {
        using var fixture = new Fixture();
        var created = await fixture.Services.GetRequiredService<IAdminApiKeyStore>()
            .CreateAsync("worker", ["agent:workspace", "admin:read"], null, null, default);
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record);
        var snapshot = await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default);
        snapshot.Should().NotBeNull();
        var current = await AgentCredentialAuthority.RevalidateAsync(snapshot!, fixture.Services, default);
        current!.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().BeEquivalentTo(principal.FindAll(ClaimTypes.Role).Select(c => c.Value));
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ClaimTypes.Role, "restricted-worker"));
        (await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default)).Should().BeNull();
        (await AgentCredentialAuthority.IsCurrentAsync(principal, fixture.Services, default)).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelegatedReplay_PreservesValidatedOAuthNarrowing(bool canRead)
    {
        using var fixture = new Fixture();
        var principal = Token();
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.RemoveClaim(identity.FindFirst("scope")!);
        identity.AddClaim(new Claim("scope", "agent:workspace" + (canRead ? " " + OperatorScopeCatalog.Read : "")));
        AgentCredentialAuthority.MarkValidated(identity);
        // A post-validation mapping may not expand the validated token's ceiling.
        identity.AddClaim(new Claim("scp", OperatorScopeCatalog.Full));
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        var snapshot = await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default);
        snapshot.Should().NotBeNull();
        var replay = await AgentCredentialAuthority.RevalidateAsync(snapshot!, fixture.Services, default);
        replay.Should().NotBeNull();
        foreach (var candidate in new[] { principal, replay! })
        {
            var scopes = new OperatorScopeAuthorizer();
            scopes.Evaluate(candidate, OperatorResourceType.Deployment, OperatorOperation.Read).IsAllowed.Should().Be(canRead);
            var publish = scopes.Evaluate(candidate, OperatorResourceType.Deployment, OperatorOperation.Publish);
            publish.IsScopeGoverned.Should().BeTrue();
            publish.IsAllowed.Should().BeFalse();
        }
    }

    [Fact]
    public async Task StoredAgent_GenericDurableCaptureAndLegacyRestoreFailClosed()
    {
        using var fixture = new Fixture();
        var created = await fixture.Services.GetRequiredService<IAdminApiKeyStore>()
            .CreateAsync("worker", ["agent:workspace", "admin:read"], null, null, default);
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record);
        var capture = () => JobSecurityContextCapture.Capture(principal, new RbacOptions());
        capture.Should().Throw<BadHttpRequestException>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default)).Should().NotBeNull();
        var legacy = new JobSecurityContext("worker", null,
            [new("api_key_id", created.Record.Id.ToString()), new("permission", "agent:workspace"), new(ClaimTypes.Role, "admin")]);
        var restore = () => JobSecurityContextCapture.Restore(legacy);
        restore.Should().Throw<UnauthorizedAccessException>();
        var result = await JobSecurityContextCapture.RevalidateRoleMembershipAsync(legacy, null, new RbacOptions());
        result.HasRemovedRoles.Should().BeTrue();
        result.Status.Should().Be(JobSecurityContextMembershipStatus.ManagedUnresolved);
    }

    [Fact]
    public async Task QueryOnlyCanonicalRole_DoesNotGainMetadataExportOrDifferentLayer()
    {
        using var fixture = new Fixture();
        fixture.Profile.Permissions = ["agent:workspace", "read:*"];
        fixture.Roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new EffectivePermissions
            {
                UserId = "subject",
                Roles = ["worker"],
                Permissions = [
                new PermissionGrant { Service = "agent", Layer = "*", Operation = "workspace" },
                new PermissionGrant { Service = "parcels", Layer = "0", Operation = "read" }]
            });
        var principal = Token();
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        AgentCredentialAuthority.IsAgent(principal).Should().BeTrue();
        AccessPolicyHelpers.EvaluateScopedKeyAccess(principal, "parcels", "0", AuthorizationOperation.Query)!.Value.IsAllowed.Should().BeTrue();
        AccessPolicyHelpers.EvaluateScopedKeyAccess(principal, "parcels", "0", AuthorizationOperation.Metadata)!.Value.IsAllowed.Should().BeFalse();
        AccessPolicyHelpers.EvaluateScopedKeyAccess(principal, "parcels", "0", AuthorizationOperation.Export)!.Value.IsAllowed.Should().BeFalse();
        AccessPolicyHelpers.EvaluateScopedKeyAccess(principal, "parcels", "1", AuthorizationOperation.Query)!.Value.IsAllowed.Should().BeFalse();
        var snapshot = await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default);
        snapshot.Should().NotBeNull();
        fixture.Roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new EffectivePermissions
            {
                UserId = "subject",
                Roles = ["worker"],
                Permissions = [
                new PermissionGrant { Service = "agent", Layer = "*", Operation = "workspace" },
                new PermissionGrant { Service = "parcels", Layer = "0", Operation = "metadata" }]
            });
        (await AgentCredentialAuthority.RevalidateAsync(snapshot!, fixture.Services, default)).Should().BeNull();
    }

    [Fact]
    public async Task CurrentAuthority_RechecksOrdinaryStoredKeyWithoutAgentWorkspaceGrant()
    {
        using var fixture = new Fixture();
        var store = fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var created = await store.CreateAsync("reader", ["admin:read"], null, null, default);
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record);
        (await AgentCredentialAuthority.IsCurrentAsync(principal, fixture.Services, default)).Should().BeTrue();
        await store.RevokeAsync(created.Record.Id, default);
        (await AgentCredentialAuthority.IsCurrentAsync(principal, fixture.Services, default)).Should().BeFalse();
    }

    [Fact]
    public async Task DelegatedCredential_CannotApproveAnyProposal_OrdinaryHumanCan()
    {
        using var fixture = new Fixture();
        var principal = Token();
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        var requirement = new AdminApproveRequirement();
        var delegated = new AuthorizationHandlerContext([requirement], principal, "another-agent-proposal");
        await new AdminApproveAuthorizationHandler().HandleAsync(delegated);
        delegated.HasFailed.Should().BeTrue();
        var human = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "human"));
        var allowed = new AuthorizationHandlerContext([requirement], human, "another-agent-proposal");
        await new AdminApproveAuthorizationHandler().HandleAsync(allowed);
        allowed.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task DelegatedCredential_PreservesManagedRowAndFieldRestrictionsWithoutAdminRole()
    {
        using var fixture = new Fixture();
        var principal = Token();
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        principal.IsInRole("worker").Should().BeFalse();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal, RequestServices = fixture.Services } };
        var resource = new MetadataV2Resource { Metadata = new MetadataV2ObjectMetadata { Id = "parcels", Name = "parcels" } };
        var graph = Substitute.For<IMetadataV2GraphProvider>();
        var masks = Substitute.For<IFieldMaskPolicyStore>();
        masks.GetEffectivePoliciesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), "parcels", Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyList<string>>(0).Contains("worker")
                ? new FieldMaskPolicy[] { new() { Role = "worker", Service = "*", Layer = "parcels", Attribute = "secret" } }
                : []);
        var fields = new FieldMaskSource(accessor, masks, graph, Options.Create(new RbacOptions()), NullLogger<FieldMaskSource>.Instance);
        (await fields.ResolveAsync(resource)).Should().Equal("secret");
        var rows = Substitute.For<IRlsPolicyStore>();
        rows.GetEffectivePoliciesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), "parcels", Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyList<string>>(0).Contains("worker")
                ? new RlsPolicy[] { new() { Role = "worker", Service = "*", Layer = "parcels", Attribute = "owner_role", ClaimType = ClaimTypes.Role } }
                : []);
        var filters = Substitute.For<IFilterExpressionService>();
        filters.Normalize(Arg.Any<FilterExpression>(), resource).Returns(call => call.ArgAt<FilterExpression>(0));
        filters.Translate(Arg.Any<FilterExpression>(), resource)
            .Returns(call => FilterTranslationResult.Success(call.ArgAt<FilterExpression>(0), new SqlFragment("TRUE", [])));
        var rls = new RowLevelSecurityFilterSource(accessor, rows, graph, filters, Options.Create(new RbacOptions()), NullLogger<RowLevelSecurityFilterSource>.Instance);
        var expressions = await rls.ResolveExpressionsAsync(resource);
        expressions.Should().ContainSingle();
        // The restriction compares against the managed role, never the kind marker.
        var predicate = expressions[0].Should().BeOfType<BinaryExpression>().Subject;
        predicate.Right.Should().BeOfType<ValueList>().Subject.Values.Cast<Literal>().Select(value => value.Value).Should().Equal("worker");
        principal.IsInRole("worker").Should().BeFalse();
    }

    [Fact]
    public async Task StoredKeyRotation_InvalidatesRetainedPrincipalAndSealedSnapshotWithUnchangedIdAndGrants()
    {
        using var fixture = new Fixture();
        var store = fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var created = await store.CreateAsync("worker", ["agent:workspace"], null, null, default);
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record);
        var snapshot = await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default);
        snapshot.Should().NotBeNull();
        (await AgentCredentialAuthority.IsCurrentAsync(principal, fixture.Services, default)).Should().BeTrue();
        var rotated = await store.RotateAsync(created.Record.Id, default);
        rotated.Should().NotBeNull();
        rotated!.Record.Id.Should().Be(created.Record.Id);
        rotated.Record.Permissions.Should().Equal(created.Record.Permissions);
        (await AgentCredentialAuthority.IsCurrentAsync(principal, fixture.Services, default)).Should().BeFalse();
        (await AgentCredentialAuthority.RevalidateAsync(snapshot!, fixture.Services, default)).Should().BeNull();
        var validated = await store.ValidateAsync(rotated.Key, default);
        validated.Should().NotBeNull();
        var current = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(validated!.Record);
        (await AgentCredentialAuthority.IsCurrentAsync(current, fixture.Services, default)).Should().BeTrue();
        (await AgentCredentialAuthority.CaptureAsync(current, fixture.Services, default)).Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualBootstrapOrDevelopmentOperator_IsCurrentButCannotCaptureDeferredAgentAuthority(bool developmentBypass)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(new ApiKeyAuthenticationDependencies(Options.Create(new ApiKeyAuthenticationOptions
        {
            AdminPassword = "bootstrap-credential-consumer-test",
            EnvironmentName = "Test",
            IsTestMode = true,
            DevAuthBypass = developmentBypass ? "true" : "false",
            DevAuthBypassAcknowledged = "true"
        })));
        services.AddAuthentication(AuthenticationExtensions.ApiKeyScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthenticationExtensions.ApiKeyScheme, _ => { });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        if (!developmentBypass) context.Request.Headers["X-API-Key"] = "bootstrap-credential-consumer-test";
        var authenticated = await context.AuthenticateAsync(AuthenticationExtensions.ApiKeyScheme);
        authenticated.Succeeded.Should().BeTrue();
        var principal = authenticated.Principal!;
        (await AgentCredentialAuthority.IsCurrentAsync(principal, scope.ServiceProvider, default)).Should().BeTrue();
        AgentCredentialAuthority.IsAgent(principal).Should().BeFalse();
        var ordinarySnapshot = JobSecurityContextCapture.Capture(principal, new RbacOptions());
        JobSecurityContextCapture.Restore(ordinarySnapshot).Identity!.IsAuthenticated.Should().BeTrue();
        (await AgentCredentialAuthority.CaptureAsync(principal, scope.ServiceProvider, default)).Should().BeNull();
        var untrusted = new ClaimsPrincipal(new ClaimsIdentity(principal.Claims.Select(claim => new Claim(claim.Type, claim.Value)), AuthenticationExtensions.ApiKeyScheme));
        using var fixture = new Fixture();
        (await AgentCredentialAuthority.IsCurrentAsync(untrusted, fixture.Services, default)).Should().BeFalse();
    }

    [Fact]
    public async Task DelegatedCredential_CannotEnterLegacyDurableJobSnapshot()
    {
        using var fixture = new Fixture();
        var principal = Token();
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        var capture = () => JobSecurityContextCapture.Capture(principal, new RbacOptions());
        capture.Should().Throw<BadHttpRequestException>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await AgentCredentialAuthority.CaptureAsync(principal, fixture.Services, default)).Should().NotBeNull();
    }

    [Fact]
    public async Task FrozenAuthorityFingerprint_TracksManagedRoleSetEvenWhenGrantsStayIdentical()
    {
        using var fixture = new Fixture();
        var original = Token();
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)original.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(original, fixture.Services);
        var fingerprint = CanonicalSecurityActor.ResolveAuthorityFingerprint(original);
        fixture.Membership.ResolveMembershipAsync("subject", "https://issuer.example", Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(true, ["worker", "restricted"]));
        var changed = Token();
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)changed.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(changed, fixture.Services);
        changed.FindAll("permission").Select(claim => claim.Value).Should().Equal(original.FindAll("permission").Select(claim => claim.Value));
        CanonicalSecurityActor.ResolveAuthorityFingerprint(changed).Should().NotBe(fingerprint);
        fixture.Membership.ResolveMembershipAsync("subject", "https://issuer.example", Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(true, ["restricted", "worker"]));
        var reordered = Token();
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)reordered.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(reordered, fixture.Services);
        CanonicalSecurityActor.ResolveAuthorityFingerprint(reordered).Should().Be(CanonicalSecurityActor.ResolveAuthorityFingerprint(changed));
    }

    [Fact]
    public async Task DelegatedQuarantine_RemovesCaseVariantConfiguredRoleClaims()
    {
        using var fixture = new Fixture("groups");
        var principal = Token();
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("GROUPS", "admin"));
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        AgentCredentialAuthority.IsAgent(principal).Should().BeTrue();
        principal.FindAll("groups").Should().BeEmpty();
        RbacRoleClaims.Enumerate(principal, new RbacOptions { RoleClaimType = "groups" }, true)
            .Should().Equal(AgentCredentialAuthority.DelegatedRole);
    }

    [Fact]
    public async Task DelegatedAdminWrite_CannotMintOrChangeIndependentCredentialAuthority()
    {
        using var fixture = new Fixture();
        fixture.Profile.Permissions = ["agent:workspace", "admin:write"];
        fixture.Roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new EffectivePermissions
            {
                UserId = "subject",
                Roles = ["worker"],
                Permissions = [
                new() { Service = "agent", Layer = "*", Operation = "workspace" },
                new() { Service = "admin", Layer = "*", Operation = "write" }]
            });
        var principal = Token();
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        AdminApiKeyPermission.IsAuthorized(principal, "POST").Should().BeTrue();
        AdminApiKeyPermission.CanManageApiKeys(principal).Should().BeFalse();
        using var services = new ServiceCollection().AddLogging()
            .AddAuthorization(CredentialAuthorityManagementAuthorization.Configure).BuildServiceProvider();
        var authorization = services.GetRequiredService<IAuthorizationService>();
        (await authorization.AuthorizeAsync(principal, null, CredentialAuthorityManagementAuthorization.PolicyName)).Succeeded.Should().BeFalse();
        var human = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "human"));
        (await authorization.AuthorizeAsync(human, null, CredentialAuthorityManagementAuthorization.PolicyName)).Succeeded.Should().BeTrue();
        AdminApiKeyPermission.CanManageApiKeys(human).Should().BeTrue();
    }

    [Theory]
    [InlineData("admin:write", false, false)]
    [InlineData("admin:write", true, false)]
    [InlineData("admin", true, false)]
    [InlineData("admin", false, true)]
    public async Task StoredCredential_AuthorityManagementRequiresFullNonAgentAuthority(string grant, bool workspaceAgent, bool allowed)
    {
        var keys = new InMemoryAdminApiKeyStore();
        var grants = workspaceAgent ? new[] { grant, "agent:workspace" } : new[] { grant };
        var key = await keys.CreateAsync("credential-management", grants, null, null, default);
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(key.Record);
        using var services = new ServiceCollection().AddLogging()
            .AddAuthorization(CredentialAuthorityManagementAuthorization.Configure).BuildServiceProvider();
        var authorization = services.GetRequiredService<IAuthorizationService>();
        (await authorization.AuthorizeAsync(principal, null, CredentialAuthorityManagementAuthorization.PolicyName))
            .Succeeded.Should().Be(allowed);
        AdminApiKeyPermission.CanManageApiKeys(principal).Should().Be(allowed);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("admin:write")]
    [InlineData("admin:approve")]
    public async Task StoredWorkspaceAgent_CannotDecideProposalsEvenWithAdministrativeGrants(string grant)
    {
        var keys = new InMemoryAdminApiKeyStore();
        var workload = await keys.CreateAsync("agent", [grant, "agent:workspace"], null, null, default);
        var operatorKey = await keys.CreateAsync("operator", [grant], null, null, default);
        var requirement = new AdminApproveRequirement();
        var denied = new AuthorizationHandlerContext([requirement],
            ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(workload.Record), null);
        await new AdminApproveAuthorizationHandler().HandleAsync(denied);
        denied.HasFailed.Should().BeTrue();
        var allowed = new AuthorizationHandlerContext([requirement],
            ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(operatorKey.Record), null);
        await new AdminApproveAuthorizationHandler().HandleAsync(allowed);
        allowed.HasSucceeded.Should().BeTrue();
    }

    private static ClaimsPrincipal Token(string? mismatch = null) => new(new ClaimsIdentity([
        new Claim("scope", "agent:workspace"),
        new Claim("exp", (mismatch == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddMinutes(10)).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new Claim("iss", mismatch == "issuer" ? "https://evil.example" : "https://issuer.example"),
        new Claim("sub", mismatch == "subject" ? "other" : "subject"),
        new Claim("aud", mismatch == "audience" ? "other" : "honua"),
        new Claim("tenant_id", mismatch == "tenant" ? "other" : "tenant")], OidcAuthenticationExtensions.JwtBearerScheme));

    [Theory]
    [InlineData("unchanged")]
    [InlineData("membership")]
    [InlineData("role-grant")]
    [InlineData("profile-generation")]
    [InlineData("profile-disabled")]
    [InlineData("profile-expired")]
    [InlineData("ceiling")]
    public async Task VerifiedLeaseLineage_SeparatesTokenExpiryFromLiveAuthority(string change)
    {
        var clock = new LeaseClock();
        using var fixture = new Fixture(clock: clock);
        var principal = Token();
        AgentCredentialAuthority.MarkValidated((ClaimsIdentity)principal.Identity!);
        await AgentCredentialAuthority.BindOidcAsync(principal, fixture.Services);
        var tenant = Substitute.For<Honua.Core.Features.MultiTenancy.Abstractions.ITenantContext>();
        tenant.TenantId.Returns("tenant");
        var context = await AgentJobSecurityBridge.CaptureAsync(principal, new RbacOptions(), fixture.Services, tenant, default);
        var job = new ExecutionJobRecord
        {
            OperationId = "lease-job",
            Status = ExecutionJobStatus.Queued,
            CreatedAt = clock.GetUtcNow(),
            UpdatedAt = clock.GetUtcNow(),
            Spec = new() { TargetKind = BatchComputeTargetKind.LocalProcess, Backend = "local", Kind = ExecutionJobKind.Geoprocessing, WorkloadName = "analysis" },
            Audit = new() { RequestedBy = context.OwnerActorId, TenantId = context.TenantId, SubmitterSecurityContext = context }
        };
        var protector = new JobExecutionLeaseProtector(new EphemeralDataProtectionProvider(), clock);
        var lease = protector.ProtectValidatedBinding(job, new()
        {
            Kind = job.Spec.Kind,
            Resources = [],
            ProviderBinding = "closed-constant-plan",
            Operations = [new() { ResourceType = OperatorResourceType.Process, ResourceId = "constant", Operation = OperatorOperation.Execute }]
        }, new() { IsAllowed = true, DecisionId = "benign-canonical-policy", MaximumExpiresAt = clock.GetUtcNow().AddMinutes(30) }, null, clock.GetUtcNow().AddMinutes(30));
        var accepted = job with { Audit = job.Audit with { SubmitterSecurityContext = context with { ExecutionLease = lease } } };
        clock.UtcNow = clock.GetUtcNow().AddMinutes(11);
        // Ordinary authentication/replay remains expired. Only a verified job-bound lease
        // can take the separate lineage path, without changing the original token snapshot.
        (await AgentCredentialAuthority.RevalidateAsync(context.AgentCredential!, fixture.Services, default)).Should().BeNull();
        var proof = protector.UnprotectAndMatchAuthoritativeJob(accepted);
        switch (change)
        {
            case "membership":
                fixture.Membership.ResolveMembershipAsync("subject", "https://issuer.example", Arg.Any<CancellationToken>())
                    .Returns(new PrincipalMembership(false, []));
                break;
            case "role-grant":
                fixture.Roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                    .Returns(new EffectivePermissions { UserId = "subject", Roles = ["worker"], Permissions = [] });
                break;
            case "profile-generation": fixture.Profile.Generation++; break;
            case "profile-disabled": fixture.Profile.Enabled = false; break;
            case "profile-expired": fixture.Profile.ExpiresAt = clock.GetUtcNow(); break;
            case "ceiling": fixture.Profile.Permissions = []; break;
        }
        var current = await AgentCredentialAuthority.RevalidateLeaseLineageAsync(protector, proof, accepted, fixture.Services, default);
        (current is not null).Should().Be(change == "unchanged");
        var unverifiedCopy = proof with { };
        var forged = () => AgentCredentialAuthority.RevalidateLeaseLineageAsync(protector, unverifiedCopy, accepted, fixture.Services, default);
        await forged.Should().ThrowAsync<UnauthorizedAccessException>();
        context.AgentCredential!.TokenExpiresAt.Should().BeBefore(clock.GetUtcNow());
        // Lineage is not yet a usable execution credential: scope fencing is a separate adapter.
        if (current is not null)
        {
            (await AgentCredentialAuthority.IsCurrentAsync(current, fixture.Services, default)).Should().BeFalse();
        }
    }

    private sealed class LeaseClock : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class Fixture : IDisposable
    {
        internal OidcDelegatedAgentProfile Profile { get; } = new()
        {
            Id = "worker",
            Generation = 1,
            Enabled = true,
            Issuer = "https://issuer.example",
            Audience = "honua",
            Subject = "subject",
            Tenant = "tenant",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Permissions = ["agent:workspace"]
        };
        internal IPrincipalMembershipSource Membership { get; } = Substitute.For<IPrincipalMembershipSource>();
        internal IRoleStore Roles { get; } = Substitute.For<IRoleStore>();
        internal ServiceProvider Services { get; }
        internal Fixture(string roleClaimType = "roles", TimeProvider? clock = null)
        {
            var options = Substitute.For<IOptionsMonitor<OidcAuthenticationOptions>>();
            options.CurrentValue.Returns(new OidcAuthenticationOptions { Enabled = true, DelegatedAgents = [Profile], DelegatedAgentAudiences = [new() { Issuer = "https://issuer.example", Audience = "honua" }] });
            Membership.ResolveMembershipAsync("subject", "https://issuer.example", Arg.Any<CancellationToken>())
                .Returns(new PrincipalMembership(true, ["worker"]));
            Roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(new EffectivePermissions { UserId = "subject", Roles = ["worker"], Permissions = [new PermissionGrant { Service = "agent", Layer = "*", Operation = "workspace" }] });
            Services = new ServiceCollection().AddSingleton(clock ?? TimeProvider.System).AddSingleton(options).AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions { RoleClaimType = roleClaimType })).AddSingleton(Membership).AddSingleton(Roles).AddSingleton<IAdminApiKeyStore>(new InMemoryAdminApiKeyStore()).BuildServiceProvider();
        }
        public void Dispose() => Services.Dispose();
    }
}
