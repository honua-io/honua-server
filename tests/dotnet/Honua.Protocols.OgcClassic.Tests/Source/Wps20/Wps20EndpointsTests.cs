// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wps20;

[Collection("Database")]
[Protocol(TestProtocols.Wps202)]
public sealed class Wps20EndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /wps")]
    [InterfaceOperation(TestProtocols.Wps202, "GetCapabilities")]
    public async Task GetCapabilities_AdvertisesAsyncExecutionForCanonicalProcesses()
    {
        var response = await _fixture.Client.GetAsync("/wps?service=WPS&request=GetCapabilities&version=2.0.0");
        var xml = await response.Content.ReadAsStringAsync();
        var document = XDocument.Parse(xml);
        XNamespace wps = "http://www.opengis.net/wps/2.0";
        XNamespace ows = "http://www.opengis.net/ows/2.0";
        var canonicalProcess = document
            .Descendants(wps + "ProcessSummary")
            .Single(summary => summary.Element(ows + "Identifier")?.Value == "geometry.buffer");

        response.StatusCode.Should().Be(HttpStatusCode.OK, xml);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        xml.Should().Contain("<wps:Capabilities");
        canonicalProcess.Attribute("jobControlOptions")?.Value.Should().Be("async-execute");
        xml.Should().Contain("processVersion=\"1.0.0\"");
        xml.Should().NotContain("Operation name=\"Dismiss\"");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /wps")]
    [InterfaceOperation(TestProtocols.Wps202, "DescribeProcess")]
    public async Task DescribeProcess_Kvp_ReturnsNamespaceCorrectDescription()
    {
        var response = await _fixture.Client.GetAsync("/wps?service=WPS&request=DescribeProcess&version=2.0.0&identifier=geometry.buffer");
        var xml = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, xml);
        xml.Should().Contain($"xmlns:wps=\"http://www.opengis.net/wps/2.0\"");
        xml.Should().Contain("<ows:Identifier>geometry.buffer</ows:Identifier>");
        xml.Should().Contain("<ows:Identifier>result</ows:Identifier>");
        xml.Should().Contain("<wps:Process processVersion=\"1.0.0\"");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /wps")]
    [InterfaceOperation(TestProtocols.Wps202, "Execute")]
    public async Task Execute_XmlInputId_SubmitsCanonicalPlan()
    {
        AnalysisPlan? submittedPlan = null;
        var catalog = Substitute.For<IProcessCatalog>();
        catalog.GetProcess("echo").Returns(new ProcessDefinition
        {
            ProcessId = "echo",
            Title = "Echo",
            Description = "Echoes a literal value.",
            Category = "test",
            Parameters = [new ProcessParameterSpec { Name = "value", DisplayName = "Value", Description = "Value to echo.", ValueType = ProcessParameterValueType.Text, Required = true }],
            OutputArtifactKinds = []
        });
        var jobs = Substitute.For<IGeoprocessingJobService>();
        jobs.SubmitJobAsync(Arg.Do<AnalysisPlan>(plan => submittedPlan = plan), Arg.Any<string?>(), Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(new ExecutionJobRecord
            {
                OperationId = "wps-job-1",
                Status = ExecutionJobStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Spec = new ExecutionJobSpec { TargetKind = default, Backend = "test", Kind = ExecutionJobKind.Geoprocessing, WorkloadName = "echo" }
            });
        await using var fixture = new WebAppFixture().ReplaceService(catalog).ReplaceService(jobs);
        await fixture.InitializeAsync();
        const string body = "<wps:Execute service='WPS' version='2.0.0' xmlns:wps='http://www.opengis.net/wps/2.0' xmlns:ows='http://www.opengis.net/ows/2.0'><ows:Identifier>echo</ows:Identifier><wps:Input id='value'><wps:Data><wps:LiteralValue>aloha</wps:LiteralValue></wps:Data></wps:Input></wps:Execute>";

        using var content = new StringContent(body, Encoding.UTF8, "application/xml");
        var response = await fixture.Client.PostAsync("/wps", content);
        var xml = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, xml);
        submittedPlan.Should().NotBeNull();
        submittedPlan!.Steps.Single().Inputs["value"].Should().Be("aloha");
    }

    [Theory]
    [InlineData("OTHER", "2.0.0", "service")]
    [InlineData("WPS", "1.0.0", "version")]
    [Operation(Operations.ContractTesting)]
    [Endpoint("GET /wps")]
    public async Task Kvp_WrongServiceOrVersion_ReturnsProtocolException(string service, string version, string locator)
    {
        var response = await _fixture.Client.GetAsync($"/wps?service={service}&request=GetCapabilities&version={version}");
        var xml = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, xml);
        xml.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        xml.Should().Contain($"locator=\"{locator}\"");
    }

    [Theory]
    [InlineData("OTHER", "2.0.0", "service")]
    [InlineData("WPS", "2.0.2", "version")]
    [Operation(Operations.ContractTesting)]
    [Endpoint("POST /wps")]
    public async Task Xml_WrongServiceOrVersion_ReturnsProtocolException(string service, string version, string locator)
    {
        var body = $"<wps:GetCapabilities service='{service}' version='{version}' xmlns:wps='http://www.opengis.net/wps/2.0'/>";
        using var content = new StringContent(body, Encoding.UTF8, "application/xml");

        var response = await _fixture.Client.PostAsync("/wps", content);
        var xml = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, xml);
        xml.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        xml.Should().Contain($"locator=\"{locator}\"");
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /wps")]
    [InterfaceOperation(TestProtocols.Wps202, "GetResult")]
    public async Task GetResult_MapsAdvertisedResultIdentifierAndLiteralMediaType()
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        jobs.GetJobResultsAsync("wps-job-result", Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisResultPackage
            {
                ResultPackageId = "result-1",
                Status = GeoprocessingWorkflowStatus.Completed,
                Summary = new ResultSummary { Title = "buffer complete" },
                Provenance = null!
            });
        await using var fixture = new WebAppFixture().ReplaceService(jobs);
        await fixture.InitializeAsync();

        var response = await fixture.Client.GetAsync("/wps?service=WPS&request=GetResult&version=2.0.0&jobId=wps-job-result");
        var xml = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, xml);
        xml.Should().Contain("<ows:Identifier>result</ows:Identifier>");
        xml.Should().Contain("<wps:Data mimeType=\"text/plain\">");
        xml.Should().Contain("<wps:LiteralValue>buffer complete</wps:LiteralValue>");
    }

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("POST /wps")]
    [InterfaceOperation(TestProtocols.Wps202, "Execute")]
    public async Task Execute_XmlWithDtd_IsRejectedWithoutEntityExpansion()
    {
        const string body = "<!DOCTYPE x [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><wps:Execute xmlns:wps='http://www.opengis.net/wps/2.0' xmlns:ows='http://www.opengis.net/ows/2.0'><ows:Identifier>&xxe;</ows:Identifier></wps:Execute>";
        using var content = new StringContent(body, Encoding.UTF8, "application/xml");

        var response = await _fixture.Client.PostAsync("/wps", content);
        var xml = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, xml);
        xml.Should().Contain("ExceptionReport").And.Contain("prohibited constructs");
    }

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /wps")]
    [InterfaceOperation(TestProtocols.Wps202, "GetStatus")]
    public async Task GetStatus_UnknownJob_ReturnsOwsExceptionReport()
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        jobs.GetJobAsync("missing-job", Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ExecutionJobRecord>(new GeoprocessingNotFoundException("Job 'missing-job' not found.")));
        await using var fixture = new WebAppFixture().ReplaceService(jobs);
        await fixture.InitializeAsync();

        var response = await fixture.Client.GetAsync("/wps?service=WPS&request=GetStatus&version=2.0.0&jobId=missing-job");
        var xml = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, xml);
        xml.Should().Contain("exceptionCode=\"NoSuchJob\"");
        xml.Should().Contain("xmlns:ows=\"http://www.opengis.net/ows/2.0\"");
    }
}
