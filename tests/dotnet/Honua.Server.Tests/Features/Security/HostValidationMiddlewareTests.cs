// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Security;

[Collection("Database")]
[Protocol(Protocols.OgcApiFeatures)]
public sealed class HostValidationMiddlewareTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostValidation:Enabled"] = "true",
                ["Public:BaseUrl"] = "https://api.honua.test"
            })));

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features")]
    public async Task Request_WithConfiguredPublicHost_AllowsRequest()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ogc/features?f=json");
        request.Headers.Host = "api.honua.test";

        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features")]
    public async Task Request_WithForgedHostHeader_ReturnsBadRequest()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ogc/features?f=json");
        request.Headers.Host = "attacker.example";

        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Invalid Host header");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /healthz/live")]
    public async Task HealthRequest_WithForgedHostHeader_AllowsRequest()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz/live");
        request.Headers.Host = "attacker.example";

        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

[Collection("Database")]
[Protocol(Protocols.OgcApiFeatures)]
public sealed class HostValidationFallbackTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostValidation:Enabled"] = "true"
            })));

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features")]
    public async Task Request_WithForgedIpHostHeader_AndNoAllowlist_ReturnsBadRequest()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ogc/features?f=json");
        request.Headers.Host = "203.0.113.77";

        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Invalid Host header");
    }
}

public sealed class HostValidationMiddlewareUnitTests
{
    [Fact]
    public async Task InvokeAsync_FallbackMode_LocalhostWithNonLoopbackLocalAddress_ReturnsBadRequest()
    {
        var (middleware, tracker) = CreateMiddleware();
        var context = CreateContext("localhost", IPAddress.Parse("203.0.113.10"));

        await middleware.InvokeAsync(context);

        tracker.NextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        body.Should().Contain("Invalid Host header");
    }

    [Fact]
    public async Task InvokeAsync_FallbackMode_LocalhostWithLoopbackLocalAddress_AllowsRequest()
    {
        var (middleware, tracker) = CreateMiddleware();
        var context = CreateContext("localhost", IPAddress.Loopback);

        await middleware.InvokeAsync(context);

        tracker.NextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
    }

    [Fact]
    public async Task InvokeAsync_FallbackMode_TestEnvironment_LocalhostWithNullLocalAddress_AllowsRequest()
    {
        var (middleware, tracker) = CreateMiddleware(environmentName: Environments.Staging);
        var context = CreateContext("localhost", null, IPAddress.Parse("198.51.100.25"));

        await middleware.InvokeAsync(context);

        tracker.NextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        var (testMiddleware, testTracker) = CreateMiddleware(environmentName: "Test");
        var testContext = CreateContext("localhost", null, IPAddress.Parse("198.51.100.25"));

        await testMiddleware.InvokeAsync(testContext);

        testTracker.NextCalled.Should().BeTrue();
        testContext.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
    }

    [Fact]
    public async Task InvokeAsync_FallbackMode_InMemoryConnectionAndLoopbackHost_AllowsRequest()
    {
        var (middleware, tracker) = CreateMiddleware();
        var context = CreateContext("127.0.0.1", null, null);

        await middleware.InvokeAsync(context);

        tracker.NextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
    }

    [Fact]
    public async Task InvokeAsync_HealthProbePath_BypassesHostValidation()
    {
        var (middleware, tracker) = CreateMiddleware();
        var context = CreateContext("203.0.113.77", IPAddress.Parse("198.51.100.10"), path: "/healthz/ready");

        await middleware.InvokeAsync(context);

        tracker.NextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
    }

    private static (HostValidationMiddleware Middleware, InvocationTracker Tracker) CreateMiddleware(string environmentName = "Production")
    {
        var tracker = new InvocationTracker();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostValidation:Enabled"] = "true"
            })
            .Build();

        Task Next(HttpContext context)
        {
            tracker.NextCalled = true;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }

        var middleware = new HostValidationMiddleware(
            Next,
            configuration,
            new TestHostEnvironment { EnvironmentName = environmentName },
            NullLogger<HostValidationMiddleware>.Instance);

        return (middleware, tracker);
    }

    private static DefaultHttpContext CreateContext(
        string host,
        IPAddress? localIpAddress,
        IPAddress? remoteIpAddress = null,
        string path = "/ogc/features")
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();

        var context = new DefaultHttpContext();
        context.RequestServices = services;
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Connection.LocalIpAddress = localIpAddress;
        context.Connection.RemoteIpAddress = remoteIpAddress;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Honua.Server.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class InvocationTracker
    {
        public bool NextCalled { get; set; }
    }
}
