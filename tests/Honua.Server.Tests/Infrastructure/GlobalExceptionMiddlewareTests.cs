// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Infrastructure;

[Collection("GlobalExceptionMiddleware")]
public class GlobalExceptionMiddlewareTests : IDisposable
{
    private readonly TestServer _server;
    private readonly HttpClient _client;

    public GlobalExceptionMiddlewareTests()
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddLogging();
                    })
                    .Configure(app =>
                    {
                        app.UseCorrelationId(); // Need this for correlation ID in exceptions
                        app.UseGlobalExceptionHandling();
                        app.Run(async context =>
                        {
                            var path = context.Request.Path;
                            if (path == "/throw-argument")
                                throw new ArgumentException("Invalid argument provided");
                            if (path == "/throw-unauthorized")
                                throw new UnauthorizedAccessException("Access denied");
                            if (path == "/throw-timeout")
                                throw new TimeoutException("Operation timed out");
                            if (path == "/throw-general")
                                throw new InvalidOperationException("Something went wrong");
                            if (path == "/throw-unhandled")
                                throw new NotImplementedException("This is not implemented");

                            await context.Response.WriteAsync("OK");
                        });
                    });
            });

        var host = builder.Build();
        host.Start();

        _server = host.GetTestServer();
        _client = _server.CreateClient();
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_ArgumentException_Returns400BadRequest()
    {
        // Act
        var response = await _client.GetAsync("/throw-argument");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(content);

        error.Should().NotBeNull();
        error!.Error.Code.Should().Be(400);
        error.Error.Message.Should().Be("Bad Request");
        error.Error.Details.Should().Contain("Invalid request parameters.");
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_UnauthorizedAccessException_Returns401Unauthorized()
    {
        // Act
        var response = await _client.GetAsync("/throw-unauthorized");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var content = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(content);

        error.Should().NotBeNull();
        error!.Error.Code.Should().Be(401);
        error.Error.Message.Should().Be("Unauthorized");
        error.Error.Details.Should().Contain("Access denied.");
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_TimeoutException_Returns408RequestTimeout()
    {
        // Act
        var response = await _client.GetAsync("/throw-timeout");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.RequestTimeout);

        var content = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(content);

        error.Should().NotBeNull();
        error!.Error.Code.Should().Be(408);
        error.Error.Message.Should().Be("Request Timeout");
        error.Error.Details.Should().Contain("The request timed out.");
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_UnhandledException_Returns500InternalServerError()
    {
        // Act
        var response = await _client.GetAsync("/throw-unhandled");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var content = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(content);

        error.Should().NotBeNull();
        error!.Error.Code.Should().Be(500);
        error.Error.Message.Should().Be("Internal Server Error");
        error.Error.Details.Should().Contain("An unexpected error occurred.");
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_SuccessfulRequest_PassesThrough()
    {
        // Act
        var response = await _client.GetAsync("/success");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("OK");
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_AddsCorrelationIdHeader_InErrorResponse()
    {
        // Act
        var response = await _client.GetAsync("/throw-general");

        // Assert
        response.Headers.Should().ContainKey("X-Correlation-ID");
        var correlationId = response.Headers.GetValues("X-Correlation-ID").First();
        correlationId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_ODataPath_ReturnsODataErrorFormat()
    {
        // Arrange
        _client.DefaultRequestHeaders.Add("Accept", "application/json");

        // Act
        var response = await _client.GetAsync("/odata/throw-general");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.Should().ContainKey("OData-Version");
        response.Headers.GetValues("OData-Version").First().Should().Be("4.0");

        var content = await response.Content.ReadAsStringAsync();
        // OData error format should have "error" property with nested structure
        content.Should().Contain("\"error\":");
        content.Should().Contain("\"code\":");
        content.Should().Contain("\"message\":");
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_GeoServicesPath_ReturnsGeoServicesErrorFormat()
    {
        // Act
        var response = await _client.GetAsync("/rest/services/throw-general");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(content);

        error.Should().NotBeNull();
        error!.Error.Code.Should().Be(400);
        error.Error.Message.Should().Be("Bad Request");
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_OgcFeaturesPath_ReturnsGeoServicesErrorFormat()
    {
        // Act
        var response = await _client.GetAsync("/collections/throw-general");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(content);

        error.Should().NotBeNull();
        error!.Error.Code.Should().Be(400);
        error.Error.Message.Should().Be("Bad Request");
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
        GC.SuppressFinalize(this);
    }
}