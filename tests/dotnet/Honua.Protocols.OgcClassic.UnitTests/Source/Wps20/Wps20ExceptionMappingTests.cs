// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Validation;
using Honua.Geoprocessing;
using Honua.Protocols.Ogc.Classic.Wps20;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
        context.Response.Headers["Honua-Retryable"].ToString().Should().Be("true");
        context.Response.Headers.RetryAfter.ToString().Should().Be("9");
        var body = await ReadBodyAsync(context);
        body.Should().Contain("exceptionCode=\"ServerBusy\"");
        body.Should().NotContain("NoApplicableCode");
        context.Response.ContentType.Should().Contain("xml");
    }

    [UnitTest]
    public async Task Execute_NonJobCallableProcess_ReturnsNoSuchProcess404()
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var catalog = new BuiltInProcessCatalog();

        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/xml";
        var xml = """
            <wps:Execute xmlns:wps="http://www.opengis.net/wps/2.0" xmlns:ows="http://www.opengis.net/ows/2.0"
                         service="WPS" version="2.0.0" response="document" mode="async">
              <ows:Identifier>source.postgis</ows:Identifier>
            </wps:Execute>
            """;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        context.Response.Body = new MemoryStream();

        var options = Substitute.For<IOptionsMonitor<Wps20Options>>();
        options.CurrentValue.Returns(new Wps20Options());
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        using var echo = new Wps20ConformanceEcho(options, environment);

        var result = await Wps20Endpoint.DispatchAsync(
            context,
            catalog,
            jobs,
            echo,
            NullLogger<Wps20Endpoint.Wps20EndpointLog>.Instance);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        var body = await ReadBodyAsync(context);
        body.Should().Contain("exceptionCode=\"NoSuchProcess\"");
    }

    [UnitTest]
    public async Task PostXml_DeeplyNestedDocument_Returns400()
    {
        var depth = RemoteXmlDocumentParser.DefaultMaxElementDepth + 20;
        var xml = "<wps:Execute xmlns:wps=\"http://www.opengis.net/wps/2.0\" xmlns:ows=\"http://www.opengis.net/ows/2.0\" service=\"WPS\" version=\"2.0.0\" response=\"document\" mode=\"async\">" +
                  "<ows:Identifier>echo</ows:Identifier><wps:Input id=\"a\"><wps:Data>" +
                  string.Concat(Enumerable.Repeat("<x>", depth)) +
                  "v" +
                  string.Concat(Enumerable.Repeat("</x>", depth)) +
                  "</wps:Data></wps:Input></wps:Execute>";

        var context = await DispatchPostXmlAsync(xml);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    public async Task PostXml_ExceedingOneMegabyteBodyLimit_Returns413()
    {
        var padding = new string('a', 1_048_577);
        var xml = $"<wps:GetCapabilities xmlns:wps=\"http://www.opengis.net/wps/2.0\" service=\"WPS\"><!--{padding}--></wps:GetCapabilities>";

        var context = await DispatchPostXmlAsync(xml);

        context.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [UnitTest]
    public async Task PostXml_Utf8BomPayload_ParsesCleanly()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <wps:GetCapabilities xmlns:wps="http://www.opengis.net/wps/2.0" service="WPS"/>
            """;
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(xml)).ToArray();

        var context = await DispatchPostXmlBytesAsync(bytes);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        (await ReadBodyAsync(context)).Should().Contain("Capabilities");
    }

    [UnitTest]
    public async Task PostXml_Utf16Payload_ParsesCleanly()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-16"?>
            <wps:GetCapabilities xmlns:wps="http://www.opengis.net/wps/2.0" service="WPS"/>
            """;
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();

        var context = await DispatchPostXmlBytesAsync(bytes);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        (await ReadBodyAsync(context)).Should().Contain("Capabilities");
    }

    private static Task<DefaultHttpContext> DispatchPostXmlAsync(string xml)
        => DispatchPostXmlBytesAsync(Encoding.UTF8.GetBytes(xml));

    private static async Task<DefaultHttpContext> DispatchPostXmlBytesAsync(byte[] bytes)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/xml";
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();

        var options = Substitute.For<IOptionsMonitor<Wps20Options>>();
        options.CurrentValue.Returns(new Wps20Options());
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        using var echo = new Wps20ConformanceEcho(options, environment);

        var result = await Wps20Endpoint.DispatchAsync(
            context,
            new BuiltInProcessCatalog(),
            Substitute.For<IGeoprocessingJobService>(),
            echo,
            NullLogger<Wps20Endpoint.Wps20EndpointLog>.Instance);
        await result.ExecuteAsync(context);
        return context;
    }

    private static async Task<DefaultHttpContext> DispatchAsync(IGeoprocessingJobService jobs, string jobId)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
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
