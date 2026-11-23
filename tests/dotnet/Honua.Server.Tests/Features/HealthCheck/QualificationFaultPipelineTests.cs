// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Server.Features.HealthCheck;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.HealthCheck;

[Protocol(TestProtocols.Health)]
[Collection("Performance")]
public sealed class QualificationFaultPipelineTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private const string PublicQueryRoute = "/rest/services?f=json";

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/deep")]
    public async Task ServeErrors_PublicRoutesFailWhileProbesStayHealthy()
    {
        using var host = Configure("ServeErrors");
        using var client = host.CreateClient();

        using var live = await client.GetAsync("/healthz/live");
        using var deep = await client.GetAsync("/healthz/deep");
        using var ready = await client.GetAsync("/healthz/ready");
        using var query = await client.GetAsync(PublicQueryRoute);
        using var metrics = await client.GetAsync("/healthz/metrics");
        using var descendant = await client.GetAsync("/healthz/deep/extra");

        live.StatusCode.Should().Be(HttpStatusCode.OK);
        deep.StatusCode.Should().Be(HttpStatusCode.OK, "the watchdog must not see this fault; only deployment alarms may");
        metrics.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "only the three probes are spared");
        descendant.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
        ready.Headers.Contains(QualificationFaultMiddleware.FaultHeader).Should().BeFalse();
        query.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        query.Headers.GetValues(QualificationFaultMiddleware.FaultHeader).Should().Equal("ServeErrors");
    }

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/deep")]
    public async Task Wedged_DeepProbeAndPublicRoutesFailWhileLoadBalancerProbesStayHealthy()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var marker = Substitute.For<IQualificationWedgeMarker>();
        marker.ShouldWedgeAsync("gate-run-0001", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            started.TrySetResult();
            return Task.FromResult(true);
        });
        using var host = Configure("Wedged:gate-run-0001", marker);
        using var client = host.CreateClient();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var live = await client.GetAsync("/healthz/live");
        using var ready = await client.GetAsync("/healthz/ready");
        using var deep = await client.GetAsync("/healthz/deep");
        using var query = await client.GetAsync(PublicQueryRoute);
        using var descendant = await client.GetAsync("/healthz/live/extra");

        live.StatusCode.Should().Be(HttpStatusCode.OK);
        descendant.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
        ready.Headers.Contains(QualificationFaultMiddleware.FaultHeader).Should().BeFalse();
        deep.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        deep.Headers.GetValues(QualificationFaultMiddleware.FaultHeader).Should().Equal("Wedged");
        query.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/deep")]
    public async Task Wedged_AfterTheRecoveryRestart_ServesNormally()
    {
        using var host = Configure("Wedged:gate-run-0001", QualificationFaultTests.ConsumedMarker());
        using var client = host.CreateClient();

        using var deep = await client.GetAsync("/healthz/deep");
        using var query = await client.GetAsync(PublicQueryRoute);

        deep.StatusCode.Should().Be(HttpStatusCode.OK);
        query.StatusCode.Should().Be(HttpStatusCode.OK);
        query.Headers.Contains(QualificationFaultMiddleware.FaultHeader).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/deep")]
    public async Task SettingAbsent_FaultIsInert()
    {
        using var host = Configure(value: null);
        using var client = host.CreateClient();

        using var deep = await client.GetAsync("/healthz/deep");
        using var query = await client.GetAsync(PublicQueryRoute);

        deep.StatusCode.Should().Be(HttpStatusCode.OK);
        query.StatusCode.Should().Be(HttpStatusCode.OK);
        query.Headers.Contains(QualificationFaultMiddleware.FaultHeader).Should().BeFalse();
    }

    private WebApplicationFactory<Program> Configure(string? value, IQualificationWedgeMarker? marker = null)
        => factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { [QualificationFault.ConfigurationKey] = value }));
            builder.ConfigureServices(services =>
            {
                var readiness = Substitute.For<IReadinessCheckService>();
                readiness.CheckReadinessAsync(Arg.Any<CancellationToken>()).Returns(ReadinessResult.Ready());
                services.AddSingleton(readiness);
                var database = Substitute.For<IDatabaseHealthChecker>();
                database.IsDatabaseHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
                services.AddSingleton(database);
                services.AddSingleton(DeepHealthEndpointFailureTests.HealthyCatalog());
                if (marker is not null)
                {
                    services.AddSingleton(marker);
                }
            });
        });
}
