// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Infrastructure.Monitoring;

[Protocol(Protocols.Admin)]
[Operation(Operations.ErrorHandling)]
public sealed class RecentErrorsEndpointTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/errors")]
    public async Task RecentErrorsEndpoint_RedactsSensitiveInfo()
    {
        using var factory = CreateFactory(capacity: 5);
        using var scope = factory.Services.CreateScope();
        var buffer = scope.ServiceProvider.GetRequiredService<RecentErrorBuffer>();

        var context = CreateContext("/api/v1/tiles", "trace-sensitive");
        var message = "User test@example.com password=SuperSecret token=abc123 bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";
        var errorResponse = new StandardErrorResponse(
            StatusCodes.Status500InternalServerError,
            "Internal Server Error",
            message);

        buffer.Record(context, errorResponse);

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/admin/observability/errors");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<RecentErrorsResponse>(payload, _jsonOptions);

        result.Should().NotBeNull();
        result!.Errors.Should().HaveCount(1);

        var entry = result.Errors.Single();
        entry.CorrelationId.Should().Be("trace-sensitive");
        entry.Path.Should().Be("/api/v1/tiles");
        entry.Message.Should().NotContain("test@example.com");
        entry.Message.Should().NotContain("SuperSecret");
        entry.Message.Should().NotContain("abc123");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/errors")]
    public async Task RecentErrorsEndpoint_RecordsClientErrorsWhenExplicitlyRequested()
    {
        using var factory = CreateFactory(capacity: 5);
        using var scope = factory.Services.CreateScope();
        var buffer = scope.ServiceProvider.GetRequiredService<RecentErrorBuffer>();

        var context = CreateContext("/rest/services/test/MapServer/WMS", "trace-client-error");
        buffer.Record(
            context,
            StatusCodes.Status400BadRequest,
            "Bad Request",
            "WMS invalid BBOX",
            includeClientErrors: true);

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/admin/observability/errors");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<RecentErrorsResponse>(payload, _jsonOptions);

        result.Should().NotBeNull();
        result!.Errors.Should().ContainSingle();
        result.Errors[0].StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Errors[0].Path.Should().Be("/rest/services/test/MapServer/WMS");
        result.Errors[0].Message.Should().Contain("WMS invalid BBOX");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/errors")]
    public async Task RecentErrorsEndpoint_CapsBuffer()
    {
        using var factory = CreateFactory(capacity: 2);
        using var scope = factory.Services.CreateScope();
        var buffer = scope.ServiceProvider.GetRequiredService<RecentErrorBuffer>();

        buffer.Record(CreateContext("/api/v1/a", "trace-1"), CreateError());
        buffer.Record(CreateContext("/api/v1/b", "trace-2"), CreateError());
        buffer.Record(CreateContext("/api/v1/c", "trace-3"), CreateError());

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/admin/observability/errors");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<RecentErrorsResponse>(payload, _jsonOptions);

        result.Should().NotBeNull();
        result!.Capacity.Should().Be(2);
        result.Errors.Should().HaveCount(2);
        result.Errors[0].CorrelationId.Should().Be("trace-3");
        result.Errors[1].CorrelationId.Should().Be("trace-2");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/telemetry")]
    public async Task TelemetryStatusEndpoint_ReturnsStatusPayload()
    {
        using var factory = CreateFactory(capacity: 5);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/admin/observability/telemetry");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("tracingEnabled");
        payload.Should().Contain("otlpConfigured");
    }

    private static WebApplicationFactory<Program> CreateFactory(int capacity)
    {
        return new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((context, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["HONUA_DEV_AUTH"] = "true",
                        ["Monitoring:RecentErrors:Capacity"] = capacity.ToString(CultureInfo.InvariantCulture)
                    });
                });
            });
    }

    private static DefaultHttpContext CreateContext(string path, string traceId)
    {
        var context = new DefaultHttpContext
        {
            TraceIdentifier = traceId
        };
        context.Request.Path = path;
        return context;
    }

    private static StandardErrorResponse CreateError()
        => new(StatusCodes.Status500InternalServerError, "Internal Server Error", "Synthetic error");
}
