// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
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
            claims: [new Claim("scope", variant.Contains("omitted-scope", StringComparison.Ordinal) ? "" : "agent:workspace"), new Claim("sub", variant == "wrong-subject" ? "other" : "subject"),
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
            result.Principal!.FindFirst("api_key_id").Should().BeNull();
            result.Principal.FindFirst("auth_type")!.Value.Should().NotBe("admin-api-key");
        }
    }
}
