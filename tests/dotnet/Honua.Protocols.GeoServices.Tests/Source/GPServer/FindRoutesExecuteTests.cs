// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Server.Tests.Features.Protocols.GeoServices.NAServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>HTTP execution evidence for the bounded FindRoutes tool and both advertised URLs.</summary>
[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.GPServer)]
public sealed class FindRoutesExecuteTests(FindRoutesExecuteTestsFixture fixture)
    : IClassFixture<FindRoutesExecuteTestsFixture>
{
    private const string Stops = "-157.858333,21.306944;-157.862,21.31";

    [IntegrationTheory]
    [InlineData("GPServer", false, false)]
    [InlineData("GPServer", true, false)]
    [InlineData("NAServer", false, false)]
    [InlineData("NAServer", true, false)]
    [InlineData("GPServer", false, true)]
    [InlineData("GPServer", true, true)]
    [InlineData("NAServer", false, true)]
    [InlineData("NAServer", true, true)]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("GET /rest/services/{serviceId}/NAServer/FindRoutes/execute")]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/FindRoutes/execute")]
    public async Task Execute_DefaultOrSupportedOptions_MatchesCanonicalRouteSolve(string service, bool post, bool explicitOptions)
    {
        var parameters = new Dictionary<string, string> { ["f"] = "json", ["Stops"] = Stops };
        if (explicitOptions)
        {
            parameters["Measurement_Units"] = "Minutes";
            parameters["Reorder_Stops_to_Find_Optimal_Routes"] = "false";
            parameters["Travel_Mode"] = "walking";
        }

        fixture.Requests.Clear();
        using var actual = await ExecuteAsync(service, post, parameters);
        var findRoutesRequest = fixture.Requests.Single();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["stops"] = Stops,
            ["travelMode"] = explicitOptions ? "walking" : "",
        });
        using var response = await fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/NAServer/Route/solve", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var canonical = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        fixture.Requests.Should().HaveCount(2);
        findRoutesRequest.Should().BeEquivalentTo(fixture.Requests.Last());
        findRoutesRequest.Stops.Should().Equal(new RoutePoint(-157.858333, 21.306944), new RoutePoint(-157.862, 21.31));
        findRoutesRequest.TravelMode.Should().Be(explicitOptions ? "walking" : null);
        var results = actual.RootElement.GetProperty("results");
        results[0].GetProperty("paramName").GetString().Should().Be("Output_Routes");
        results[0].GetProperty("value").GetRawText().Should().Be(canonical.RootElement.GetProperty("routes").GetRawText());
        results[1].GetProperty("value").GetBoolean().Should().BeTrue();
    }

    [IntegrationTheory]
    [InlineData("GPServer")]
    [InlineData("NAServer")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/FindRoutes/execute")]
    public async Task Execute_UnsupportedOptionsOrAliases_RejectsBeforeSolving(string service)
    {
        var rejected = new (string Key, string Value, string Error)[]
        {
            ("Measurement_Units", "Kilometers", "Minutes"),
            ("measurementUnits", "Hours", "Minutes"),
            ("impedanceAttributeName", "Kilometers", "TravelTime"),
            ("Reorder_Stops_to_Find_Optimal_Routes", "true", "input order"),
            ("Reorder_Stops_to_Find_Optimal_Route", "true", "input order"),
            ("findBestSequence", "true", "input order"),
            ("Reorder_Stops_to_Find_Optimal_Routes", "invalid", "input order"),
            ("Travel_Mode", "flying", "not supported"),
            ("travelMode", "flying", "conflict"),
            ("f", "xml", "Unsupported output format"),
            ("Point_Barriers", """{"features":[{"geometry":{"x":-157.86,"y":21.31}}]}""", "Point barriers"),
            ("barriers", """{"features":[{"geometry":{"x":-157.86,"y":21.31}}]}""", "Point barriers"),
            ("Return_to_Start", "true", "not supported"),
            ("Stops", "-157.858333,21.306944", "At least two"),
        };
        fixture.Requests.Clear();
        foreach (var (key, value, error) in rejected)
        {
            var parameters = new Dictionary<string, string>
            {
                ["f"] = "json",
                ["Stops"] = Stops,
                ["Travel_Mode"] = "driving",
                ["Measurement_Units"] = "Minutes",
                ["Reorder_Stops_to_Find_Optimal_Routes"] = "false",
            };
            parameters[key] = value;
            using var document = await ExecuteAsync(service, true, parameters);
            document.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(400);
            document.RootElement.GetProperty("error").GetRawText().Should().Contain(error);
            document.RootElement.TryGetProperty("results", out _).Should().BeFalse();
        }
        fixture.Requests.Should().BeEmpty("unsupported inputs must never reach the provider");
    }

    private async Task<JsonDocument> ExecuteAsync(string service, bool post, Dictionary<string, string> parameters)
    {
        var url = $"/rest/services/{WebAppFixture.TestServiceId}/{service}/FindRoutes/execute";
        using var content = new FormUrlEncodedContent(parameters);
        using var response = post
            ? await fixture.Client.PostAsync(url, content)
            : await fixture.Client.GetAsync(url + "?" + await content.ReadAsStringAsync());
        response.StatusCode.Should().Be(HttpStatusCode.OK, "GeoServices uses an HTTP 200 envelope for errors too");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}

/// <summary>Captures actual provider calls made by the HTTP adapter.</summary>
public sealed class FindRoutesExecuteTestsFixture : IAsyncLifetime
{
    public ConcurrentQueue<RouteSolveRequest> Requests { get; } = new();

    public WebAppFixture App { get; }

    public FindRoutesExecuteTestsFixture()
    {
        var provider = new TestRoutingProvider(
            new RoutingProviderCapabilities(SupportsRoute: true) { SupportedTravelModes = ["driving", "walking"] },
            request =>
            {
                Requests.Enqueue(request);
                return null;
            });
        App = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IRoutingProvider>();
                services.AddSingleton<IRoutingProvider>(provider);
            });
    }

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        Client = App.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }
}
