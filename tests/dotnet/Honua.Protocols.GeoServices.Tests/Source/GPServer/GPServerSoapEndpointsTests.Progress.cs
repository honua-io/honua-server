// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

public sealed partial class GPServerSoapEndpointsTests
{
    [IntegrationTheory]
    [InlineData("false")]
    [InlineData("true")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData(" true ")]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    [InterfaceOperation(TestProtocols.GPServer, "GetJobStatus")]
    public async Task GetJobStatus_ProgressHint_PreservesScalarStatusAndSeparateMessages(string progressHint)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        jobs.GetJobAsync("soap-job", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(SoapJob() with { Status = ExecutionJobStatus.Succeeded, CurrentPhase = "Completed" });
        using var factory = ServiceRbacTestFixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<IGeoprocessingJobService>();
            services.AddSingleton(jobs);
        });
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-reader");
        // Match Pro's measured GetProgressMsg=false request; the response contract
        // remains scalar even for the deliberately tolerated true hint.
        using var response = await PostAsync(client, "GetJobStatus",
            $"<JobID>soap-job</JobID><GetProgressMsg>{progressHint}</GetProgressMsg>");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var operation = XDocument.Parse(body).Descendants(XName.Get("GetJobStatusResponse", ArcGis)).Single();
        operation.Elements().Should().ContainSingle().Which.Name.Should().Be(XName.Get("Result"));
        operation.Element("Result")!.Value.Should().Be("esriJobSucceeded");
        operation.Element("Result")!.HasElements.Should().BeFalse();

        using var messages = await PostAsync(client, "GetJobMessages", "<JobID>soap-job</JobID>");
        messages.StatusCode.Should().Be(HttpStatusCode.OK);
        XDocument.Parse(await messages.Content.ReadAsStringAsync()).Descendants("MessageDesc")
            .Should().ContainSingle().Which.Value.Should().Be("Completed");
        await jobs.DidNotReceiveWithAnyArgs().GetJobResultsAsync(default!, default!, default);
    }

    [IntegrationTheory]
    [InlineData("<GetProgressMsg>yes</GetProgressMsg>")]
    [InlineData("<GetProgressMsg>True</GetProgressMsg>")]
    [InlineData("<GetProgressMsg>2</GetProgressMsg>")]
    [InlineData("<GetProgressMsg/>")]
    [InlineData("<GetProgressMsg xsi:nil=\"true\"/>")]
    [InlineData("<GetProgressMsg><Value>false</Value></GetProgressMsg>")]
    [InlineData("<GetProgressMsg>false</GetProgressMsg><GetProgressMsg>true</GetProgressMsg>")]
    [InlineData("<GetProgressMsg>false</GetProgressMsg><Unknown/>")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task GetJobStatus_InvalidProgressHint_ReturnsSoapFaultBeforeJobLookup(string arguments)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        using var factory = ServiceRbacTestFixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<IGeoprocessingJobService>();
            services.AddSingleton(jobs);
        });
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-reader");
        using var response = await PostAsync(client, "GetJobStatus", "<JobID>soap-job</JobID>" + arguments);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        XDocument.Parse(body).Descendants(XName.Get("Fault", Soap11)).Should().ContainSingle();
        await jobs.DidNotReceiveWithAnyArgs().GetJobAsync(default!, default!, default);
    }

    [IntegrationTheory]
    [InlineData("GetJobMessages")]
    [InlineData("GetJobToolName")]
    [InlineData("GetJobResult")]
    [InlineData("CancelJob")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task OtherJobOperations_ProgressHint_RemainsRejected(string operation)
    {
        using var client = ServiceRbacTestFixture.CreateClient(fixture.Factory, "alpha-reader");
        using var response = await PostAsync(client, operation,
            "<JobID>soap-job</JobID><GetProgressMsg>false</GetProgressMsg>");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        XDocument.Parse(body).Descendants(XName.Get("Fault", Soap11)).Should().ContainSingle();
    }
}
