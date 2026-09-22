// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Geoprocessing;
using Honua.Routing.Features.Routing.Abstractions;
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

[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.GPServer)]
public sealed class GPServerReservedNamePublicationTests
{
    private const string AreaTool = "Honua_67656F6D657472792E61726561";
    private const string Wkb = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Arguments = """
        <ToolName>Honua_67656F6D657472792E61726561</ToolName>
        <Values xsi:type="tns:GPValues"><GPValue xsi:type="tns:GPString"><Value>AQEAAAAAAAAAAAAAAAAAAAAAAAAA</Value></GPValue>
        <GPValue xsi:type="tns:GPLong"><Value>3857</Value></GPValue></Values>
        """;

    [IntegrationTheory]
    [InlineData("Routing", false)]
    [InlineData("Routing", true)]
    [InlineData("NetworkAnalysisUtilities", false)]
    [InlineData("NetworkAnalysisUtilities", true)]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    [Endpoint("GET /sharing/rest/portals/self")]
    public async Task CanonicalReservedIdentity_RetainsAuthorizedCatalogMetadataAndJobExecution(string service, bool byId)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var submissions = new List<(AnalysisPlan Plan, IReadOnlyDictionary<string, string>? Binding)>();
        jobs.SubmitJobAsync(Arg.Any<AnalysisPlan>(), Arg.Any<string?>(), Arg.Any<ClaimsPrincipal>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                submissions.Add((call.Arg<AnalysisPlan>(), call.Arg<IReadOnlyDictionary<string, string>?>()));
                return Task.FromResult(Job(service));
            });
        using var factory = CreateFactory(jobs);
        await SetIdentityAsync(factory, service, byId);
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-reader");
        using var metadata = await client.GetAsync($"/rest/services/{service}/GPServer?f=json");
        var metadataText = await metadata.Content.ReadAsStringAsync();
        metadata.StatusCode.Should().Be(HttpStatusCode.OK, metadataText);
        using var root = JsonDocument.Parse(metadataText);
        root.RootElement.GetProperty("executionType").GetString().Should().Be("esriExecutionTypeAsynchronous");
        var tasks = root.RootElement.GetProperty("tasks").EnumerateArray().Select(value => value.GetString()).ToArray();
        tasks.Should().Contain(AreaTool).And.NotContain("GetTravelModes").And.NotContain("GetToolInfo");
        using var discovery = await PostSoapAsync(client, service, "GetToolInfos");
        var discoveryText = await discovery.Content.ReadAsStringAsync();
        discovery.StatusCode.Should().Be(HttpStatusCode.OK, discoveryText);
        XDocument.Parse(discoveryText).Descendants("GPToolInfo").Select(item => item.Element("Name")!.Value).Should().Equal(tasks);
        using var mode = await PostSoapAsync(client, service, "GetExecutionType");
        var modeText = await mode.Content.ReadAsStringAsync();
        mode.StatusCode.Should().Be(HttpStatusCode.OK, modeText);
        XDocument.Parse(modeText).Descendants("Result").Single().Value.Should().Be("esriExecutionTypeAsynchronous");

        using var restJob = await client.PostAsync($"/rest/services/{service}/GPServer/{AreaTool}/submitJob", Form());
        var restText = await restJob.Content.ReadAsStringAsync();
        restJob.StatusCode.Should().Be(HttpStatusCode.OK, restText);
        using var restResult = JsonDocument.Parse(restText);
        restResult.RootElement.GetProperty("jobId").GetString().Should().Be("reserved-job");
        using var soapJob = await PostSoapAsync(client, service, "SubmitJob", Arguments);
        var soapText = await soapJob.Content.ReadAsStringAsync();
        soapJob.StatusCode.Should().Be(HttpStatusCode.OK, soapText);
        XDocument.Parse(soapText).Descendants("Result").Single().Value.Should().Be("reserved-job");
        submissions.Should().HaveCount(2);
        foreach (var submission in submissions)
        {
            var step = submission.Plan.Steps.Should().ContainSingle().Subject;
            step.ProcessId.Should().Be("geometry.area");
            step.Inputs["wkb"].Should().Be(Wkb);
            step.Inputs["srid"].Should().Be("3857");
            submission.Binding![GeoprocessingProtocolMetadataKeys.GPServerServiceId].Should().Be(service);
            submission.Binding[GeoprocessingProtocolMetadataKeys.GPServerTaskName].Should().Be(AreaTool);
        }
        await jobs.Received(2).EnsureCallerAuthorizedAsync(Arg.Any<ClaimsPrincipal>(), OperatorResourceType.Process,
            OperatorOperation.Execute, Arg.Any<CancellationToken>());

        using var portal = await client.GetAsync("/sharing/rest/portals/self?f=json");
        var portalText = await portal.Content.ReadAsStringAsync();
        portal.StatusCode.Should().Be(HttpStatusCode.OK, portalText);
        using var portalBody = JsonDocument.Parse(portalText);
        if (portalBody.RootElement.TryGetProperty("helperServices", out var helpers))
        {
            helpers.TryGetProperty("routingUtilities", out _).Should().BeFalse();
        }
    }

    [IntegrationTheory]
    [InlineData("Routing", false)]
    [InlineData("Routing", true)]
    [InlineData("NetworkAnalysisUtilities", false)]
    [InlineData("NetworkAnalysisUtilities", true)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task CanonicalReservedIdentity_DeniedPrincipalCannotUseSyntheticFallback(string service, bool byId)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        using var factory = CreateFactory(jobs);
        await SetIdentityAsync(factory, service, byId);
        using var anonymous = factory.CreateClient();
        using var denied = ServiceRbacTestFixture.CreateClient(factory, "other-role");
        foreach (var (client, status) in new[] { (anonymous, HttpStatusCode.Unauthorized), (denied, HttpStatusCode.Forbidden) })
        {
            using var metadata = await client.GetAsync($"/rest/services/{service}/GPServer?f=json");
            await AssertRestErrorAsync(metadata, status == HttpStatusCode.Unauthorized ? 499 : 403);
            using var soapMetadata = await PostSoapAsync(client, service, "GetToolInfos");
            soapMetadata.StatusCode.Should().Be(status, await soapMetadata.Content.ReadAsStringAsync());
            using var restJob = await client.PostAsync($"/rest/services/{service}/GPServer/{AreaTool}/submitJob", Form());
            await AssertRestErrorAsync(restJob, status == HttpStatusCode.Unauthorized ? 499 : 403);
            using var soapJob = await PostSoapAsync(client, service, "SubmitJob", Arguments);
            soapJob.StatusCode.Should().Be(status, await soapJob.Content.ReadAsStringAsync());
            using var malformed = await client.PostAsync($"/rest/services/{service}/GPServer/{AreaTool}/submitJob",
                new StringContent("not a form", Encoding.UTF8, "text/plain"));
            await AssertRestErrorAsync(malformed, status == HttpStatusCode.Unauthorized ? 499 : 403);
            using var malformedSoap = await PostSoapAsync(client, service, "SubmitJob",
                Arguments.Replace("<Value>3857</Value>", "<Value>not-an-integer</Value>", StringComparison.Ordinal));
            malformedSoap.StatusCode.Should().Be(status, await malformedSoap.Content.ReadAsStringAsync());
        }
        jobs.ReceivedCalls().Should().BeEmpty();
    }

    [IntegrationTheory]
    [InlineData("Routing", false)]
    [InlineData("Routing", true)]
    [InlineData("NetworkAnalysisUtilities", false)]
    [InlineData("NetworkAnalysisUtilities", true)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task ProtocolDisabledReservedIdentity_RemainsNotFoundWithoutSyntheticFallback(string service, bool byId)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        using var factory = CreateFactory(jobs);
        await SetIdentityAsync(factory, service, byId, disabled: true);
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-reader");
        using var metadata = await client.GetAsync($"/rest/services/{service}/GPServer?f=json");
        await AssertRestErrorAsync(metadata, 404);
        using var soapMetadata = await PostSoapAsync(client, service, "GetToolInfos");
        soapMetadata.StatusCode.Should().Be(HttpStatusCode.NotFound, await soapMetadata.Content.ReadAsStringAsync());
        using var restJob = await client.PostAsync($"/rest/services/{service}/GPServer/{AreaTool}/submitJob", Form());
        await AssertRestErrorAsync(restJob, 404);
        using var soapJob = await PostSoapAsync(client, service, "SubmitJob", Arguments);
        soapJob.StatusCode.Should().Be(HttpStatusCode.NotFound, await soapJob.Content.ReadAsStringAsync());
        jobs.ReceivedCalls().Should().BeEmpty();
    }

    private static WebApplicationFactory<Program> CreateFactory(IGeoprocessingJobService jobs)
        => ServiceRbacTestFixture.CreateFactory(
            layerCatalogFactory: static () => new RbacTestLayerCatalog(
                alphaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["alpha-reader"])),
            configureServices: services =>
        {
            services.RemoveAll<IGeoprocessingJobService>();
            services.AddSingleton(jobs);
            services.RemoveAll<IRoutingProvider>();
            services.AddScoped<IRoutingProvider, TestRoutingProvider>();
        });

    private static async Task SetIdentityAsync(WebApplicationFactory<Program> factory, string identity, bool byId, bool disabled = false)
    {
        var provider = (TestMetadataV2GraphProvider)factory.Services.GetRequiredService<IMetadataV2GraphProvider>();
        var graph = (await provider.GetCurrentAsync()).Graph;
        var previous = graph.Services.Single(service => service.Metadata.Name == "alpha");
        var replacement = previous with
        {
            Protocols = disabled ? [] : previous.Protocols,
            Metadata = previous.Metadata with
            {
                Id = byId ? identity : previous.Metadata.Id,
                Name = byId ? "ordinary-owned" : identity
            }
        };
        provider.SetGraph(graph with
        {
            Revision = graph.Revision + 1,
            Services = graph.Services.Select(service => service.Metadata.Id == previous.Metadata.Id ? replacement : service).ToArray(),
            Publications = graph.Publications.Select(publication => publication.ServiceId == previous.Metadata.Id
                ? publication with { ServiceId = replacement.Metadata.Id } : publication).ToArray()
        });
    }

    private static async Task AssertRestErrorAsync(HttpResponseMessage response, int expectedCode)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var body = JsonDocument.Parse(text);
        body.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(expectedCode);
        body.RootElement.TryGetProperty("tasks", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("jobId", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("results", out _).Should().BeFalse();
    }

    private static FormUrlEncodedContent Form() => new(new Dictionary<string, string>
    {
        ["wkb"] = Wkb, ["srid"] = "3857", ["f"] = "json"
    });

    private static Task<HttpResponseMessage> PostSoapAsync(HttpClient client, string service, string operation, string arguments = "")
        => client.PostAsync($"/services/{service}/GPServer", new StringContent(
            $"<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:tns=\"http://www.esri.com/schemas/ArcGIS/10.8\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><soap:Body><tns:{operation}>{arguments}</tns:{operation}></soap:Body></soap:Envelope>",
            Encoding.UTF8, "text/xml"));

    private static ExecutionJobRecord Job(string service) => new()
    {
        OperationId = "reserved-job",
        Status = ExecutionJobStatus.Running,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Spec = new ExecutionJobSpec
        {
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = "local",
            Kind = ExecutionJobKind.Geoprocessing,
            WorkloadName = "reserved-contract",
            Parameters = new Dictionary<string, string>
            {
                [GeoprocessingProtocolMetadataKeys.GPServerServiceId] = service,
                [GeoprocessingProtocolMetadataKeys.GPServerTaskName] = AreaTool
            }
        }
    };
}
