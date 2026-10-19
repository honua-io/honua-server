// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.DataProtection;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Xunit;

namespace Honua.Hosting.UnitTests.Infrastructure.Authentication;

public sealed class DelegatedAgentJwtBearerTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("valid-read-scope", true)]
    [InlineData("wrong-issuer", false)]
    [InlineData("wrong-audience", false)]
    [InlineData("wrong-subject", false)]
    [InlineData("wrong-tenant", false)]
    [InlineData("multi-audience", false)]
    [InlineData("duplicate-subject", false)]
    [InlineData("duplicate-tenant", false)]
    [InlineData("duplicate-alternate-tenant", false)]
    [InlineData("wire-duplicate-sub", false)]
    [InlineData("wire-duplicate-aud", false)]
    [InlineData("wire-duplicate-tenant_id", false)]
    [InlineData("wire-duplicate-iss", false)]
    [InlineData("expired", false)]
    [InlineData("forged-signature", false)]
    [InlineData("removed-profile-omitted-scope-admin", false)]
    [InlineData("disabled-profile-admin", false)]
    [InlineData("active-profile-omitted-scope", false)]
    public async Task ProductionJwtBearerPipeline_RequiresValidatedExactProfile(string variant, bool expectedAgent)
    {
        using var rsa = RSA.Create(2048);
        using var otherRsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "trusted" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Oidc:Enabled"] = "true",
            ["Oidc:Generic:Enabled"] = "true",
            ["Oidc:Generic:Authority"] = "https://issuer.example",
            ["Oidc:Generic:ClientId"] = "honua",
            ["Oidc:TokenValidation:EnableTokenReplayProtection"] = "false"
        }).Build();
        var services = new ServiceCollection().AddLogging().AddMemoryCache();
        services.AddSingleton<IConfiguration>(config);
        // Production binds DI options separately in AuthenticationOptionsRegistration.
        services.Configure<OidcAuthenticationOptions>(config.GetSection(OidcAuthenticationOptions.SectionName));
        services.AddOidcAuthentication(config);
        services.Configure<RbacOptions>(options => options.RoleClaimType = "groups");
        services.Configure<OidcAuthenticationOptions>(options =>
        {
            options.DelegatedAgentAudiences = [new() { Issuer = "https://issuer.example", Audience = "honua" }];
            options.DelegatedAgents = variant == "removed-profile-omitted-scope-admin" ? [] : [new OidcDelegatedAgentProfile
            {
                Id = "worker", Generation = 1, Enabled = variant != "disabled-profile-admin", Issuer = "https://issuer.example", Audience = "honua",
                Subject = "subject", Tenant = "tenant", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Permissions = ["agent:workspace"]
            }];
        });
        services.PostConfigure<JwtBearerOptions>(OidcAuthenticationExtensions.JwtBearerScheme, options =>
        {
            var discovery = new OpenIdConnectConfiguration { Issuer = "https://issuer.example" };
            discovery.SigningKeys.Add(key);
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(discovery);
            options.TokenValidationParameters.ValidIssuer = "https://issuer.example";
            options.TokenValidationParameters.ValidAudience = "honua";
            options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
        });
        var membership = Substitute.For<IPrincipalMembershipSource>();
        membership.ResolveMembershipAsync("subject", "https://issuer.example", Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(true, ["admin"]));
        var roles = Substitute.For<IRoleStore>();
        roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new EffectivePermissions { UserId = "subject", Roles = ["worker"], Permissions = [new PermissionGrant { Service = "agent", Layer = "*", Operation = "workspace" }] });
        services.AddSingleton(membership).AddSingleton(roles);
        await using var provider = services.BuildServiceProvider();
        var token = new JwtSecurityToken(
            issuer: variant == "wrong-issuer" ? "https://evil.example" : "https://issuer.example",
            audience: variant == "wrong-audience" ? "other" : "honua",
            claims: [new Claim("scope", variant.Contains("omitted-scope", StringComparison.Ordinal) ? "" : variant == "valid-read-scope" ? "agent:workspace " + OperatorScopeCatalog.Read : "agent:workspace"), new Claim("sub", variant == "wrong-subject" ? "other" : "subject"),
                new Claim("tenant_id", variant == "wrong-tenant" ? "other" : "tenant"),
                new Claim("permission", "agent:workspace"), new Claim("api_key_id", Guid.NewGuid().ToString()),
                new Claim("auth_type", "admin-api-key"), new Claim(ClaimTypes.Role, "admin"), new Claim("GROUPS", "admin")],
            notBefore: DateTime.UtcNow.AddMinutes(-10),
            expires: variant == "expired" ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(variant == "forged-signature" ? new RsaSecurityKey(otherRsa) : key, SecurityAlgorithms.RsaSha256));
        if (variant == "multi-audience") token.Payload["aud"] = new[] { "honua", "other" };
        if (variant == "duplicate-subject") token.Payload["sub"] = new[] { "subject", "subject" };
        if (variant == "duplicate-tenant") token.Payload["tenant_id"] = new[] { "tenant", "tenant" };
        if (variant == "duplicate-alternate-tenant") token.Payload["tid"] = new[] { "tenant", "tenant" };
        var context = new DefaultHttpContext { RequestServices = provider };
        var encoded = new JwtSecurityTokenHandler().WriteToken(token);
        if (variant.StartsWith("wire-duplicate-", StringComparison.Ordinal))
        {
            var claim = variant["wire-duplicate-".Length..];
            var parts = encoded.Split('.');
            // Keep the valid property last: a parser's last-value-wins behavior must
            // not turn an ambiguous, correctly signed credential into authority.
            var payload = Base64UrlEncoder.Decode(parts[1]);
            payload = "{\"" + claim + "\":\"other\"," + payload[1..];
            var signingInput = parts[0] + "." + Base64UrlEncoder.Encode(payload);
            encoded = signingInput + "." + Base64UrlEncoder.Encode(rsa.SignData(
                Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        context.Request.Headers.Authorization = "Bearer " + encoded;
        var result = await context.AuthenticateAsync(OidcAuthenticationExtensions.JwtBearerScheme);
        (result.Succeeded && AgentCredentialAuthority.IsAgent(result.Principal!)).Should().Be(expectedAgent);
        if (result.Succeeded)
        {
            result.Principal!.IsInRole("admin").Should().BeFalse();
            RbacRoleClaims.IsAdmin(result.Principal, new RbacOptions { RoleClaimType = "groups" }, true).Should().BeFalse();
            AdminApiKeyPermission.IsAuthorized(result.Principal, "POST").Should().BeFalse();
            var approval = new AuthorizationHandlerContext([new AdminApproveRequirement()], result.Principal, "another-agent-proposal");
            await new AdminApproveAuthorizationHandler().HandleAsync(approval);
            approval.HasFailed.Should().BeTrue();
        }
        if (expectedAgent)
        {
            var credential = await AgentCredentialAuthority.CaptureAsync(result.Principal!, provider, default);
            credential.Should().NotBeNull();
            var replay = await AgentCredentialAuthority.RevalidateAsync(credential!, provider, default);
            replay.Should().NotBeNull();
            var scopeAuthorizer = new Honua.Core.Features.Authorization.OperatorScopeAuthorizer();
            foreach (var candidate in new[] { result.Principal!, replay! })
            {
                var read = scopeAuthorizer.Evaluate(candidate, OperatorResourceType.Deployment, OperatorOperation.Read);
                read.IsScopeGoverned.Should().BeTrue();
                read.IsAllowed.Should().Be(variant == "valid-read-scope");
                scopeAuthorizer.Evaluate(candidate, OperatorResourceType.Deployment, OperatorOperation.Publish).IsAllowed.Should().BeFalse();
            }
            result.Principal!.FindFirst("api_key_id").Should().BeNull();
            result.Principal.FindFirst("auth_type")!.Value.Should().NotBe("admin-api-key");
        }
    }
    [Theory]
    [InlineData("unchanged")]
    [InlineData("membership")]
    [InlineData("grant")]
    [InlineData("generation")]
    [InlineData("ceiling")]
    [InlineData("issuer")]
    public async Task ProductionJwtBearerPipeline_JobLeaseRetainsOnlyCurrentBoundedLineageAfterOriginExpiry(string change)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "trusted-lease" };
        var clock = new LeasePipelineClock();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Oidc:Enabled"] = "true", ["Oidc:Generic:Enabled"] = "true",
            ["Oidc:Generic:Authority"] = "https://issuer.example", ["Oidc:Generic:ClientId"] = "honua",
            ["Oidc:TokenValidation:EnableTokenReplayProtection"] = "false"
        }).Build();
        var profile = new OidcDelegatedAgentProfile
        {
            Id = "worker", Generation = 1, Enabled = true, Issuer = "https://issuer.example", Audience = "honua",
            Subject = "subject", Tenant = "tenant", ExpiresAt = clock.GetUtcNow().AddHours(1),
            Permissions = ["agent:workspace", "admin:write"]
        };
        var membership = Substitute.For<IPrincipalMembershipSource>();
        membership.ResolveMembershipAsync("subject", "https://issuer.example", Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(true, ["worker"]));
        var roles = Substitute.For<IRoleStore>();
        roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new EffectivePermissions { UserId = "subject", Roles = ["worker"], Permissions = [
                new PermissionGrant { Service = "agent", Layer = "*", Operation = "workspace" },
                new PermissionGrant { Service = "admin", Layer = "*", Operation = "write" }] });
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant");
        var store = Substitute.For<IExecutionJobStore>();
        ExecutionJobRecord? stored = null;
        store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(stored));
        var scopeProvider = Substitute.For<IJobExecutionLeaseScopeProvider>();
        scopeProvider.Kind.Returns(ExecutionJobKind.Geoprocessing);
        scopeProvider.PrepareAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(new JobExecutionLeaseScope
        {
            Kind = ExecutionJobKind.Geoprocessing, Resources = [], ProviderBinding = "server-verified-constant-plan",
            Operations = [new() { ResourceType = OperatorResourceType.Process, Operation = OperatorOperation.Execute }]
        });
        scopeProvider.RequireCurrentAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<JobExecutionLeaseScope>(), Arg.Any<JobSecurityContext>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<JobExecutionLeaseObservationKind?>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var policy = Substitute.For<IJobExecutionLeasePolicy>();
        policy.AuthorizeIssuanceAsync(Arg.Any<JobExecutionLeaseIssuanceRequest>(), Arg.Any<JobExecutionLeaseScope>(), Arg.Any<CancellationToken>())
            .Returns(new JobExecutionLeasePolicyDecision { IsAllowed = true, DecisionId = "canonical-benign", MaximumExpiresAt = clock.GetUtcNow().AddMinutes(30) });
        policy.RequireCurrentAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<JobExecutionLeasePolicyDecision>(), Arg.Any<JobExecutionLeaseScope>(), Arg.Any<IJobExecutionLeaseCurrentPolicy>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var services = new ServiceCollection().AddLogging().AddMemoryCache()
            .AddSingleton<IConfiguration>(config).AddSingleton<TimeProvider>(clock)
            .AddSingleton(membership).AddSingleton(roles).AddSingleton(tenant).AddSingleton(store)
            .AddSingleton(policy).AddSingleton(scopeProvider)
            .AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider())
            .AddScoped<JobExecutionLeaseAuthority>().AddSingleton<IJobExecutionLeaseAuthority, JobExecutionLeaseAuthorityFacade>();
        services.Configure<OidcAuthenticationOptions>(config.GetSection(OidcAuthenticationOptions.SectionName));
        services.AddOidcAuthentication(config);
        services.Configure<OidcAuthenticationOptions>(options =>
        {
            options.DelegatedAgentAudiences = [new() { Issuer = "https://issuer.example", Audience = "honua" }];
            options.DelegatedAgents = [profile];
        });
        services.PostConfigure<JwtBearerOptions>(OidcAuthenticationExtensions.JwtBearerScheme, options =>
        {
            var discovery = new OpenIdConnectConfiguration { Issuer = "https://issuer.example" };
            discovery.SigningKeys.Add(key);
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(discovery);
            options.TokenValidationParameters.ValidIssuer = "https://issuer.example";
            options.TokenValidationParameters.ValidAudience = "honua";
            options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
        });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var token = new JwtSecurityToken("https://issuer.example", "honua",
            [new Claim("sub", "subject"), new Claim("tenant_id", "tenant"),
                new Claim("scope", "agent:workspace " + OperatorScopeCatalog.Full)],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        await using var requestScope = provider.CreateAsyncScope();
        var requestServices = requestScope.ServiceProvider;
        var http = new DefaultHttpContext { RequestServices = requestServices };
        http.Request.Headers.Authorization = "Bearer " + new JwtSecurityTokenHandler().WriteToken(token);
        var authentication = await http.AuthenticateAsync(OidcAuthenticationExtensions.JwtBearerScheme);
        authentication.Succeeded.Should().BeTrue();
        var executor = authentication.Principal!;
        AgentCredentialAuthority.IsAgent(executor).Should().BeTrue();
        var context = await AgentJobSecurityBridge.CaptureAsync(executor, new RbacOptions(), requestServices, tenant, default);
        var job = new ExecutionJobRecord
        {
            OperationId = "jwt-lease-job", Status = ExecutionJobStatus.Queued, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow(),
            Spec = new() { TargetKind = BatchComputeTargetKind.LocalProcess, Backend = "local", Kind = ExecutionJobKind.Geoprocessing, WorkloadName = "constant" },
            Audit = new() { RequestedBy = context.OwnerActorId, TenantId = context.TenantId, SubmitterSecurityContext = context }
        };
        var authority = provider.GetRequiredService<IJobExecutionLeaseAuthority>();
        var lease = await authority.IssueAsync(new() { PreparedJob = job, Executor = executor, RequestedExpiresAt = clock.GetUtcNow().AddMinutes(20) });
        stored = job with { Audit = job.Audit with { SubmitterSecurityContext = context with { ExecutionLease = lease } } };
        clock.UtcNow = clock.GetUtcNow().AddMinutes(6);
        (await AgentCredentialAuthority.CaptureAsync(executor, provider, default)).Should().BeNull();
        switch (change)
        {
            case "membership": membership.ResolveMembershipAsync("subject", "https://issuer.example", Arg.Any<CancellationToken>()).Returns(new PrincipalMembership(false, [])); break;
            case "grant": roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                    .Returns(new EffectivePermissions { UserId = "subject", Roles = ["worker"], Permissions = [] }); break;
            case "generation": profile.Generation++; break;
            case "ceiling": profile.Permissions = ["agent:workspace"]; break;
            case "issuer": profile.Issuer = "https://other.example"; break;
        }
        if (change != "unchanged")
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authority.RevalidateForExecutionAsync(stored));
            return;
        }
        var current = await authority.RevalidateForExecutionAsync(stored);
        using var scope = JobSecurityScope.BeginExecution(stored, current);
        (await AgentCredentialAuthority.IsCurrentAsync(current.CurrentAgentPrincipal!, provider, default)).Should().BeTrue();
        current.CurrentAgentPrincipal!.FindFirst("api_key_id").Should().BeNull();
        AdminApiKeyPermission.ResolveAccessLevel(current.CurrentAgentPrincipal).Should().Be(AdminApiKeyPermission.AdminAccessLevel.None);
        current.AgentCredential!.TokenExpiresAt.Should().Be(context.AgentCredential!.TokenExpiresAt);
        clock.UtcNow = lease.ExpiresAt;
        (await AgentCredentialAuthority.IsCurrentAsync(current.CurrentAgentPrincipal, provider, default)).Should().BeFalse();
    }

    private sealed class LeasePipelineClock : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

}
