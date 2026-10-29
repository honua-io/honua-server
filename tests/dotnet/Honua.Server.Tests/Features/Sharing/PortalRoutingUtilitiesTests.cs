// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
using System.Text.Json;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Sharing;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class PortalRoutingUtilitiesTests
{
    [IntegrationTheory]
    [InlineData("mock", true)]
    [InlineData("unavailable", false)]
    [Endpoint("GET /sharing/rest/portals/self")]
    public async Task PortalHelper_ReferencesAvailableRoutingUtilities(string provider, bool available)
    {
        var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("Routing:Provider", provider);
        });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var response = await client.GetAsync("/sharing/rest/portals/self?f=json");
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var helpers = body.RootElement.GetProperty("helperServices");
            Assert.Equal(available, helpers.TryGetProperty("routingUtilities", out var utility));
            if (available)
            {
                var uri = new Uri(utility.GetProperty("url").GetString()!);
                Assert.Equal("/rest/services/Routing/GPServer", uri.AbsolutePath);
                using var gp = await client.GetAsync(uri.PathAndQuery + "?f=json");
                gp.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await gp.Content.ReadAsStringAsync());
                var tasks = document.RootElement.GetProperty("tasks").EnumerateArray().Select(x => x.GetString()).ToArray();
                Assert.Contains("GetTravelModes", tasks);
                Assert.Contains("GetToolInfo", tasks);
            }
        }
        finally { await fixture.DisposeAsync(); }
    }
}
