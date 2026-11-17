// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Streaming;
using Honua.Server.Tests.Infrastructure.Telemetry;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Streaming;

public sealed class FeatureStreamAnonymousAdmissionTests
{
    private const string ClientA = FeatureStreamSessionManager.AnonymousClientPartitionPrefix + "198.51.100.1";
    private const string ClientB = FeatureStreamSessionManager.AnonymousClientPartitionPrefix + "198.51.100.2";

    [UnitTest]
    public void TryCreateSession_AnonymousClientAtItsCap_DoesNotBlockAnotherClient()
    {
        using var manager = CreateManager(new FeatureStreamOptions { MaxConcurrentSessions = 8, MaxAnonymousSessionsPerClient = 2 });

        var sessions = AdmitMany(manager, ClientA, 3);
        var other = manager.TryCreateSession("SSE", null, admissionPartition: ClientB);

        sessions.Should().HaveCount(2);
        other.Should().NotBeNull();
        DisposeAll(sessions);
        other!.Dispose();
    }

    [UnitTest]
    public void TryCreateSession_AllAnonymousClientsTogether_ShareOneAllowance()
    {
        using var manager = CreateManager(new FeatureStreamOptions { MaxConcurrentSessions = 3, MaxAnonymousSessionsPerClient = 2 });

        var sessions = AdmitMany(manager, ClientA, 2).Concat(AdmitMany(manager, ClientB, 2)).ToList();
        var principal = manager.TryCreateSession("SSE", null, admissionPartition: "principal:alice");

        sessions.Should().HaveCount(3, "anonymous clients together are held to MaxConcurrentSessions");
        principal.Should().NotBeNull("authenticated principals keep their own allowance");
        DisposeAll(sessions);
        principal!.Dispose();
    }

    [UnitTest]
    public void TryCreateSession_NodeCapReached_RefusesPrincipalsAndAnonymousClients()
    {
        using var manager = CreateManager(new FeatureStreamOptions { MaxConcurrentSessions = 2, MaxNodeSessions = 3 });

        var sessions = AdmitMany(manager, "principal:alice", 2).Concat(AdmitMany(manager, "principal:bob", 2)).ToList();

        sessions.Should().HaveCount(3);
        manager.TryCreateSession("SSE", null, admissionPartition: ClientA).Should().BeNull();
        manager.SessionCount.Should().Be(3);
        DisposeAll(sessions);
        manager.SessionCount.Should().Be(0);
    }

    [UnitTest]
    public void TryCreateSession_AnonymousSessionsReleased_ReturnTheSharedAllowance()
    {
        using var manager = CreateManager(new FeatureStreamOptions { MaxConcurrentSessions = 2, MaxAnonymousSessionsPerClient = 2 });

        DisposeAll(AdmitMany(manager, ClientA, 2));
        var again = AdmitMany(manager, ClientB, 2);

        again.Should().HaveCount(2);
        manager.AnonymousClientSessionCount.Should().Be(2);
        DisposeAll(again);
        manager.AnonymousClientSessionCount.Should().Be(0);
    }

    [UnitTest]
    public async Task HealthCheck_AnonymousAllowanceNearlyFull_ReportsDegraded()
    {
        using var manager = CreateManager(new FeatureStreamOptions { MaxConcurrentSessions = 4, MaxAnonymousSessionsPerClient = 2 });
        var sessions = AdmitMany(manager, ClientA, 2).Concat(AdmitMany(manager, ClientB, 2)).ToList();

        var result = await new FeatureStreamHealthCheck(manager).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["anonymous_sessions"].Should().Be(4);
        DisposeAll(sessions);
    }

    [UnitTest]
    public void ResolveAdmissionPartition_AnonymousWithDefaultTenant_StaysAnonymous()
    {
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IOptions<TenantContextOptions>)).Returns(Options.Create(new TenantContextOptions { Enabled = true }));
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("public");
        services.GetService(typeof(ITenantContext)).Returns(tenant);
        var context = AnonymousContext("198.51.100.1");
        context.RequestServices = services;

        FeatureStreamEndpoints.ResolveAdmissionPartition(context).Should().Be(ClientA);
    }

    [UnitTest]
    public void ResolveAdmissionPartition_NoClientAddress_UsesOneUnknownClient()
    {
        var context = new DefaultHttpContext { RequestServices = Substitute.For<IServiceProvider>() };

        FeatureStreamEndpoints.ResolveAdmissionPartition(context)
            .Should().Be(FeatureStreamSessionManager.AnonymousClientPartitionPrefix + "unknown");
    }

    [UnitTheory]
    [InlineData("2001:db8:1:2::10", "2001:db8:1:2::20")]
    [InlineData("::ffff:198.51.100.1", "198.51.100.1")]
    public void ResolveAdmissionPartition_SameAnonymousClient_SharesPartition(string first, string second)
    {
        var a = FeatureStreamEndpoints.ResolveAdmissionPartition(AnonymousContext(first));

        a.Should().StartWith(FeatureStreamSessionManager.AnonymousClientPartitionPrefix);
        FeatureStreamEndpoints.ResolveAdmissionPartition(AnonymousContext(second)).Should().Be(a);
    }

    [UnitTheory]
    [InlineData("2001:db8:1:2::10", "2001:db8:1:3::10")]
    [InlineData("198.51.100.1", "198.51.100.2")]
    public void ResolveAdmissionPartition_DifferentAnonymousClients_GetSeparatePartitions(string first, string second)
    {
        FeatureStreamEndpoints.ResolveAdmissionPartition(AnonymousContext(first))
            .Should().NotBe(FeatureStreamEndpoints.ResolveAdmissionPartition(AnonymousContext(second)));
    }

    private static FeatureStreamSessionManager CreateManager(FeatureStreamOptions options)
        => new(Options.Create(options), NullLogger<FeatureStreamSessionManager>.Instance, TestTelemetry.CreateFeatureStreamMetrics());

    private static List<FeatureStreamSession> AdmitMany(FeatureStreamSessionManager manager, string partition, int attempts)
        => Enumerable.Range(0, attempts)
            .Select(_ => manager.TryCreateSession("SSE", null, admissionPartition: partition))
            .OfType<FeatureStreamSession>()
            .ToList();

    private static void DisposeAll(IEnumerable<FeatureStreamSession> sessions)
    {
        foreach (var session in sessions)
        {
            session.Dispose();
        }
    }

    private static DefaultHttpContext AnonymousContext(string address)
    {
        var context = new DefaultHttpContext { RequestServices = Substitute.For<IServiceProvider>() };
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return context;
    }
}
