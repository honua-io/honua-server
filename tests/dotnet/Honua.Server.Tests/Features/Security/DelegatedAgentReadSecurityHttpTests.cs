// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Honua.Server.Tests.Features.Security;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class DelegatedAgentReadSecurityHttpTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task RealDelegatedJwt_QueryRetainsManagedRoleRowsAndFields_RevocationDeniesAccess()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "read-agent" };
        var membership = Substitute.For<IPrincipalMembershipSource>();
        membership.ResolveMembershipAsync("reader", "https://workload.example", Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(true, ["worker", "admin"]));
        var roles = Substitute.For<IRoleStore>();
        roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new EffectivePermissions
            {
                UserId = "reader",
                Roles = ["worker", "admin"],
                Permissions = [
                new() { Service = "agent", Layer = "*", Operation = "workspace" },
                new() { Service = "*", Layer = "*", Operation = "*" }]
            });
        var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", "delegated-read-test-admin");
            builder.UseSetting("Oidc:Enabled", "true");
            builder.UseSetting("Oidc:Generic:Enabled", "true");
            builder.UseSetting("Oidc:Generic:Authority", "https://workload.example");
            builder.UseSetting("Oidc:Generic:ClientId", "honua-workloads");
            builder.UseSetting("Oidc:TokenValidation:EnableTokenReplayProtection", "false");
        }).ConfigureServices(services =>
        {
            services.RemoveAll<IPrincipalMembershipSource>();
            services.AddSingleton(membership);
            services.RemoveAll<IRoleStore>();
            services.AddSingleton(roles);
            services.Configure<OidcAuthenticationOptions>(options =>
            {
                options.DelegatedAgentAudiences = [new() { Issuer = "https://workload.example", Audience = "honua-workloads" }];
                options.DelegatedAgents = [new() { Id = "read-agent", Generation = 1, Enabled = true,
                    Issuer = "https://workload.example", Audience = "honua-workloads", Subject = "reader", Tenant = "public",
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Permissions = ["agent:workspace", "read:*"] }];
            });
            services.PostConfigure<JwtBearerOptions>(OidcAuthenticationExtensions.JwtBearerScheme, options =>
            {
                var discovery = new OpenIdConnectConfiguration { Issuer = "https://workload.example" };
                discovery.SigningKeys.Add(key);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(discovery);
            });
        });
        await fixture.InitializeAsync();
        try
        {
            await using var scope = fixture.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IRlsPolicyStore>().CreatePolicyAsync(new()
            {
                Role = "worker",
                Service = "*",
                Layer = "*",
                Attribute = "category",
                ClaimType = "category"
            });
            await scope.ServiceProvider.GetRequiredService<IFieldMaskPolicyStore>().CreatePolicyAsync(new()
            {
                Role = "worker",
                Service = "*",
                Layer = "*",
                Attribute = "description"
            });
            using var client = fixture.CreateClient();
            var token = new JwtSecurityToken("https://workload.example", "honua-workloads",
                [new("sub", "reader"), new("tenant_id", "public"), new("scope", "agent:workspace"), new("category", "test"), new(ClaimTypes.Role, "admin")],
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
            var path = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1%3D1&outFields=*&returnGeometry=false&f=json";
            using var result = await client.GetAsync(path);
            var payload = await result.Content.ReadAsStringAsync();
            result.StatusCode.Should().Be(HttpStatusCode.OK, payload);
            using var document = JsonDocument.Parse(payload);
            var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();
            features.Should().HaveCount(3);
            foreach (var feature in features)
            {
                var attributes = feature.GetProperty("attributes");
                attributes.GetProperty("category").GetString().Should().Be("test");
                attributes.TryGetProperty("description", out _).Should().BeFalse();
            }
            membership.ResolveMembershipAsync("reader", "https://workload.example", Arg.Any<CancellationToken>())
                .Returns(new PrincipalMembership(false, []));
            using var denied = await client.GetAsync(path);
            denied.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
        }
        finally { await fixture.DisposeAsync(); }
    }
}
