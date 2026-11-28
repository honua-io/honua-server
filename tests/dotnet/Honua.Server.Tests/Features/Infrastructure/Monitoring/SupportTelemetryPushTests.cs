// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.Hosting;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

[Protocol(TestProtocols.TestQuality)]
public sealed class SupportTelemetryPushTests
{
    public sealed class InvalidConfigCase
    {
        public string? EndpointUrl { get; init; }
        public string AllowHttp { get; init; } = "false";
        public string ApiKey { get; init; } = "key-abc";
        public required string ExpectedMessagePattern { get; init; }
    }

    public static TheoryData<InvalidConfigCase> InvalidEnabledConfigs() =>
    [
        new() { EndpointUrl = null, ExpectedMessagePattern = "*EndpointUrl*" },
        new() { EndpointUrl = "not-a-url", ExpectedMessagePattern = "*EndpointUrl*" },
        new() { EndpointUrl = "http://support.honua.io", ExpectedMessagePattern = "*https*" },
        new() { EndpointUrl = "https://user:pass@support.honua.io", ExpectedMessagePattern = "*embedded credentials*" },
        new() { EndpointUrl = "https://support.honua.io", ApiKey = "", ExpectedMessagePattern = "*ApiKey*" },
        new() { EndpointUrl = "https://support.honua.io", ApiKey = "key with spaces", ExpectedMessagePattern = "*ASCII*" },
        new() { EndpointUrl = "https://support.honua.io", ApiKey = "key\r\nX-Injected: 1", ExpectedMessagePattern = "*ASCII*" },
    ];

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void AddSupportTelemetryPush_DisabledByDefault_DoesNotRegisterBackgroundWorker()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddSupportTelemetryPush(configuration);

        services.Should().NotContain(d => d.ServiceType == typeof(IHostedService));
    }

    [UnitTheory]
    [MemberData(nameof(InvalidEnabledConfigs))]
    [Operation(Operations.TestInfrastructure)]
    public void AddSupportTelemetryPush_InvalidEnabledConfiguration_FailsClosed(InvalidConfigCase invalidCase)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SupportTelemetry:Enabled"] = "true",
                ["SupportTelemetry:EndpointUrl"] = invalidCase.EndpointUrl,
                ["SupportTelemetry:AllowHttp"] = invalidCase.AllowHttp,
                ["SupportTelemetry:ApiKey"] = invalidCase.ApiKey,
            })
            .Build();
        var services = new ServiceCollection();

        var act = () => services.AddSupportTelemetryPush(configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage(invalidCase.ExpectedMessagePattern);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void AddSupportTelemetryPush_EnabledWithValidHttps_RegistersWorkerAndSuppressesOnLambdaProfile()
    {
        var baseSettings = new Dictionary<string, string?>
        {
            ["SupportTelemetry:Enabled"] = "true",
            ["SupportTelemetry:EndpointUrl"] = "https://support.honua.io",
            ["SupportTelemetry:ApiKey"] = "key-customer-1",
        };

        var standardServices = new ServiceCollection();
        standardServices.AddSupportTelemetryPush(new ConfigurationBuilder().AddInMemoryCollection(baseSettings).Build());
        standardServices.Should().Contain(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(SupportTelemetryPushBackgroundService));

        var lambdaSettings = new Dictionary<string, string?>(baseSettings)
        {
            ["HONUA_CONTROL_PLANE_LAMBDA_HANDLER"] = "true",
        };
        var lambdaServices = new ServiceCollection();
        lambdaServices.AddSupportTelemetryPush(new ConfigurationBuilder().AddInMemoryCollection(lambdaSettings).Build());
        lambdaServices.Should().NotContain(d => d.ServiceType == typeof(IHostedService));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task PushIngestAsync_ValidConfiguration_DeliversSanitizedWirePayloadWithArtifactDigest()
    {
        using var handler = new RecordingHandler(HttpStatusCode.Accepted);
        await using var provider = BuildFullTelemetryProvider(handler);
        var client = provider.GetRequiredService<SupportTelemetryPushClient>();

        var delivered = await client.PushIngestAsync(CancellationToken.None);

        delivered.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
        AssertValidIngestRequest(handler.Requests[0]);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task PushAsync_WithTicketIdAndDegradedFindingsService_StillDeliversSnapshotAndReturnsFalseOnRejection()
    {
        using var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SupportTelemetry:Enabled"] = "true",
                ["SupportTelemetry:EndpointUrl"] = "http://localhost:5099",
                ["SupportTelemetry:AllowHttp"] = "true",
                ["SupportTelemetry:ApiKey"] = "key-local-dev",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IServerInstanceIdentity>(new FakeInstanceIdentity("local-node-1"));
        services.AddSingleton(new RecentErrorBuffer(Options.Create(new RecentErrorBufferOptions { Capacity = 5 })));
        services.AddScoped<IReadinessCheckService>(_ => FakeReadinessCheck.NotReady());
        services.AddScoped<IOpsFindingsService>(_ => new ThrowingOpsFindingsService());
        services.AddSupportTelemetryPush(config);
        services.AddHttpClient(SupportTelemetryPushClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<SupportTelemetryPushClient>();

        var delivered = await client.PushAsync("TKT-42", CancellationToken.None);

        delivered.Should().BeFalse();
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri.Should().Be(new Uri("http://localhost:5099/api/v1/tickets/TKT-42/telemetry-snapshot"));

        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        doc.RootElement.GetProperty("health").GetProperty("readiness").GetString().Should().Be("unhealthy");
        doc.RootElement.GetProperty("opsFindings").GetArrayLength().Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ResolveArtifactDigest_PrefersConfiguredOverrideOverEnvironmentConfig()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SupportTelemetry:ArtifactDigest"] = "sha256:config-digest",
                ["HONUA_ARTIFACT_DIGEST"] = "sha256:env-digest",
            })
            .Build();

        ServerArtifactMetadata.ResolveArtifactDigest(config, "sha256:override-digest").Should().Be("sha256:override-digest");
        ServerArtifactMetadata.ResolveArtifactDigest(config).Should().Be("sha256:config-digest");

        var envOnly = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HONUA_ARTIFACT_DIGEST"] = "sha256:env-digest",
            })
            .Build();
        ServerArtifactMetadata.ResolveArtifactDigest(envOnly).Should().Be("sha256:env-digest");
    }

    private static ServiceProvider BuildFullTelemetryProvider(RecordingHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SupportTelemetry:Enabled"] = "true",
                ["SupportTelemetry:EndpointUrl"] = "https://support.honua.io",
                ["SupportTelemetry:ApiKey"] = "key-tenant-alpha",
                ["SupportTelemetry:InstanceUrl"] = "https://gis.alpha.example.com",
                ["HONUA_ARTIFACT_DIGEST"] = "sha256:1111222233334444",
                ["HONUA_PLATFORM_LOCK_DIGEST"] = "sha256:aaaabbbbccccdddd",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IServerInstanceIdentity>(new FakeInstanceIdentity("honua-prod-eu-1"));
        services.AddSingleton(CreateSeededErrorBuffer());
        services.AddSingleton(new MigrationState());
        services.AddScoped<IReadinessCheckService>(_ => FakeReadinessCheck.Ready());
        services.AddSingleton<IHttpRequestMetricsSnapshotProvider>(new FakeHttpMetricsProvider());
        services.AddSingleton<IDatabasePerformanceMetricsProvider>(new FakeDatabaseMetricsProvider());
        services.AddScoped<IOpsFindingsService>(_ => new FakeOpsFindingsService([CreateSampleFinding()]));
        services.AddSupportTelemetryPush(config);
        services.AddHttpClient(SupportTelemetryPushClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private static RecentErrorBuffer CreateSeededErrorBuffer()
    {
        var buffer = new RecentErrorBuffer(Options.Create(new RecentErrorBufferOptions { Capacity = 10 }));
        buffer.RecordSeed(new RecentErrorEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "corr-01",
            Path = "/ogc/features/collections",
            StatusCode = 500,
            Message = "Upstream auth failed with Bearer super-secret-token for ops@alpha.example.com",
        });
        return buffer;
    }

    private static OpsFinding CreateSampleFinding() => new()
    {
        Id = "finding-dlq-01",
        Rule = "alert-dispatch-backlog",
        Severity = OpsFindingSeverity.Warning,
        Title = "Dead-letter alert queue exceeded threshold",
        Explanation = "12 dispatches failed.",
        DetectedAt = DateTimeOffset.UtcNow,
        Subject = new OpsFindingSubject { TargetId = "alerts" },
        EvidenceRefs = ["alert-dlq"],
        RecommendedAction = new OpsFindingRecommendedAction
        {
            Kind = OperationClass.AdminConfigChange,
            Summary = "Redrive dead letters",
            ExecutionPayload = "{}",
            Reason = "Transient sink outage cleared",
            AutoSafe = false,
        },
    };

    private static void AssertValidIngestRequest(CapturedRequest captured)
    {
        captured.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri.Should().Be(new Uri("https://support.honua.io/api/v1/telemetry/ingest"));
        captured.AuthorizationScheme.Should().Be("Bearer");
        captured.AuthorizationParameter.Should().Be("key-tenant-alpha");
        captured.Body.Should().NotContain("super-secret-token");
        captured.Body.Should().NotContain("ops@alpha.example.com");

        using var doc = JsonDocument.Parse(captured.Body);
        var root = doc.RootElement;
        root.GetProperty("instanceId").GetString().Should().Be("honua-prod-eu-1");
        root.GetProperty("instanceUrl").GetString().Should().Be("https://gis.alpha.example.com");

        var health = root.GetProperty("health");
        health.GetProperty("readiness").GetString().Should().Be("healthy");
        health.GetProperty("liveness").GetString().Should().Be("healthy");
        health.GetProperty("artifactDigest").GetString().Should().Be("sha256:1111222233334444");
        health.GetProperty("platformLockDigest").GetString().Should().Be("sha256:aaaabbbbccccdddd");

        var metrics = root.GetProperty("metrics");
        metrics.GetProperty("httpP95DurationMs").GetDouble().Should().Be(420.5);
        metrics.GetProperty("httpServerErrorRate").GetDouble().Should().BeApproximately(0.05, 0.0001);
        metrics.GetProperty("dbCacheHitRate").GetDouble().Should().Be(0.92);

        root.GetProperty("recentErrors").GetArrayLength().Should().Be(1);
        var findings = root.GetProperty("opsFindings");
        findings.GetArrayLength().Should().Be(1);
        findings[0].GetProperty("findingId").GetString().Should().Be("finding-dlq-01");
        findings[0].GetProperty("rule").GetString().Should().Be("alert-dispatch-backlog");
    }

    private sealed class CapturedRequest
    {
        public required HttpMethod Method { get; init; }
        public Uri? RequestUri { get; init; }
        public string? AuthorizationScheme { get; init; }
        public string? AuthorizationParameter { get; init; }
        public required string Body { get; init; }
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(new CapturedRequest
            {
                Method = request.Method,
                RequestUri = request.RequestUri,
                AuthorizationScheme = request.Headers.Authorization?.Scheme,
                AuthorizationParameter = request.Headers.Authorization?.Parameter,
                Body = body,
            });
            return new HttpResponseMessage(statusCode);
        }
    }

    private sealed class FakeInstanceIdentity(string instanceId) : IServerInstanceIdentity
    {
        public string InstanceId { get; } = instanceId;
        public string Source => ServerInstanceIdentitySources.Configuration;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    }

    private sealed class FakeReadinessCheck : IReadinessCheckService
    {
        private readonly ReadinessResult _result;

        private FakeReadinessCheck(ReadinessResult result) => _result = result;

        public static FakeReadinessCheck Ready() => new(ReadinessResult.Ready());

        public static FakeReadinessCheck NotReady() => new(new ReadinessResult
        {
            IsReady = false,
            StatusCode = 503,
            Message = "Not ready",
        });

        public Task<ReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }

    private sealed class FakeHttpMetricsProvider : IHttpRequestMetricsSnapshotProvider
    {
        public HttpRequestMetricsSnapshot GetHttpRequestMetricsSnapshot() => new()
        {
            TotalRequests = 100,
            TotalServerErrors = 5,
            TotalClientErrors = 2,
            ActiveRequests = 1,
            AvgDurationMs = 110.0,
            MaxDurationMs = 950.0,
            P95DurationMs = 420.5,
            SlowRequestCount = 3,
            SlowRequestThresholdMs = 500.0,
        };
    }

    private sealed class FakeDatabaseMetricsProvider : IDatabasePerformanceMetricsProvider
    {
        public DatabasePerformanceMetricsSnapshot GetMetrics() => new()
        {
            CacheHitRate = 0.92,
            CacheHits = 920,
            CacheMisses = 80,
        };
    }

    private sealed class FakeOpsFindingsService(IReadOnlyList<OpsFinding> findings) : IOpsFindingsService
    {
        public Task<IReadOnlyList<OpsFinding>> EvaluateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(findings);

        public Task<OpsFindingProposalResult> ProposeAsync(
            string findingId,
            string? rootProvisioningOperationId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingOpsFindingsService : IOpsFindingsService
    {
        public Task<IReadOnlyList<OpsFinding>> EvaluateAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated DB outage during findings evaluation");

        public Task<OpsFindingProposalResult> ProposeAsync(
            string findingId,
            string? rootProvisioningOperationId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
