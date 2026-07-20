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
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

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
        xml.Should().NotContain("Operation name=\"Dismiss\"").And.NotContain("jobControlOptions=\"sync-execute\"");
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
        xml.Should().Contain("<wps:ProcessOffering processVersion=\"1.0.0\"");
        xml.Should().Contain("<wps:Process>").And.NotContain("<wps:Process processVersion=");
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
    [Operation(Operations.ContractTesting)]
    [Endpoint("POST /wps")]
    public async Task GetCapabilities_XmlWithoutVersion_NegotiatesWps200()
    {
        const string body = "<wps:GetCapabilities service='WPS' xmlns:wps='http://www.opengis.net/wps/2.0'/>";
        using var content = new StringContent(body, Encoding.UTF8, "application/xml");

        var response = await _fixture.Client.PostAsync("/wps", content);
        var xml = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, xml);
        xml.Should().Contain("<wps:Capabilities").And.Contain("version=\"2.0.0\"");
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

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    [Endpoint("GET /wps")]
    public async Task ConformanceEcho_DefaultOff_IsNotAdvertised()
    {
        var response = await _fixture.Client.GetAsync("/wps?service=WPS&request=GetCapabilities&version=2.0.0");
        var xml = await response.Content.ReadAsStringAsync();

        xml.Should().NotContain("honua.cite.echo");

        var alias = await _fixture.Client.GetAsync("/wps?service=WPS&request=DescribeProcess&version=2.0.0&identifier=org.n52.javaps.test.EchoProcess");
        alias.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("POST /wps")]
    public async Task ConformanceEcho_DefaultOffAlias_UsesCanonicalAuthorization()
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        await using var fixture = new WebAppFixture().ReplaceService(jobs);
        await fixture.InitializeAsync();
        const string body = "<wps:Execute service='WPS' version='2.0.0' mode='sync' response='raw' xmlns:wps='http://www.opengis.net/wps/2.0' xmlns:ows='http://www.opengis.net/ows/2.0'><ows:Identifier>org.n52.javaps.test.EchoProcess</ows:Identifier><wps:Input id='literalInput'><wps:Data><wps:LiteralValue>blocked</wps:LiteralValue></wps:Data></wps:Input></wps:Execute>";

        var response = await fixture.Client.PostAsync("/wps", new StringContent(body, Encoding.UTF8, "application/xml"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await jobs.Received(1).EnsureCallerAuthorizedAsync(
            Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
            Honua.Core.Features.Authorization.Domain.OperatorResourceType.Process,
            Honua.Core.Features.Authorization.Domain.OperatorOperation.Execute,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Operation(Operations.SecurityTesting)]
    public void ConformanceEcho_FlagOutsideTestEnvironment_RemainsDisabled()
    {
        var options = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<Honua.Protocols.Ogc.Classic.Wps20.Wps20Options>>();
        options.CurrentValue.Returns(new Honua.Protocols.Ogc.Classic.Wps20.Wps20Options { EnableConformanceEcho = true });
        var environment = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        environment.EnvironmentName.Returns("Production");
        using var echo = new Honua.Protocols.Ogc.Classic.Wps20.Wps20ConformanceEcho(options, environment);

        echo.Enabled.Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /wps")]
    public async Task ConformanceEcho_DescribeAndAll_ExposeEtsDataCategories()
    {
        await using var fixture = CreateConformanceFixture();
        await fixture.InitializeAsync();

        var response = await fixture.Client.GetAsync("/wps?service=WPS&request=DescribeProcess&version=2.0.0&identifier=honua.cite.echo");
        var xml = await response.Content.ReadAsStringAsync();
        var all = await fixture.Client.GetStringAsync("/wps?service=WPS&request=DescribeProcess&version=2.0.0&identifier=ALL");

        response.StatusCode.Should().Be(HttpStatusCode.OK, xml);
        xml.Should().Contain("jobControlOptions=\"sync-execute async-execute\"");
        xml.Should().Contain("<wps:ProcessOffering processVersion=\"1.0.0\"");
        xml.Should().Contain("<wps:Process>").And.NotContain("<wps:Process processVersion=");
        xml.Should().Contain("<wps:LiteralData>").And.Contain("<LiteralDataDomain default=\"true\">");
        xml.Should().Contain("<ows:AnyValue/>").And.Contain("<ows:DataType ows:reference=");
        xml.Should().NotContain("<wps:LiteralDataDomain");
        xml.Should().Contain("<ows:DataType ows:reference=\"http://www.w3.org/2001/XMLSchema#string\">string</ows:DataType>");
        xml.Should().Contain("<wps:ComplexData>").And.Contain("<wps:BoundingBoxData>");
        all.Should().Contain("honua.cite.echo").And.Contain("geometry.buffer");
    }

    [Theory]
    [InlineData("document")]
    [InlineData("raw")]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /wps")]
    public async Task ConformanceEcho_SyncLiteral_ReturnsRequestedResponse(string responseForm)
    {
        await using var fixture = CreateConformanceFixture();
        await fixture.InitializeAsync();
        var body = $"<wps:Execute service='WPS' version='2.0.0' mode='sync' response='{responseForm}' xmlns:wps='http://www.opengis.net/wps/2.0' xmlns:ows='http://www.opengis.net/ows/2.0'><ows:Identifier>honua.cite.echo</ows:Identifier><wps:Input id='literalInput'><wps:Data><wps:LiteralValue>hello_literal</wps:LiteralValue></wps:Data></wps:Input><wps:Output id='literalOutput' transmission='value'/></wps:Execute>";

        var response = await fixture.Client.PostAsync("/wps", new StringContent(body, Encoding.UTF8, "application/xml"));
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("hello_literal");
        if (responseForm == "document")
        {
            content.Should().Contain("<wps:Result").And.Contain("<ows:Identifier>literalOutput</ows:Identifier>");
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /wps")]
    public async Task ConformanceEcho_SyncComplex_PreservesXmlPayload()
    {
        await using var fixture = CreateConformanceFixture();
        await fixture.InitializeAsync();
        const string body = "<wps:Execute service='WPS' version='2.0.0' mode='sync' response='document' xmlns:wps='http://www.opengis.net/wps/2.0' xmlns:ows='http://www.opengis.net/ows/2.0'><ows:Identifier>honua.cite.echo</ows:Identifier><wps:Input id='complexInput'><wps:Data><testElement>hello_complex</testElement></wps:Data></wps:Input><wps:Output id='complexOutput' transmission='value'/></wps:Execute>";

        var response = await fixture.Client.PostAsync("/wps", new StringContent(body, Encoding.UTF8, "application/xml"));
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("<testElement>hello_complex</testElement>");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /wps")]
    public async Task ConformanceEcho_Ets11Alias_UsesSameInertEchoBehavior()
    {
        await using var fixture = CreateConformanceFixture();
        await fixture.InitializeAsync();
        var description = await fixture.Client.GetStringAsync("/wps?service=WPS&request=DescribeProcess&version=2.0.0&identifier=org.n52.javaps.test.EchoProcess");
        const string body = "<wps:Execute service='WPS' version='2.0.0' mode='sync' response='raw' xmlns:wps='http://www.opengis.net/wps/2.0' xmlns:ows='http://www.opengis.net/ows/2.0'><ows:Identifier>org.n52.javaps.test.EchoProcess</ows:Identifier><wps:Input id='literalInput'><wps:Data><wps:LiteralValue>ets-alias</wps:LiteralValue></wps:Data></wps:Input><wps:Output id='literalOutput' transmission='value'/></wps:Execute>";

        var response = await fixture.Client.PostAsync("/wps", new StringContent(body, Encoding.UTF8, "application/xml"));
        var content = await response.Content.ReadAsStringAsync();

        description.Should().Contain("<ows:Identifier>org.n52.javaps.test.EchoProcess</ows:Identifier>");
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Be("ets-alias");
    }

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("POST /wps")]
    public async Task ConformanceEcho_AsyncResultAndReference_AreImmediateAndDereferenceable()
    {
        await using var fixture = CreateConformanceFixture();
        await fixture.InitializeAsync();
        const string body = "<wps:Execute service='WPS' version='2.0.0' mode='async' response='document' xmlns:wps='http://www.opengis.net/wps/2.0' xmlns:ows='http://www.opengis.net/ows/2.0'><ows:Identifier>honua.cite.echo</ows:Identifier><wps:Input id='literalInput'><wps:Data><wps:LiteralValue>async-aloha</wps:LiteralValue></wps:Data></wps:Input><wps:Output id='literalOutput' transmission='reference'/></wps:Execute>";

        var execute = await fixture.Client.PostAsync("/wps", new StringContent(body, Encoding.UTF8, "application/xml"));
        var executeXml = await execute.Content.ReadAsStringAsync();
        var jobId = System.Xml.Linq.XDocument.Parse(executeXml).Descendants(System.Xml.Linq.XName.Get("JobID", "http://www.opengis.net/wps/2.0")).Single().Value;
        var status = await fixture.Client.GetStringAsync($"/wps?service=WPS&request=GetStatus&version=2.0.0&jobId={jobId}");
        var result = await fixture.Client.GetStringAsync($"/wps?service=WPS&request=GetResult&version=2.0.0&jobId={jobId}");
        var resultDocument = System.Xml.Linq.XDocument.Parse(result);
        System.Xml.Linq.XNamespace xlink = "http://www.w3.org/1999/xlink";
        var href = resultDocument.Descendants(System.Xml.Linq.XName.Get("Reference", "http://www.opengis.net/wps/2.0")).Single().Attribute(xlink + "href")!.Value;
        var referenced = await fixture.Client.GetStringAsync(href);

        status.Should().Contain("<wps:Status>Succeeded</wps:Status>");
        result.Should().Contain("<ows:Identifier>literalOutput</ows:Identifier>");
        referenced.Should().Be("async-aloha");
    }

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("POST /wps")]
    public async Task ConformanceEcho_ReferenceToPrivateNetwork_IsRejected()
    {
        await using var fixture = CreateConformanceFixture(allowedHosts: ["localhost"]);
        await fixture.InitializeAsync();
        const string body = "<wps:Execute service='WPS' version='2.0.0' mode='sync' response='document' xmlns:wps='http://www.opengis.net/wps/2.0' xmlns:ows='http://www.opengis.net/ows/2.0' xmlns:xlink='http://www.w3.org/1999/xlink'><ows:Identifier>honua.cite.echo</ows:Identifier><wps:Input id='literalInput'><wps:Reference mimeType='text/plain' xlink:href='https://localhost/private'/></wps:Input><wps:Output id='literalOutput' transmission='value'/></wps:Execute>";

        var response = await fixture.Client.PostAsync("/wps", new StringContent(body, Encoding.UTF8, "application/xml"));
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("public HTTPS URL");
    }

    [Theory]
    [InlineData("<wps:Output id='literalOutput' transmission='value'/><wps:Output id='literalOutput' transmission='value'/>")]
    [InlineData("<wps:Output id='literalOutput' transmission='invalid'/>")]
    [InlineData("<wps:Output id='complexOutput' transmission='value'/>")]
    [Operation(Operations.ContractTesting)]
    [Endpoint("POST /wps")]
    public async Task ConformanceEcho_InvalidOutputContract_ReturnsOwsException(string outputs)
    {
        await using var fixture = CreateConformanceFixture();
        await fixture.InitializeAsync();
        var body = $"<wps:Execute service='WPS' version='2.0.0' mode='sync' response='document' xmlns:wps='http://www.opengis.net/wps/2.0' xmlns:ows='http://www.opengis.net/ows/2.0'><ows:Identifier>honua.cite.echo</ows:Identifier><wps:Input id='literalInput'><wps:Data><wps:LiteralValue>aloha</wps:LiteralValue></wps:Data></wps:Input>{outputs}</wps:Execute>";

        var response = await fixture.Client.PostAsync("/wps", new StringContent(body, Encoding.UTF8, "application/xml"));
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("ExceptionReport").And.Contain("locator=\"output\"");
    }

    [Theory]
    [InlineData("invalidOutput", "value", "sync", "document", "output")]
    [InlineData("literalOutput", "invalid", "sync", "document", "output")]
    [InlineData("literalOutput", "value", "auto", "document", "mode")]
    [InlineData("literalOutput", "value", "sync", "invalid", "response")]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("POST /wps")]
    public async Task ConformanceEcho_InvalidContract_DoesNotResolveReference(
        string outputId,
        string transmission,
        string mode,
        string responseForm,
        string locator)
    {
        await using var fixture = CreateConformanceFixture(allowedHosts: ["does-not-resolve.invalid"]);
        await fixture.InitializeAsync();
        var body = $"<wps:Execute service='WPS' version='2.0.0' mode='{mode}' response='{responseForm}' xmlns:wps='http://www.opengis.net/wps/2.0' xmlns:ows='http://www.opengis.net/ows/2.0' xmlns:xlink='http://www.w3.org/1999/xlink'><ows:Identifier>honua.cite.echo</ows:Identifier><wps:Input id='literalInput'><wps:Reference mimeType='text/plain' xlink:href='https://does-not-resolve.invalid/value'/></wps:Input><wps:Output id='{outputId}' transmission='{transmission}'/></wps:Execute>";

        var response = await fixture.Client.PostAsync("/wps", new StringContent(body, Encoding.UTF8, "application/xml"));
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain($"locator=\"{locator}\"");
        content.Should().NotContain("could not be resolved");
    }

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("POST /wps")]
    public async Task ConformanceEcho_UnresolvableReference_ReturnsOwsException()
    {
        await using var fixture = CreateConformanceFixture(allowedHosts: ["does-not-resolve.invalid"]);
        await fixture.InitializeAsync();
        const string body = "<wps:Execute service='WPS' version='2.0.0' mode='sync' response='document' xmlns:wps='http://www.opengis.net/wps/2.0' xmlns:ows='http://www.opengis.net/ows/2.0' xmlns:xlink='http://www.w3.org/1999/xlink'><ows:Identifier>honua.cite.echo</ows:Identifier><wps:Input id='literalInput'><wps:Reference mimeType='text/plain' xlink:href='https://does-not-resolve.invalid/value'/></wps:Input><wps:Output id='literalOutput' transmission='value'/></wps:Execute>";

        var response = await fixture.Client.PostAsync("/wps", new StringContent(body, Encoding.UTF8, "application/xml"));
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("ExceptionReport").And.Contain("could not be resolved");
    }

    [Theory]
    [InlineData("sync", "document", "value")]
    [InlineData("auto", "document", "value")]
    [InlineData("async", "raw", "value")]
    [InlineData("async", "document", "reference")]
    [Operation(Operations.ContractTesting)]
    [Endpoint("POST /wps")]
    public async Task CanonicalExecute_UnsupportedContract_IsRejectedBeforeSubmission(string mode, string responseForm, string transmission)
    {
        var catalog = Substitute.For<IProcessCatalog>();
        catalog.GetProcess("canonical.test").Returns(new ProcessDefinition
        {
            ProcessId = "canonical.test",
            Title = "Canonical",
            Description = "Test process.",
            Category = "test",
            Parameters = [],
            OutputArtifactKinds = []
        });
        var jobs = Substitute.For<IGeoprocessingJobService>();
        await using var fixture = new WebAppFixture().ReplaceService(catalog).ReplaceService(jobs);
        await fixture.InitializeAsync();
        var body = $"<wps:Execute service='WPS' version='2.0.0' mode='{mode}' response='{responseForm}' xmlns:wps='http://www.opengis.net/wps/2.0' xmlns:ows='http://www.opengis.net/ows/2.0'><ows:Identifier>canonical.test</ows:Identifier><wps:Output id='result' transmission='{transmission}'/></wps:Execute>";

        var response = await fixture.Client.PostAsync("/wps", new StringContent(body, Encoding.UTF8, "application/xml"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await jobs.DidNotReceive().SubmitJobAsync(
            Arg.Any<AnalysisPlan>(),
            Arg.Any<string?>(),
            Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    [Endpoint("GET /wps")]
    public async Task ConformanceEcho_ConfiguredPublicBaseUrl_IsUsedForLinks()
    {
        await using var fixture = CreateConformanceFixture(publicBaseUrl: "https://cite.example.test/root");
        await fixture.InitializeAsync();

        var capabilities = await fixture.Client.GetStringAsync("/wps?service=WPS&request=GetCapabilities&version=2.0.0");
        const string body = "<wps:Execute service='WPS' version='2.0.0' mode='sync' response='document' xmlns:wps='http://www.opengis.net/wps/2.0' xmlns:ows='http://www.opengis.net/ows/2.0'><ows:Identifier>honua.cite.echo</ows:Identifier><wps:Input id='literalInput'><wps:Data><wps:LiteralValue>aloha</wps:LiteralValue></wps:Data></wps:Input><wps:Output id='literalOutput' transmission='reference'/></wps:Execute>";
        var result = await fixture.Client.PostAsync("/wps", new StringContent(body, Encoding.UTF8, "application/xml"));
        var resultXml = await result.Content.ReadAsStringAsync();

        capabilities.Should().Contain("https://cite.example.test/root/wps");
        resultXml.Should().Contain("https://cite.example.test/root/wps/conformance/results/");
        capabilities.Should().NotContain("http://localhost/wps");
        Honua.Protocols.Ogc.Classic.Wps20.Wps20ConformanceEcho.MaxConcurrentReferenceFetches.Should().Be(4);
    }

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /wps")]
    public async Task ConformanceEcho_NoPublicBaseUrl_EmitsRelativeLinksAndIgnoresHost()
    {
        await using var fixture = CreateConformanceFixture();
        await fixture.InitializeAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/wps?service=WPS&request=GetCapabilities&version=2.0.0");
        request.Headers.Host = "hostile.example.test";

        var response = await fixture.Client.SendAsync(request);
        var capabilities = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, capabilities);
        capabilities.Should().Contain("xlink:href=\"/wps\"");
        capabilities.Should().NotContain("hostile.example.test").And.NotContain("http://localhost/wps");
    }

    private static WebAppFixture CreateConformanceFixture(
        string? environment = null,
        string? publicBaseUrl = null,
        params string[] allowedHosts) =>
        new WebAppFixture().ConfigureWebHost(builder =>
        {
            if (environment is not null)
            {
                builder.UseEnvironment(environment);
            }
            builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HONUA_WPS_CITE_ECHO_PROCESS_ENABLED"] = "true",
                ["HONUA_CITE_WPS20_ECHO_PROCESS_ID"] = "honua.cite.echo",
                ["Wps20:ConformanceReferenceAllowedHosts:0"] = allowedHosts.FirstOrDefault() ?? "raw.githubusercontent.com",
                ["Wps20:PublicBaseUrl"] = publicBaseUrl,
                ["PUBLIC_BASE_URL"] = publicBaseUrl
            }));
        });
}
