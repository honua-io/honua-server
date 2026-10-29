// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Sharing;

[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class PortalRoutingPrivilegeTests
{
    [IntegrationTheory]
    [InlineData("mock", true)]
    [InlineData("unavailable", false)]
    [Endpoint("GET /sharing/rest/portals/self")]
    public async Task AdminRoutingPrivilege_RequiresRealProviderCapability(string provider, bool available)
    {
        var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
            builder.UseSetting("Routing:Provider", provider);
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new StableIpFilter()));
        });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "admin", ["password"] = WebAppFixture.SharedAdminPassword,
                ["client"] = "requestip", ["f"] = "json"
            });
            using var tokenResponse = await client.PostAsync("/sharing/rest/generateToken", form);
            tokenResponse.EnsureSuccessStatusCode();
            using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
            var token = tokenJson.RootElement.GetProperty("token").GetString();
            using var response = await client.GetAsync("/sharing/rest/portals/self?f=json&token=" + Uri.EscapeDataString(token!));
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var user = body.RootElement.GetProperty("user");
            var hasPrivilege = user.TryGetProperty("privileges", out var privileges);
            Assert.Equal(available, hasPrivilege);
            if (available)
                Assert.Equal("premium:user:networkanalysis", Assert.Single(privileges.EnumerateArray()).GetString());
            using var anonymous = await client.GetAsync("/sharing/rest/portals/self?f=json");
            anonymous.EnsureSuccessStatusCode();
            using var anonymousBody = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync());
            Assert.False(anonymousBody.RootElement.TryGetProperty("user", out var anonymousUser) && anonymousUser.ValueKind != JsonValueKind.Null);
        }
        finally { await fixture.DisposeAsync(); }
    }

    private sealed class StableIpFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.11");
                await nextMiddleware();
            });
            next(app);
        };
    }
}
