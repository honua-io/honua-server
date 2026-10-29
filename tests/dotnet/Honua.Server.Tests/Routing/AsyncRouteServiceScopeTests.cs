// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Routing;

public sealed class AsyncRouteServiceScopeTests
{
    [Fact]
    public void ConfiguredService_AloneResolvesAndListsAllRouteNames()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Routing:AsyncGPServiceId"] = "permitted" }).Build();
        using var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var catalog = new BuiltInProcessCatalog();
        var routeNames = GPServerEndpoints.BuildPublishedTaskNames(catalog)
            .Where(n => GPServerEndpoints.ResolveTaskDefinition(catalog, n)?.ProcessId == RoutingProcessDefinitions.Route).ToArray();
        Assert.Contains("FindRoutes", routeNames);
        Assert.NotEmpty(routeNames);
        foreach (var name in routeNames.Append("routing.route"))
        {
            Assert.Null(GPServerEndpoints.ResolveTaskDefinitionForService(context, "other", catalog, name));
            Assert.NotNull(GPServerEndpoints.ResolveTaskDefinitionForService(context, "permitted", catalog, name));
        }
        var unrelated = GPServerEndpoints.BuildPublishedTaskNamesForService(context, "other", catalog).ToHashSet();
        Assert.DoesNotContain(routeNames, unrelated.Contains);
        var configured = GPServerEndpoints.BuildPublishedTaskNamesForService(context, "permitted", catalog).ToHashSet();
        Assert.All(routeNames, name => Assert.Contains(name, configured));
    }
    [IntegrationTest]
    public async Task OtherPublishedService_DoesNotListOrSubmitAnyRouteProcessName()
    {
        var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("Routing:AsyncGPServiceId", "other");
        });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            var catalog = new BuiltInProcessCatalog();
            var names = GPServerEndpoints.BuildPublishedTaskNames(catalog)
                .Where(n => GPServerEndpoints.ResolveTaskDefinition(catalog, n)?.ProcessId == RoutingProcessDefinitions.Route)
                .Append("routing.route").Distinct().ToArray();
            using var listing = await client.GetAsync("/rest/services/test/GPServer?f=json");
            listing.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await listing.Content.ReadAsStringAsync());
            var tasks = document.RootElement.GetProperty("tasks").EnumerateArray().Select(x => x.GetString()).ToArray();
            Assert.DoesNotContain(names, tasks.Contains);
            foreach (var name in names.Where(n => n != "FindRoutes"))
            {
                using var info = await client.GetAsync($"/rest/services/test/GPServer/{Uri.EscapeDataString(name)}?f=json");
                using var infoBody = JsonDocument.Parse(await info.Content.ReadAsStringAsync());
                Assert.Equal(404, infoBody.RootElement.GetProperty("error").GetProperty("code").GetInt32());
                using var content = new StringContent("invalid", Encoding.UTF8, "application/json");
                using var submit = await client.PostAsync($"/rest/services/test/GPServer/{Uri.EscapeDataString(name)}/submitJob", content);
                using var submitBody = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
                Assert.Equal(404, submitBody.RootElement.GetProperty("error").GetProperty("code").GetInt32());
                Assert.False(submitBody.RootElement.TryGetProperty("jobId", out _));
            }
            const string soap = "<soap:Envelope xmlns:soap='http://schemas.xmlsoap.org/soap/envelope/' xmlns:tns='http://www.esri.com/schemas/ArcGIS/10.8'><soap:Body><tns:GetToolNames/></soap:Body></soap:Envelope>";
            using var xml = new StringContent(soap, Encoding.UTF8, "text/xml");
            using var reply = await client.PostAsync("/services/test/GPServer", xml);
            reply.EnsureSuccessStatusCode();
            var text = await reply.Content.ReadAsStringAsync();
            Assert.DoesNotContain(names, text.Contains);
        }
        finally { await fixture.DisposeAsync(); }
    }

}
