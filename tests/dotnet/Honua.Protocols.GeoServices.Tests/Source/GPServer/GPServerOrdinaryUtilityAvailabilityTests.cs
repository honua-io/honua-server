// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Geoprocessing;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Server.Tests.Features.Protocols.GeoServices.NAServer;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>Ordinary service utility aliases retain authorization and dependency failure contracts.</summary>
[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.GPServer)]
public sealed class GPServerOrdinaryUtilityAvailabilityTests
{
    [IntegrationTheory]
    [InlineData("GetTravelModes", true)]
    [InlineData("GetTravelModes", false)]
    [InlineData("GetToolInfo", true)]
    [InlineData("GetToolInfo", false)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task MissingDependency_AuthorizedAliasReturns503WithoutJobDispatch(string task, bool removeProvider)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var terminal = Substitute.For<IGeoprocessingJobTerminalService>();
        using var factory = CreateFactory(jobs, terminal, removeProvider);
        using var client = ServiceRbacTestFixture.CreateClient(factory, "gp-reader");
        var root = $"/rest/services/alpha/GPServer/{task}";
        using var metadata = await client.GetAsync(root + "?f=json");
        await AssertErrorAsync(metadata, 503);
        using var execute = await client.GetAsync(root + "/execute?f=json");
        await AssertErrorAsync(execute, 503);
        using var post = await client.PostAsync(root + "/execute", Form());
        await AssertErrorAsync(post, 503);
        jobs.ReceivedCalls().Should().BeEmpty();
        terminal.ReceivedCalls().Should().BeEmpty();
    }

    [IntegrationTheory]
    [InlineData(null, true, 499)]
    [InlineData(null, false, 499)]
    [InlineData("other-role", true, 403)]
    [InlineData("other-role", false, 403)]
    [Operation(Operations.Security)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task MissingDependency_DoesNotOverrideServiceDenial(string? role, bool removeProvider, int expected)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var terminal = Substitute.For<IGeoprocessingJobTerminalService>();
        using var factory = CreateFactory(jobs, terminal, removeProvider);
        using var client = role is null ? factory.CreateClient() : ServiceRbacTestFixture.CreateClient(factory, role);
        using var metadata = await client.GetAsync("/rest/services/alpha/GPServer/GetTravelModes?f=json");
        await AssertErrorAsync(metadata, expected);
        using var execute = await client.GetAsync("/rest/services/alpha/GPServer/GetTravelModes/execute?f=json");
        await AssertErrorAsync(execute, expected);
        using var post = await client.PostAsync("/rest/services/alpha/GPServer/GetTravelModes/execute", Form());
        await AssertErrorAsync(post, expected);
        jobs.ReceivedCalls().Should().BeEmpty();
        terminal.ReceivedCalls().Should().BeEmpty();
    }

    [IntegrationTheory]
    [InlineData("GetTravelModes")]
    [InlineData("GetToolInfo")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public async Task CanonicalSameNamedTask_DoesNotRequireRoutingDependencies(string task)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var terminal = Substitute.For<IGeoprocessingJobTerminalService>();
        var definition = new BuiltInProcessCatalog().GetProcess("geometry.area")! with { ProcessId = task };
        var catalog = Substitute.For<IProcessCatalog>();
        catalog.ListProcesses().Returns(new[] { definition });
        catalog.GetProcess(task).Returns(definition);
        using var factory = CreateFactory(jobs, terminal, removeProvider: true, catalog: catalog);
        using var client = ServiceRbacTestFixture.CreateClient(factory, "gp-reader");
        using var response = await client.GetAsync($"/rest/services/alpha/GPServer/{task}?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("executionType").GetString().Should().Be("esriExecutionTypeAsynchronous");
        body.RootElement.GetProperty("parameters").EnumerateArray().Select(parameter => parameter.GetProperty("name").GetString())
            .Should().Contain("wkb").And.Contain("srid").And.NotContain("supportedTravelModes");
        jobs.ReceivedCalls().Should().BeEmpty();
        terminal.ReceivedCalls().Should().BeEmpty();
    }

    [IntegrationTheory]
    [InlineData("Routing")]
    [InlineData("NetworkAnalysisUtilities")]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task ReservedPublicationCollision_DoesNotDisableAuthorizedOrdinaryAlias(string reservedName)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var terminal = Substitute.For<IGeoprocessingJobTerminalService>();
        using var factory = CreateFactory(jobs, terminal, removeProvider: null);
        var provider = (TestMetadataV2GraphProvider)factory.Services.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await provider.GetCurrentAsync();
        var ordinary = snapshot.Index.ServicesByName["alpha"];
        var collision = ordinary with { Metadata = ordinary.Metadata with { Id = "availability-collision", Name = reservedName } };
        provider.SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Services = [.. snapshot.Graph.Services, collision]
        });
        using var client = ServiceRbacTestFixture.CreateClient(factory, "gp-reader");
        using var response = await client.GetAsync("/rest/services/alpha/GPServer/GetTravelModes/execute?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("results")[0].GetProperty("value").GetProperty("features").GetArrayLength().Should().BeGreaterThan(0);
        jobs.ReceivedCalls().Should().BeEmpty();
        terminal.ReceivedCalls().Should().BeEmpty();
    }

    private static WebApplicationFactory<Program> CreateFactory(
        IGeoprocessingJobService jobs, IGeoprocessingJobTerminalService terminal, bool? removeProvider, IProcessCatalog? catalog = null)
        => ServiceRbacTestFixture.CreateFactory(
            layerCatalogFactory: static () => new RbacTestLayerCatalog(
                alphaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["gp-reader"])),
            configureServices: services =>
            {
                services.RemoveAll<IGeoprocessingJobService>();
                services.AddSingleton(jobs);
                services.RemoveAll<IGeoprocessingJobTerminalService>();
                services.AddSingleton(terminal);
                services.RemoveAll<IRoutingProvider>();
                if (removeProvider is not true)
                {
                    services.AddScoped<IRoutingProvider, TestRoutingProvider>();
                }
                services.RemoveAll<INetworkDatasetResolver>();
                if (removeProvider is not false)
                {
                    var resolver = Substitute.For<INetworkDatasetResolver>();
                    resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(NetworkDataset.Default);
                    services.AddSingleton(resolver);
                }
                if (catalog is not null)
                {
                    services.RemoveAll<IProcessCatalog>();
                    services.AddSingleton(catalog);
                }
            });

    private static FormUrlEncodedContent Form() => new(new Dictionary<string, string> { ["f"] = "json" });

    private static async Task AssertErrorAsync(HttpResponseMessage response, int expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var body = JsonDocument.Parse(text);
        body.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(expected);
        body.RootElement.TryGetProperty("results", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("jobId", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("parameters", out _).Should().BeFalse();
    }
}
