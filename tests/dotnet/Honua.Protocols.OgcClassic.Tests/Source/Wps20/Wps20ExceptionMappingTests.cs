// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Protocols.Ogc.Classic.Wps20;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wps20;

public sealed class Wps20ExceptionMappingTests
{
    [UnitTest]
    public async Task GetResult_PreconditionFailed_ReturnsResultNotReadyXml()
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        jobs.GetJobResultsAsync(Arg.Any<string>(), Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns<Task<AnalysisResultPackage>>(_ => throw new GeoprocessingPreconditionFailedException("Job 'job-1' has not reached a terminal state."));

        var context = await DispatchAsync(jobs, "job-1");

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        context.Response.ContentType.Should().Contain("xml");
        var body = await ReadBodyAsync(context);
        body.Should().Contain("exceptionCode=\"ResultNotReady\"");
        body.Should().Contain("job-1");
        body.Should().NotContain("NoApplicableCode");
    }

    [UnitTest]
    public async Task GetResult_Admission_ReturnsServerBusyWithRetryAfter()
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        jobs.GetJobResultsAsync(Arg.Any<string>(), Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns<Task<AnalysisResultPackage>>(_ => throw new GeoprocessingAdmissionException(
                ExecutionAdmissionOutcome.Throttled,
                ExecutionAdmissionDimension.Rate,
                "admission.rate",
                "The runtime is busy.",
                retryAfterSeconds: 9));

        var context = await DispatchAsync(jobs, "job-busy");

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers.RetryAfter.ToString().Should().Be("9");
        var body = await ReadBodyAsync(context);
        body.Should().Contain("exceptionCode=\"ServerBusy\"");
        body.Should().NotContain("NoApplicableCode");
        context.Response.ContentType.Should().Contain("xml");
    }

    private static async Task<DefaultHttpContext> DispatchAsync(IGeoprocessingJobService jobs, string jobId)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.QueryString = new QueryString(
            $"?service=WPS&version=2.0.0&request=GetResult&jobId={jobId}");
        context.Response.Body = new MemoryStream();

        var options = Substitute.For<IOptionsMonitor<Wps20Options>>();
        options.CurrentValue.Returns(new Wps20Options());
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        using var echo = new Wps20ConformanceEcho(options, environment);

        var result = await Wps20Endpoint.DispatchAsync(
            context,
            Substitute.For<IProcessCatalog>(),
            jobs,
            echo,
            NullLogger<Wps20Endpoint.Wps20EndpointLog>.Instance);
        await result.ExecuteAsync(context);
        return context;
    }

    private static async Task<string> ReadBodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
