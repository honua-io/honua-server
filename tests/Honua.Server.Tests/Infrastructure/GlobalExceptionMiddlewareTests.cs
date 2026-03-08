// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OData.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using ValidationException = Honua.Core.Exceptions.ValidationException;

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
                            var path = context.Request.Path.Value;

                            // Handle different exception types based on path
                            if (path != null)
                            {
                                if (path.Contains("throw-argument"))
                                    throw new ArgumentException("Invalid argument provided");
                                if (path.Contains("throw-unauthorized"))
                                    throw new UnauthorizedAccessException("Access denied");
                                if (path.Contains("throw-timeout"))
                                    throw new TimeoutException("Operation timed out");
                                if (path.Contains("throw-circuit"))
                                    throw new BrokenCircuitException("Circuit is open");
                                if (path.Contains("throw-notfound"))
                                    throw new ResourceNotFoundException("Resource missing");
                                if (path.Contains("throw-conflict"))
                                    throw new ResourceConflictException("Resource conflict");
                                if (path.Contains("throw-validation"))
                                    throw new ValidationException("Field 'name' is required.", ["Missing required field: name"]);
                                if (path.Contains("throw-unavailable"))
                                    throw new ServiceUnavailableException("Database connection failed", 30);
                                if (path.Contains("throw-auth-invalid"))
                                    throw new InvalidOperationException("IDX20803: Unable to obtain configuration from issuer metadata endpoint.");
                                if (path.Contains("throw-started"))
                                {
                                    await context.Response.StartAsync();
                                    await context.Response.WriteAsync("partial");
                                    throw new InvalidOperationException("The response has already started, the error handler will not be executed.");
                                }
                                if (path.Contains("throw-general"))
                                    throw new InvalidOperationException("Something went wrong");
                                if (path.Contains("throw-unhandled"))
                                    throw new NotImplementedException("This is not implemented");
                            }

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
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var content = await response.Content.ReadAsStringAsync();
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(content);

        problemDetails.GetProperty("title").GetString().Should().Be("Bad Request");
        problemDetails.GetProperty("detail").GetString().Should().Be("Invalid request parameters.");
        problemDetails.GetProperty("status").GetInt32().Should().Be(400);
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_UnauthorizedAccessException_Returns401Unauthorized()
    {
        // Act
        var response = await _client.GetAsync("/throw-unauthorized");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var content = await response.Content.ReadAsStringAsync();
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(content);

        problemDetails.GetProperty("title").GetString().Should().Be("Unauthorized");
        problemDetails.GetProperty("detail").GetString().Should().Be("Authentication is required to access this resource.");
        problemDetails.GetProperty("status").GetInt32().Should().Be(401);
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_IdentityModelInvalidOperation_Returns401Unauthorized()
    {
        // Act
        var response = await _client.GetAsync("/throw-auth-invalid");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var content = await response.Content.ReadAsStringAsync();
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(content);

        problemDetails.GetProperty("title").GetString().Should().Be("Unauthorized");
        problemDetails.GetProperty("detail").GetString().Should().Be("Authentication is required to access this resource.");
        problemDetails.GetProperty("status").GetInt32().Should().Be(401);
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_TimeoutException_Returns408RequestTimeout()
    {
        // Act
        var response = await _client.GetAsync("/throw-timeout");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.RequestTimeout);

        var content = await response.Content.ReadAsStringAsync();
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(content);

        problemDetails.GetProperty("title").GetString().Should().Be("Request Timeout");
        problemDetails.GetProperty("detail").GetString().Should().Be("The request timed out.");
        problemDetails.GetProperty("status").GetInt32().Should().Be(408);
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_UnhandledException_Returns500InternalServerError()
    {
        // Act
        var response = await _client.GetAsync("/throw-unhandled");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var content = await response.Content.ReadAsStringAsync();
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(content);

        problemDetails.GetProperty("title").GetString().Should().Be("Internal Server Error");
        problemDetails.GetProperty("detail").GetString().Should().Be("An unexpected error occurred while processing the request.");
        problemDetails.GetProperty("status").GetInt32().Should().Be(500);
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_ResourceNotFoundException_Returns404NotFound()
    {
        // Act
        var response = await _client.GetAsync("/throw-notfound");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(content);

        problemDetails.GetProperty("title").GetString().Should().Be("Not Found");
        problemDetails.GetProperty("detail").GetString().Should().Be("The requested resource was not found.");
        problemDetails.GetProperty("status").GetInt32().Should().Be(404);
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_ResourceConflictException_Returns409Conflict()
    {
        // Act
        var response = await _client.GetAsync("/throw-conflict");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var content = await response.Content.ReadAsStringAsync();
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(content);

        problemDetails.GetProperty("title").GetString().Should().Be("Conflict");
        problemDetails.GetProperty("detail").GetString().Should().Be("The request could not be completed due to a conflict with the current state.");
        problemDetails.GetProperty("status").GetInt32().Should().Be(409);
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_OgcPath_ResourceConflictException_ReturnsProblemDetails()
    {
        // Act
        var response = await _client.GetAsync("/ogc/features/throw-conflict");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var content = await response.Content.ReadAsStringAsync();
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(content);

        problemDetails.GetProperty("title").GetString().Should().Be("Conflict");
        problemDetails.GetProperty("status").GetInt32().Should().Be(409);
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
    public async Task GlobalExceptionMiddleware_ODataPath_ReturnsODataErrorFormat()
    {
        // Arrange
        _client.DefaultRequestHeaders.Add("Accept", "application/json");

        // Act
        var response = await _client.GetAsync("/odata/throw-general");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Headers.Should().ContainKey("OData-Version");
        response.Headers.GetValues("OData-Version").First().Should().Be("4.0");

        var content = await response.Content.ReadAsStringAsync();
        // OData error format should have "error" property with nested structure
        content.Should().Contain("\"error\":");
        content.Should().Contain("\"code\":");
        content.Should().Contain("\"message\":");
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_ODataPath_ServiceUnavailable_ReturnsODataErrorFormat()
    {
        // Act
        var response = await _client.GetAsync("/odata/throw-unavailable");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.Should().ContainKey("OData-Version");

        var content = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ODataError>(content);

        error.Should().NotBeNull();
        error!.Error.Code.Should().Be("ServiceUnavailable");
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_GeoServicesPath_ReturnsGeoServicesErrorFormat()
    {
        // Act
        var response = await _client.GetAsync("/rest/services/throw-general");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var content = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(content);

        error.Should().NotBeNull();
        error!.Error.Code.Should().Be(500);
        error.Error.Message.Should().Be("Internal Server Error");
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_OgcFeaturesPath_ReturnsProblemDetailsFormat()
    {
        // Act
        var response = await _client.GetAsync("/ogc/features/throw-general");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var content = await response.Content.ReadAsStringAsync();
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(content);

        problemDetails.GetProperty("title").GetString().Should().Be("Internal Server Error");
        problemDetails.GetProperty("detail").GetString().Should().Be("An unexpected error occurred while processing the request.");
        problemDetails.GetProperty("status").GetInt32().Should().Be(500);
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_ValidationException_Returns400BadRequest()
    {
        // Act
        var response = await _client.GetAsync("/throw-validation");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var content = await response.Content.ReadAsStringAsync();
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(content);

        problemDetails.GetProperty("title").GetString().Should().Be("Bad Request");
        // ValidationException messages are designed to be safe and should be exposed
        var detail = problemDetails.GetProperty("detail").GetString();
        detail.Should().Contain("Field 'name' is required.");
        detail.Should().Contain("Missing required field: name");
        problemDetails.GetProperty("status").GetInt32().Should().Be(400);
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_ServiceUnavailableException_Returns503ServiceUnavailable()
    {
        // Act
        var response = await _client.GetAsync("/throw-unavailable");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var content = await response.Content.ReadAsStringAsync();
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(content);

        problemDetails.GetProperty("title").GetString().Should().Be("Service Unavailable");
        var detail = problemDetails.GetProperty("detail").GetString();
        detail.Should().Contain("The service is temporarily unavailable. Please try again later.");
        detail.Should().Contain("Retry-After: 30s");
        problemDetails.GetProperty("status").GetInt32().Should().Be(503);

        // Should include Retry-After header
        response.Headers.Should().ContainKey("Retry-After");
        response.Headers.GetValues("Retry-After").First().Should().Be("30");
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_BrokenCircuitException_Returns503ServiceUnavailable()
    {
        // Act
        var response = await _client.GetAsync("/throw-circuit");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var content = await response.Content.ReadAsStringAsync();
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(content);

        problemDetails.GetProperty("title").GetString().Should().Be("Service Unavailable");
        problemDetails.GetProperty("status").GetInt32().Should().Be(503);
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_AddsCorrelationIdHeader()
    {
        // Act
        var response = await _client.GetAsync("/throw-general");

        // Assert
        // The X-Correlation-ID header should be added by the middleware
        response.Headers.Should().ContainKey("X-Correlation-ID");
        var correlationId = response.Headers.GetValues("X-Correlation-ID").First();
        correlationId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_InternalServerError_DoesNotLeakExceptionMessage()
    {
        // Act
        var response = await _client.GetAsync("/throw-unhandled");

        // Assert
        var content = await response.Content.ReadAsStringAsync();

        // The raw exception message "This is not implemented" should NOT appear in the response
        content.Should().NotContain("This is not implemented");
        // Should contain a safe generic message instead
        content.Should().Contain("An unexpected error occurred");
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_ResponseStartedException_IsNotSilentlySwallowed()
    {
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => _client.GetAsync("/throw-started"));
        exception.InnerException.Should().NotBeNull();
        exception.InnerException!.ToString().Should().Contain("The response has already started");
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
        GC.SuppressFinalize(this);
    }
}
