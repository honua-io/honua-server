// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.HealthCheck;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.HealthCheck;

[Protocol(TestProtocols.Health)]
[Collection("Performance")]
public sealed class QualificationFaultPipelineTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private const string PublicQueryRoute = "/rest/services";

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/deep")]
    public async Task ServeErrors_PublicRoutesFailWhileProbesStayHealthy()
    {
        using var host = Configure(QualificationFaultTests.Gate(QualificationFault.Parse("ServeErrors"), marker: null));
        using var client = host.CreateClient();

        using var live = await client.GetAsync("/healthz/live");
        using var deep = await client.GetAsync("/healthz/deep");
        using var ready = await client.GetAsync("/healthz/ready");
        using var query = await client.GetAsync(PublicQueryRoute);

        live.StatusCode.Should().Be(HttpStatusCode.OK);
        deep.StatusCode.Should().Be(HttpStatusCode.OK, "the watchdog must not see this fault; only deployment alarms may");
        ready.Headers.Contains(QualificationFaultMiddleware.FaultHeader).Should().BeFalse();
        query.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        query.Headers.GetValues(QualificationFaultMiddleware.FaultHeader).Should().Equal("ServeErrors");
    }

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/deep")]
    public async Task Wedged_DeepProbeAndPublicRoutesFailWhileLoadBalancerProbesStayHealthy()
    {
        using var host = Configure(QualificationFaultTests.Gate(
            QualificationFault.Parse("Wedged:gate-run-0001"), QualificationFaultTests.Marker(shouldWedge: true)));
        using var client = host.CreateClient();

        using var live = await client.GetAsync("/healthz/live");
        using var ready = await client.GetAsync("/healthz/ready");
        using var deep = await client.GetAsync("/healthz/deep");
        using var query = await client.GetAsync(PublicQueryRoute);

        live.StatusCode.Should().Be(HttpStatusCode.OK);
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
        using var host = Configure(QualificationFaultTests.Gate(
            QualificationFault.Parse("Wedged:gate-run-0001"), QualificationFaultTests.Marker(shouldWedge: false)));
        using var client = host.CreateClient();

        using var deep = await client.GetAsync("/healthz/deep");
        using var query = await client.GetAsync(PublicQueryRoute);

        deep.StatusCode.Should().Be(HttpStatusCode.OK);
        query.Headers.Contains(QualificationFaultMiddleware.FaultHeader).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/deep")]
    public async Task SettingAbsent_FaultIsInert()
    {
        using var host = Configure(QualificationFaultTests.Gate(QualificationFault.None, marker: null));
        using var client = host.CreateClient();

        using var deep = await client.GetAsync("/healthz/deep");
        using var query = await client.GetAsync(PublicQueryRoute);

        deep.StatusCode.Should().Be(HttpStatusCode.OK);
        query.Headers.Contains(QualificationFaultMiddleware.FaultHeader).Should().BeFalse();
    }

    private WebApplicationFactory<Program> Configure(QualificationFaultGate gate)
        => factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(HealthyDatabase());
            services.AddSingleton(HealthyCatalog());
            services.AddSingleton(gate);
        }));

    private static IDatabaseHealthChecker HealthyDatabase()
    {
        var checker = Substitute.For<IDatabaseHealthChecker>();
        checker.IsDatabaseHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        return checker;
    }

    private static IMetadataV2GraphProvider HealthyCatalog()
    {
        var catalog = Substitute.For<IMetadataV2GraphProvider>();
        catalog.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new MetadataV2GraphSnapshot(
            new MetadataV2Graph(), "empty", DateTimeOffset.UtcNow));
        return catalog;
    }
}
