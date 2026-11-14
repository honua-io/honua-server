// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests;

[Protocol(TestProtocols.Health)]
[Collection("Performance")]
public sealed class DeepHealthEndpointFailureTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/deep")]
    public async Task DeepHealthEndpoint_DatabaseFailsAfterSuccess_ReportsFailureWithoutCachedSuccess()
    {
        var checker = Substitute.For<IDatabaseHealthChecker>();
        checker.IsDatabaseHealthyAsync(Arg.Any<CancellationToken>()).Returns(true, false);
        using var host = Configure(checker, HealthyCatalog());
        using var client = host.CreateClient();

        using var healthy = await client.GetAsync("/healthz/deep");
        using var unhealthy = await client.GetAsync("/healthz/deep");

        healthy.StatusCode.Should().Be(HttpStatusCode.OK);
        unhealthy.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        unhealthy.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await unhealthy.Content.ReadAsStringAsync()).Should().Be("Not Ready");
        await checker.Received(2).IsDatabaseHealthyAsync(Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/deep")]
    public async Task DeepHealthEndpoint_CatalogFails_ReturnsSanitizedUnavailable()
    {
        var checker = Substitute.For<IDatabaseHealthChecker>();
        checker.IsDatabaseHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        var catalog = Substitute.For<IMetadataV2GraphProvider>();
        catalog.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns<MetadataV2GraphSnapshot>(_ => throw new InvalidOperationException("secret connection string"));
        using var host = Configure(checker, catalog);
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/healthz/deep");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Be("Not Ready");
    }

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/deep")]
    public async Task DeepHealthEndpoint_DatabaseStalls_CancelsDependencyAndReturnsUnavailable()
    {
        var checker = Substitute.For<IDatabaseHealthChecker>();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        checker.IsDatabaseHealthyAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                return true;
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        });
        using var host = Configure(checker, HealthyCatalog());
        using var client = host.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        using var response = await client.GetAsync("/healthz/deep");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/deep")]
    public async Task DeepHealthEndpoint_DependencyIgnoresCancellation_ReturnsUnavailableWithinBudget()
    {
        var checker = Substitute.For<IDatabaseHealthChecker>();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        checker.IsDatabaseHealthyAsync(Arg.Any<CancellationToken>()).Returns(pending.Task);
        using var host = Configure(checker, HealthyCatalog());
        using var client = host.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        try
        {
            using var response = await client.GetAsync("/healthz/deep");

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await response.Content.ReadAsStringAsync()).Should().Be("Not Ready");
            pending.Task.IsCompleted.Should().BeFalse();
        }
        finally
        {
            pending.TrySetResult(false);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("POST /healthz/deep")]
    [Endpoint("PUT /healthz/deep")]
    [Endpoint("DELETE /healthz/deep")]
    [Endpoint("PATCH /healthz/deep")]
    public async Task DeepHealthEndpoint_NonGetMethods_ReturnsMethodNotAllowed()
    {
        using var client = factory.CreateClient();
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
        {
            using var request = new HttpRequestMessage(method, "/healthz/deep");
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/deep")]
    public async Task DeepHealthEndpoint_MigrationsFail_ReturnsUnavailableDespiteHealthyDatabase()
    {
        var checker = Substitute.For<IDatabaseHealthChecker>();
        checker.IsDatabaseHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        using var host = Configure(checker, HealthyCatalog());
        using var client = host.CreateClient();
        host.Services.GetRequiredService<MigrationState>().MarkFailed("private migration failure");

        using var response = await client.GetAsync("/healthz/deep");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Be("Not Ready");
    }

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/deep")]
    public async Task DeepHealthEndpoint_HeadRequest_DoesNotExecuteDependencies()
    {
        var checker = Substitute.For<IDatabaseHealthChecker>();
        checker.IsDatabaseHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        using var host = Configure(checker, HealthyCatalog());
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, "/healthz/deep");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        response.Content.Headers.Allow.Should().Contain("GET");
        await checker.DidNotReceive().IsDatabaseHealthyAsync(Arg.Any<CancellationToken>());
    }

    private WebApplicationFactory<Program> Configure(IDatabaseHealthChecker checker, IMetadataV2GraphProvider catalog)
        => factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(checker);
            services.AddSingleton(catalog);
        }));

    private static IMetadataV2GraphProvider HealthyCatalog()
    {
        var catalog = Substitute.For<IMetadataV2GraphProvider>();
        catalog.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new MetadataV2GraphSnapshot(
            new MetadataV2Graph(), "empty", DateTimeOffset.UtcNow));
        return catalog;
    }
}
