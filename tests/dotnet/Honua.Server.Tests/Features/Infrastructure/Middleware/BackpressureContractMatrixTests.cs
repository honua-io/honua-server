// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Backpressure;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Core.Features.RateLimiting.Domain;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.RateLimiting;
using Honua.Protocols.OData;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Middleware;

/// <summary>
/// Full 9-surface by 2-cause backpressure matrix for issue #3905.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class BackpressureContractMatrixTests
{
    private const string CorrelationId = "backpressure-correlation";
    private const int SaturationDelaySeconds = 17;

    public static TheoryData<string, string> ContractCells
    {
        get
        {
            var cells = new TheoryData<string, string>();
            foreach (var route in Routes)
            {
                foreach (var pressure in Pressures)
                {
                    cells.Add(route.Name, pressure.Name);
                }
            }

            return cells;
        }
    }

    [UnitTest]
    public void ContractCells_ContainsExactNineByTwoDenominator()
    {
        Routes.Should().HaveCount(9);
        Pressures.Should().HaveCount(2);
        ContractCells.Count.Should().Be(18);
    }

    [Theory]
    [MemberData(nameof(ContractCells))]
    [Trait("Category", "Integration")]
    [Trait("Tier", "Integration")]
    [Operation(Operations.Infrastructure)]
    [Endpoint("GET /backpressure")]
    [Endpoint("POST /backpressure")]
    public async Task Backpressure_RouteAndCause_UsesNativeRetryableEnvelopeWithoutHandlerSideEffects(
        string routeName,
        string pressureName)
    {
        var route = Routes.Single(candidate => candidate.Name == routeName);
        var pressure = Pressures.Single(candidate => candidate.Name == pressureName);
        var handlerCalls = new StrongBox<int>();
        await using var app = await CreateAppAsync(route, pressure, handlerCalls);
        var client = app.GetTestClient();

        if (pressure.Kind == BackpressureKind.Throttled)
        {
            using var allowedRequest = CreateRequest(route);
            using var allowedResponse = await client.SendAsync(allowedRequest);
            allowedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            handlerCalls.Value.Should().Be(1);
        }

        var callsBeforeDenial = handlerCalls.Value;
        using var request = CreateRequest(route);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        handlerCalls.Value.Should().Be(callsBeforeDenial, "backpressure must terminate before endpoint execution");
        response.Headers.GetValues("X-Correlation-ID").Should().ContainSingle(CorrelationId);
        response.Headers.GetValues("Honua-Retryable").Should().ContainSingle("true");
        var retryAfterSeconds = int.Parse(
            response.Headers.GetValues("Retry-After").Single(),
            System.Globalization.CultureInfo.InvariantCulture);
        if (pressure.Kind == BackpressureKind.Saturated)
        {
            retryAfterSeconds.Should().Be(SaturationDelaySeconds);
        }
        else
        {
            retryAfterSeconds.Should().BeInRange(0, 60);
        }

        AssertNativeContract(route, pressure, retryAfterSeconds, response, body);
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    [Endpoint("POST /mcp")]
    public async Task McpBatchBackpressure_PreservesBatchEnvelopeAndRequestIds()
    {
        await using var app = await CreateAppAsync(
            new RouteCase("MCP", "/mcp"),
            new PressureCase(
                "503 saturated",
                BackpressureKind.Saturated,
                HttpStatusCode.ServiceUnavailable,
                BackpressureMetadata.ServiceUnavailableCode,
                "unavailable",
                "14"),
            new StrongBox<int>());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                "[{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"tools/list\"}," +
                "{\"jsonrpc\":\"2.0\",\"id\":\"second\",\"method\":\"tools/list\"}]",
                Encoding.UTF8,
                "application/json")
        };

        using var response = await app.GetTestClient().SendAsync(request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var responses = document.RootElement;

        responses.ValueKind.Should().Be(JsonValueKind.Array);
        responses.GetArrayLength().Should().Be(2);
        responses[0].GetProperty("id").GetInt32().Should().Be(11);
        responses[1].GetProperty("id").GetString().Should().Be("second");
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    [Endpoint("POST /mcp")]
    public async Task McpBackpressure_WhenEventStreamIsAccepted_UsesMessageEvent()
    {
        await using var app = await CreateAppAsync(
            new RouteCase("MCP", "/mcp"),
            new PressureCase(
                "503 saturated",
                BackpressureKind.Saturated,
                HttpStatusCode.ServiceUnavailable,
                BackpressureMetadata.ServiceUnavailableCode,
                "unavailable",
                "14"),
            new StrongBox<int>());

        using var request = CreateRequest(new RouteCase("MCP", "/mcp"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        body.Should().StartWith("event: message\ndata: {");
        body.Should().EndWith("\n\n");
        body.Should().Contain("\"code\":\"unavailable\"");
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    [Endpoint("POST /mcp")]
    public async Task McpBackpressure_InspectsOnlyBoundedRequestPrefix()
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = CorrelationId;
        context.Request.Path = "/mcp";
        var payload = Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"id\":3905,\"padding\":\"" +
            new string('x', 32 * 1024) + "\"}");
        var body = new CountingRequestBody(payload);
        context.Request.Body = body;
        context.Response.Body = new MemoryStream();

        await BackpressureResponseWriter.WriteAsync(context, BackpressureKind.Saturated, 17);

        body.BytesRead.Should().BeLessOrEqualTo(16 * 1024);
    }

    private static void AssertNativeContract(
        RouteCase route,
        PressureCase pressure,
        int retryAfterSeconds,
        HttpResponseMessage response,
        string body)
    {
        switch (route.Name)
        {
            case "generic RFC7807":
            case "Admin":
            case "OGC API":
                {
                    response.StatusCode.Should().Be(pressure.HttpStatus);
                    response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
                    using var document = JsonDocument.Parse(body);
                    var problem = document.RootElement;
                    problem.GetProperty("code").GetString().Should().Be(pressure.MachineCode);
                    problem.GetProperty("retryable").GetBoolean().Should().BeTrue();
                    problem.GetProperty("retryAfterSeconds").GetInt32().Should().Be(retryAfterSeconds);
                    problem.GetProperty("correlationId").GetString().Should().Be(CorrelationId);
                    break;
                }

            case "OData":
                {
                    response.StatusCode.Should().Be(pressure.HttpStatus);
                    response.Headers.GetValues("OData-Version").Should().ContainSingle("4.01");
                    using var document = JsonDocument.Parse(body);
                    var error = document.RootElement.GetProperty("error");
                    error.GetProperty("code").GetString().Should().Be(pressure.MachineCode);
                    error.GetProperty("retryable").GetBoolean().Should().BeTrue();
                    error.GetProperty("retryAfterSeconds").GetInt32().Should().Be(retryAfterSeconds);
                    error.GetProperty("details").EnumerateArray().Should().Contain(detail =>
                        detail.GetProperty("code").GetString() == "CorrelationId"
                        && detail.GetProperty("message").GetString() == CorrelationId);
                    break;
                }

            case "GeoServices":
                {
                    response.StatusCode.Should().Be(HttpStatusCode.OK);
                    using var document = JsonDocument.Parse(body);
                    var error = document.RootElement.GetProperty("error");
                    error.GetProperty("code").GetInt32().Should().Be((int)pressure.HttpStatus);
                    error.GetProperty("retryable").GetBoolean().Should().BeTrue();
                    error.GetProperty("retryAfterSeconds").GetInt32().Should().Be(retryAfterSeconds);
                    error.GetProperty("details").EnumerateArray().Should().Contain(
                        value => value.GetString() == $"CorrelationId: {CorrelationId}");
                    break;
                }

            case "WFS":
                response.StatusCode.Should().Be(pressure.HttpStatus);
                response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
                body.Should().Contain($"exceptionCode=\"{pressure.MachineCode}\"");
                break;

            case "WMS":
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
                body.Should().Contain($"code=\"{pressure.MachineCode}\"");
                break;

            case "MCP":
                {
                    response.StatusCode.Should().Be(HttpStatusCode.OK);
                    using var document = JsonDocument.Parse(body);
                    var root = document.RootElement;
                    root.GetProperty("jsonrpc").GetString().Should().Be("2.0");
                    root.GetProperty("id").GetInt32().Should().Be(3905);
                    var data = root.GetProperty("error").GetProperty("data");
                    data.GetProperty("code").GetString().Should().Be(pressure.McpCode);
                    data.GetProperty("retryable").GetBoolean().Should().BeTrue();
                    data.GetProperty("retryAfterSeconds").GetInt32().Should().Be(retryAfterSeconds);
                    data.GetProperty("correlationId").GetString().Should().Be(CorrelationId);
                    break;
                }

            case "gRPC":
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                response.Content.Headers.ContentType?.MediaType.Should().Be("application/grpc");
                response.Headers.GetValues("grpc-status").Should().ContainSingle(pressure.GrpcStatus);
                response.Headers.GetValues(BackpressureMetadata.ErrorCodeKey).Should().ContainSingle(pressure.MachineCode);
                response.Headers.GetValues(BackpressureMetadata.RetryableKey).Should().ContainSingle("true");
                response.Headers.GetValues(BackpressureMetadata.RetryAfterKey).Should().ContainSingle(
                    retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                response.Headers.GetValues(BackpressureMetadata.CorrelationIdKey).Should().ContainSingle(CorrelationId);
                body.Should().BeEmpty();
                break;

            default:
                throw new InvalidOperationException($"Unknown route family {route.Name}.");
        }

        body.Should().NotContain("\"error\":\"rate_limit_exceeded\"");
    }

    private static async Task<WebApplication> CreateAppAsync(
        RouteCase route,
        PressureCase pressure,
        StrongBox<int> handlerCalls)
    {
        StandardErrorResponseFormatter.ODataErrorFormatterOverride = ODataErrorFormatter.Format;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.TraceIdentifier = CorrelationId;
            context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.205");
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, $"matrix-{route.Name}")],
                "Test"));
            await next();
        });

        if (pressure.Kind == BackpressureKind.Throttled)
        {
            var policyStore = Substitute.For<IRateLimitPolicyStore>();
            app.Use(next =>
            {
                var limiter = new RateLimitingMiddleware(
                    next,
                    policyStore,
                    Options.Create(new RateLimitingOptions
                    {
                        Enabled = true,
                        GlobalRequestsPerMinute = 1,
                    }),
                    NullLogger<RateLimitingMiddleware>.Instance,
                    redis: null);
                return limiter.InvokeAsync;
            });
        }
        else
        {
            app.Use(_ => context => BackpressureResponseWriter.WriteAsync(
                context,
                BackpressureKind.Saturated,
                SaturationDelaySeconds,
                context.RequestAborted));
        }

        app.MapMethods(route.Path, [HttpMethods.Get, HttpMethods.Post], () =>
        {
            Interlocked.Increment(ref handlerCalls.Value);
            return Results.Text("handler-called");
        });

        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage CreateRequest(RouteCase route)
    {
        var request = new HttpRequestMessage(
            route.Name is "MCP" or "gRPC" ? HttpMethod.Post : HttpMethod.Get,
            route.Path);

        if (route.Name == "MCP")
        {
            request.Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":3905,\"method\":\"tools/list\"}",
                Encoding.UTF8,
                "application/json");
        }
        else if (route.Name == "gRPC")
        {
            request.Version = HttpVersion.Version20;
            request.Content = new ByteArrayContent([]);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        }

        return request;
    }

    private static readonly RouteCase[] Routes =
    [
        new("generic RFC7807", "/native-contract"),
        new("Admin", "/api/v1/admin/metadata/services"),
        new("OGC API", "/ogc/features/collections"),
        new("OData", "/odata/Features"),
        new("GeoServices", "/rest/services/example/FeatureServer"),
        new("WFS", "/wfs"),
        new("WMS", "/ogc/services/example/wms"),
        new("MCP", "/mcp"),
        new("gRPC", "/honua.v1.FeatureService/GetFeature"),
    ];

    private static readonly PressureCase[] Pressures =
    [
        new(
            "429 throttled",
            BackpressureKind.Throttled,
            HttpStatusCode.TooManyRequests,
            BackpressureMetadata.RateLimitExceededCode,
            "resource_exhausted",
            "8"),
        new(
            "503 saturated",
            BackpressureKind.Saturated,
            HttpStatusCode.ServiceUnavailable,
            BackpressureMetadata.ServiceUnavailableCode,
            "unavailable",
            "14"),
    ];

    private sealed record RouteCase(string Name, string Path);

    private sealed record PressureCase(
        string Name,
        BackpressureKind Kind,
        HttpStatusCode HttpStatus,
        string MachineCode,
        string McpCode,
        string GrpcStatus);

    private sealed class CountingRequestBody(byte[] payload) : MemoryStream(payload)
    {
        public int BytesRead { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = base.Read(buffer.Span);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
    }
}
