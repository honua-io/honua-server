// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Server.Features.DataEnrichment;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.DataEnrichment;

public sealed class DataEnrichmentErrorMappingTests
{
    [UnitTest]
    public async Task MapEnrichmentExecutionFailure_ArgumentExceptionWithUnsafeText_Returns400WithSanitizedFallback()
    {
        var (statusCode, body) = await ExecuteAsync(
            new ArgumentException("Npgsql syntax error near Password=secret (Parameter 'where')"));

        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("Invalid data enrichment request.");
        body.Should().NotContain("Npgsql");
        body.Should().NotContain("Password");
        body.Should().NotContain("Parameter");
    }

    [UnitTest]
    public async Task MapEnrichmentExecutionFailure_ArgumentExceptionWithSafeText_StripsParameterSuffix()
    {
        var (statusCode, body) = await ExecuteAsync(
            new ArgumentException("Distance must be positive.", "distanceMeters"));

        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("Distance must be positive.");
        body.Should().NotContain("Parameter");
    }

    [UnitTest]
    public async Task MapEnrichmentExecutionFailure_RuntimeException_Returns500Not400AndHidesInternalMessage()
    {
        var (statusCode, body) = await ExecuteAsync(
            new InvalidOperationException("internal database failure details"));

        statusCode.Should().Be(StatusCodes.Status500InternalServerError);
        body.Should().Contain("Data enrichment request failed.");
        body.Should().NotContain("internal database failure details");
    }

    [UnitTest]
    public async Task MapEnrichmentExecutionFailure_ServiceUnavailable_Returns503WithRetryHeaders()
    {
        var context = CreateHttpContext();
        var result = DataEnrichmentRequestHandlers.MapEnrichmentExecutionFailure(
            context,
            new ServiceUnavailableException("database pool saturated", retryAfterSeconds: 12));

        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers["Honua-Retryable"].ToString().Should().Be("true");
        context.Response.Headers.RetryAfter.ToString().Should().Be("12");
    }

    [UnitTest]
    public async Task MapEnrichmentExecutionFailure_TimeoutException_Returns408Not400()
    {
        var (statusCode, _) = await ExecuteAsync(new TimeoutException("query timed out"));

        statusCode.Should().Be(StatusCodes.Status408RequestTimeout);
    }

    private static async Task<(int StatusCode, string Body)> ExecuteAsync(Exception exception)
    {
        var context = CreateHttpContext();
        var result = DataEnrichmentRequestHandlers.MapEnrichmentExecutionFailure(context, exception);
        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        using var _ = JsonDocument.Parse(body);
        return (context.Response.StatusCode, body);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            TraceIdentifier = "enrich-corr-1"
        };
        context.Request.Path = "/api/enrich";
        context.Response.Body = new MemoryStream();
        return context;
    }
}
