// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

public sealed partial class GPServerSoapEndpointsTests
{
    private const string AreaTool = "Honua_67656F6D657472792E61726561";
    private const string AreaArguments = """
        <ToolName>Honua_67656F6D657472792E61726561</ToolName>
        <Values xsi:type="tns:GPValues"><GPValue xsi:type="tns:GPString"><Value>AQEAAAAAAAAAAAAAAAAAAAAAAAAA</Value></GPValue>
        <GPValue xsi:type="tns:GPLong"><Value>3857</Value></GPValue></Values>
        """;

    [IntegrationTheory]
    [InlineData("SubmitJob")]
    [InlineData("Execute")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task SoapExecution_ProcessAuthorization_PrecedesParameterValidation(string operation)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        jobs.EnsureCallerAuthorizedAsync(Arg.Any<ClaimsPrincipal>(), OperatorResourceType.Process,
            OperatorOperation.Execute, Arg.Any<CancellationToken>())
            .ThrowsAsync(new GeoprocessingAuthorizationException(false, "Execution is forbidden."));
        using var factory = ServiceRbacTestFixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<IGeoprocessingJobService>();
            services.AddSingleton(jobs);
        });
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-reader");
        using var response = await PostAsync(client, operation, "<Malformed/>");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        await jobs.DidNotReceiveWithAnyArgs().SubmitJobAsync(default!, default, default!, default, default);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    [InterfaceOperation(TestProtocols.GPServer, "SubmitJob")]
    public async Task SoapSubmitJob_PublishedName_UsesCanonicalPlanAndBinding()
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        AnalysisPlan? submittedPlan = null;
        IReadOnlyDictionary<string, string>? binding = null;
        jobs.SubmitJobAsync(Arg.Any<AnalysisPlan>(), Arg.Any<string?>(), Arg.Any<ClaimsPrincipal>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                submittedPlan = call.Arg<AnalysisPlan>();
                binding = call.Arg<IReadOnlyDictionary<string, string>?>();
                return Task.FromResult(SoapJob());
            });
        using var factory = ServiceRbacTestFixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<IGeoprocessingJobService>();
            services.AddSingleton(jobs);
        });
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-reader");
        using var response = await PostAsync(client, "SubmitJob", AreaArguments);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        XDocument.Parse(text).Descendants("Result").Single().Value.Should().Be("soap-job");
        submittedPlan.Should().NotBeNull();
        var step = submittedPlan!.Steps.Should().ContainSingle().Subject;
        step.ProcessId.Should().Be("geometry.area");
        step.Inputs["wkb"].Should().Be("AQEAAAAAAAAAAAAAAAAAAAAAAAAA");
        step.Inputs["srid"].Should().Be("3857");
        binding![GeoprocessingProtocolMetadataKeys.GPServerServiceId].Should().Be("alpha");
        binding[GeoprocessingProtocolMetadataKeys.GPServerTaskName].Should().Be(AreaTool);
    }

    [IntegrationTheory]
    [InlineData("GetJobStatus")]
    [InlineData("GetJobMessages")]
    [InlineData("GetJobResult")]
    [InlineData("CancelJob")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task SoapJobOperation_CrossServiceBinding_CannotReadOrCancel(string operation)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        jobs.GetJobAsync("soap-job", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(SoapJob("beta"));
        var terminal = Substitute.For<IGeoprocessingJobTerminalService>();
        using var factory = ServiceRbacTestFixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<IGeoprocessingJobService>();
            services.AddSingleton(jobs);
            services.RemoveAll<IGeoprocessingJobTerminalService>();
            services.AddSingleton(terminal);
        });
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-reader");
        using var response = await PostAsync(client, operation, "<JobID>soap-job</JobID>");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
        await jobs.DidNotReceiveWithAnyArgs().GetJobResultsAsync(default!, default!, default);
        await terminal.DidNotReceiveWithAnyArgs().CancelAsync(default!, default!, default, default);
    }

    [IntegrationTheory]
    [InlineData("GetJobStatus")]
    [InlineData("GetJobMessages")]
    [InlineData("GetJobResult")]
    [InlineData("CancelJob")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task SoapJobOperation_OwnershipDenial_IsPropagated(string operation)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        jobs.GetJobAsync("soap-job", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new GeoprocessingAuthorizationException(false, "Job belongs to another caller."));
        using var factory = ServiceRbacTestFixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<IGeoprocessingJobService>();
            services.AddSingleton(jobs);
        });
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-reader");
        using var response = await PostAsync(client, operation, "<JobID>soap-job</JobID>");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
    }

    [IntegrationTheory]
    [InlineData("Unsupported", HttpStatusCode.PreconditionFailed)]
    [InlineData("Unconfirmed", HttpStatusCode.PreconditionFailed)]
    [InlineData("AlreadyTerminal", HttpStatusCode.PreconditionFailed)]
    [InlineData("Timeout", HttpStatusCode.RequestTimeout)]
    [InlineData("Cancelled", HttpStatusCode.OK)]
    [Operation(Operations.Delete)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    [InterfaceOperation(TestProtocols.GPServer, "CancelJob")]
    public async Task SoapCancelJob_UsesTruthfulCanonicalCancellation(string outcome, HttpStatusCode expected)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        jobs.GetJobAsync("soap-job", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(SoapJob());
        var terminal = Substitute.For<IGeoprocessingJobTerminalService>();
        terminal.CancelAsync("soap-job", Arg.Any<ClaimsPrincipal>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new GeoprocessingCancelResult(Enum.Parse<GeoprocessingCancelOutcome>(outcome), SoapJob() with { Status = ExecutionJobStatus.Cancelled }));
        using var factory = ServiceRbacTestFixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<IGeoprocessingJobService>();
            services.AddSingleton(jobs);
            services.RemoveAll<IGeoprocessingJobTerminalService>();
            services.AddSingleton(terminal);
        });
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-reader");
        using var response = await PostAsync(client, "CancelJob", "<JobID>soap-job</JobID>");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, body);
        if (expected != HttpStatusCode.OK)
        {
            XDocument.Parse(body).Descendants(XName.Get("Fault", Soap11)).Should().ContainSingle();
            XDocument.Parse(body).Descendants("Result").Should().BeEmpty();
        }
        await terminal.Received(1).CancelAsync("soap-job", Arg.Any<ClaimsPrincipal>(), TimeSpan.FromSeconds(30), Arg.Any<CancellationToken>());
    }

    [IntegrationTheory]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    [InterfaceOperation(TestProtocols.GPServer, "Execute")]
    public async Task SoapExecute_TerminalFailure_ReturnsFaultInsteadOfSuccessfulEmptyResults(string outcome)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        jobs.SubmitJobAsync(Arg.Any<AnalysisPlan>(), Arg.Any<string?>(), Arg.Any<ClaimsPrincipal>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>()).Returns(SoapJob());
        var terminal = Substitute.For<IGeoprocessingJobTerminalService>();
        terminal.WaitForResultAsync("soap-job", Arg.Any<ClaimsPrincipal>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new GeoprocessingTerminalResult(Enum.Parse<GeoprocessingTerminalResultOutcome>(outcome),
                SoapJob() with { Status = Enum.Parse<ExecutionJobStatus>(outcome), ErrorMessage = "Deliberate worker failure." }));
        using var factory = ServiceRbacTestFixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<IGeoprocessingJobService>();
            services.AddSingleton(jobs);
            services.RemoveAll<IGeoprocessingJobTerminalService>();
            services.AddSingleton(terminal);
        });
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-reader");
        using var response = await PostAsync(client, "Execute", AreaArguments);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, body);
        XDocument.Parse(body).Descendants(XName.Get("Fault", Soap11)).Should().ContainSingle();
        XDocument.Parse(body).Descendants("Result").Should().BeEmpty();
    }

    [IntegrationTheory]
    [InlineData("GetJobStatus", "esriJobExecuting")]
    [InlineData("GetJobMessages", "")]
    [InlineData("GetJobToolName", AreaTool)]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    [InterfaceOperation(TestProtocols.GPServer, "GetJobMessages")]
    [InterfaceOperation(TestProtocols.GPServer, "GetJobToolName")]
    public async Task SoapJobOperation_ValidBinding_ReturnsCanonicalStatus(string operation, string expected)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        jobs.GetJobAsync("soap-job", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(SoapJob());
        using var factory = ServiceRbacTestFixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<IGeoprocessingJobService>();
            services.AddSingleton(jobs);
        });
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-reader");
        using var response = await PostAsync(client, operation, "<JobID>soap-job</JobID>");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        XDocument.Parse(body).Descendants("Result").Single().Value.Should().Be(expected);
    }

    private static ExecutionJobRecord SoapJob(string serviceId = "alpha") => new()
    {
        OperationId = "soap-job",
        Status = ExecutionJobStatus.Running,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Spec = new ExecutionJobSpec
        {
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = "local",
            Kind = ExecutionJobKind.Geoprocessing,
            WorkloadName = "soap-contract",
            Parameters = new Dictionary<string, string>
            {
                [GeoprocessingProtocolMetadataKeys.GPServerServiceId] = serviceId,
                [GeoprocessingProtocolMetadataKeys.GPServerTaskName] = AreaTool
            }
        }
    };
}
