// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.GPServer;
using Honua.Protocols.GeoServices.GPServer.Models;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

[Protocol(TestProtocols.GPServer)]
[Trait("Tier", "Fast")]
public sealed class RoutingServiceMetadataContractTests
{
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    [Endpoint("POST /rest/services/{serviceId}/GPServer")]
    public async Task RoutingRoot_ProvidesTheCompleteGpServiceDescriptorWithoutClaimingAProductRelease(string method)
    {
        var routing = Substitute.For<IRoutingProvider>();
        routing.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(
            new RoutingProviderCapabilities(SupportsRoute: true, SupportsServiceArea: true));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(routing);
        builder.Services.AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(new MetadataV2Graph()));
        await using var app = builder.Build();
        app.MapGPServerEndpoints();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var request = new HttpRequestMessage(new HttpMethod(method), "/rest/services/Routing/GPServer?f=json");
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        // Required members and their types come from the published GPServer schema.
        root.EnumerateObject().Select(property => property.Name).Should().Contain(
            ["currentVersion", "serviceDescription", "tasks", "executionType", "resultMapServerName", "maximumRecords", "capabilities"]);
        root.GetProperty("currentVersion").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("currentVersion").GetDouble().Should().Be(10.8);
        root.GetProperty("serviceDescription").GetString().Should().StartWith("Honua ");
        root.GetProperty("tasks").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("executionType").GetString().Should().Be("esriExecutionTypeSynchronous");
        root.GetProperty("resultMapServerName").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("maximumRecords").GetInt32().Should().BePositive();
        root.GetProperty("capabilities").GetString().Should().BeEmpty();
        root.TryGetProperty("fullVersion", out _).Should().BeFalse();
    }

    [UnitTest]
    public void RoutingSelector_DoesNotLeakThroughOrdinaryGpSerialization()
    {
        GPServiceInfoResponse descriptor = new RoutingGPServiceInfoResponse
        {
            ServiceDescription = "Honua catalog service",
            ExecutionType = "esriExecutionTypeAsynchronous",
            Tasks = ["Buffer"],
            Capabilities = string.Empty,
            ResultMapServerName = string.Empty
        };
        var json = JsonSerializer.Serialize(descriptor, GPServerJsonContext.Default.GPServiceInfoResponse);
        using var document = JsonDocument.Parse(json);
        document.RootElement.TryGetProperty("currentVersion", out _).Should().BeFalse();
        document.RootElement.TryGetProperty("fullVersion", out _).Should().BeFalse();
        document.RootElement.GetProperty("tasks")[0].GetString().Should().Be("Buffer");
    }
}
