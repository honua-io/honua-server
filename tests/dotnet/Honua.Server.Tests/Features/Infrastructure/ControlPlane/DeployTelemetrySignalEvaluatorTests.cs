// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class DeployTelemetrySignalEvaluatorTests
{
    [Fact]
    public async Task EvaluateAsync_UsesDefaultKubernetesPolicy_WhenOnlyConnectionAndJobAreConfigured()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(
            capturedQueries,
            responses: CreateSuccessfulResponses("25", "0.01", "150"));

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod"
            }));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeFalse();
        decision.RollbackRecommended.Should().BeFalse();

        var queries = capturedQueries.ToArray();
        queries.Should().HaveCount(3);
        queries[0].Should().Contain("honua_http_request_total{job=\"honua-prod\"}[5m]");
        queries[1].Should().Contain("honua_http_request_total{job=\"honua-prod\",status_code=~\"5..\"}[5m]");
        queries[2].Should().Contain("honua_http_request_duration_ms_bucket{job=\"honua-prod\"}[5m]");
    }

    [Fact]
    public async Task EvaluateAsync_UsesAwsAlbCanaryPreset_WhenPolicyIsConfigured()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(
            capturedQueries,
            responses: CreateSuccessfulResponses("12", "0.01", "120"));

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.policy"] = "aws-alb-canary",
                ["telemetry.prometheus.canary_job"] = "honua-ecs-canary"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeFalse();
        decision.RollbackRecommended.Should().BeFalse();

        var queries = capturedQueries.ToArray();
        queries.Should().HaveCount(3);
        queries[0].Should().Contain("honua_http_request_total{job=\"honua-ecs-canary\"}[5m]");
        queries[1].Should().Contain("honua_http_request_total{job=\"honua-ecs-canary\",status_code=~\"5..\"}[5m]");
        queries[2].Should().Contain("honua_http_request_duration_ms_bucket{job=\"honua-ecs-canary\"}[5m]");
    }

    [Fact]
    public async Task EvaluateAsync_UsesAwsAlbCanaryPreset_ByDefaultForAwsEcsTargets()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(
            capturedQueries,
            responses: CreateSuccessfulResponses("12", "0.01", "120"));

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.AwsEcs,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.canary_job"] = "honua-ecs-canary"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeFalse();
        decision.RollbackRecommended.Should().BeFalse();

        var queries = capturedQueries.ToArray();
        queries.Should().HaveCount(3);
        queries[0].Should().Contain("honua_http_request_total{job=\"honua-ecs-canary\"}[5m]");
        queries[1].Should().Contain("honua_http_request_total{job=\"honua-ecs-canary\",status_code=~\"5..\"}[5m]");
        queries[2].Should().Contain("honua_http_request_duration_ms_bucket{job=\"honua-ecs-canary\"}[5m]");
    }

    [Fact]
    public async Task EvaluateAsync_AwsEcsCanaryWeight_DefaultsToAwsAlbCanaryPreset()
    {
        // Without telemetry.policy or canary_selector/canary_job, the runbook
        // says the aws-alb-canary preset is selected for ECS canary deploys.
        // Verify by inspecting the resulting Prometheus queries — the preset
        // builds a canary-scoped selector around the default canary job rather
        // than the aggregate honua-http selector.
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(
            capturedQueries,
            responses: CreateSuccessfulResponses("12", "0.01", "120"));

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.AwsEcs,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["aws.ecs.canary_weight_percentage"] = "10"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeFalse();

        var queries = capturedQueries.ToArray();
        // The aws-alb-canary preset uses DefaultCanaryPrometheusJob = "honua-canary"
        // when no explicit canary selector or job is configured. The honua-http
        // preset would have used "honua".
        queries.Should().NotBeEmpty();
        queries[0].Should().Contain("job=\"honua-canary\"");
    }

    [Fact]
    public async Task EvaluateAsync_GenericDeploymentCanaryWeight_DefaultsToAwsAlbCanaryPresetForAwsEcs()
    {
        // Operators can set the generic deployment.canary_weight_percentage
        // key instead of the ECS-specific alias; the same preset selection
        // applies.
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(
            capturedQueries,
            responses: CreateSuccessfulResponses("12", "0.01", "120"));

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.AwsEcs,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["deployment.canary_weight_percentage"] = "20",
                ["telemetry.prometheus.canary_job"] = "honua-ecs-canary"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeFalse();

        var queries = capturedQueries.ToArray();
        queries[0].Should().Contain("job=\"honua-ecs-canary\"");
    }

    [Fact]
    public async Task EvaluateAsync_UsesDefaultHonuaHttpPreset_ForAzureContainerAppsTargets()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(
            capturedQueries,
            responses: CreateSuccessfulResponses("25", "0.02", "130"));

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.AzureContainerApps,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-aca"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();

        var queries = capturedQueries.ToArray();
        queries.Should().HaveCount(3);
        queries[0].Should().Contain("honua_http_request_total{job=\"honua-aca\"}[5m]");
        queries[1].Should().Contain("honua_http_request_total{job=\"honua-aca\",status_code=~\"5..\"}[5m]");
        queries[2].Should().Contain("honua_http_request_duration_ms_bucket{job=\"honua-aca\"}[5m]");
    }

    [Fact]
    public async Task EvaluateAsync_UsesExplicitQueryOverrides_WhenProvidedAlongsidePreset()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(
            capturedQueries,
            responses: CreateSuccessfulResponses("42", "0.01", "100"));

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.policy"] = "kubernetes-honua-http",
                ["telemetry.prometheus.job"] = "honua-prod",
                ["telemetry.sample_count.query"] = "sum(custom_canary_requests_total)"
            }));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeFalse();

        var queries = capturedQueries.ToArray();
        queries.Should().HaveCount(3);
        queries[0].Should().Be("sum(custom_canary_requests_total)");
        queries[1].Should().Contain("honua_http_request_total{job=\"honua-prod\",status_code=~\"5..\"}[5m]");
        queries[2].Should().Contain("honua_http_request_duration_ms_bucket{job=\"honua-prod\"}[5m]");
    }

    [Fact]
    public async Task EvaluateAsync_AwsLambdaCanary_AcceptsExplicitQueryOverridesWithoutCanarySelector()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(
            capturedQueries,
            responses: CreateSuccessfulResponses("18", "0.01", "120"));

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.AwsLambda,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.policy"] = "aws-lambda-canary",
                ["telemetry.error_rate.query"] = "sum(rate(lambda_function_errors_total[5m])) / clamp_min(sum(rate(lambda_function_invocations_total[5m])), 0.001)",
                ["telemetry.error_rate.threshold"] = "0.05",
                ["telemetry.latency_p95.query"] = "histogram_quantile(0.95, sum(rate(lambda_function_duration_ms_bucket[5m])) by (le))",
                ["telemetry.latency_p95.threshold_ms"] = "2000",
                ["telemetry.sample_count.query"] = "sum(rate(lambda_function_invocations_total[5m])) * 300",
                ["telemetry.sample_count.minimum"] = "10"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeFalse();
        decision.RollbackRecommended.Should().BeFalse();

        var queries = capturedQueries.ToArray();
        queries.Should().HaveCount(3);
        queries[0].Should().Be("sum(rate(lambda_function_invocations_total[5m])) * 300");
        queries[1].Should().Contain("lambda_function_errors_total");
        queries[2].Should().Contain("lambda_function_duration_ms_bucket");
    }

    [Fact]
    public async Task EvaluateAsync_WithPrivateTelemetryBaseUrl_DoesNotSendRequests()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(
            capturedQueries,
            connection: new DeployTelemetryConnectionOptions
            {
                ConnectionId = "prod-prom",
                Provider = "prometheus",
                BaseUrl = "https://localhost",
                TimeoutSeconds = 2
            });

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod"
            }));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeTrue();
        capturedQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_WithDisallowedAuthHeader_DoesNotSendRequests()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(
            capturedQueries,
            connection: new DeployTelemetryConnectionOptions
            {
                ConnectionId = "prod-prom",
                Provider = "prometheus",
                BaseUrl = "https://example.com",
                AuthHeaderName = "Host",
                AuthHeaderValue = "internal.example",
                TimeoutSeconds = 2
            });

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod"
            }));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeTrue();
        capturedQueries.Should().BeEmpty();
    }

    private static PrometheusDeployTelemetrySignalEvaluator CreateEvaluator(
        ConcurrentQueue<string> capturedQueries,
        DeployTelemetryConnectionOptions? connection = null,
        params string[] responses)
    {
        var responseQueue = new ConcurrentQueue<string>(responses);
        var handler = new DelegateHttpMessageHandler(request =>
        {
            var query = request.RequestUri is null
                ? string.Empty
                : Uri.UnescapeDataString(request.RequestUri.Query.TrimStart('?').Replace("query=", string.Empty, StringComparison.Ordinal));
            capturedQueries.Enqueue(query);

            responseQueue.TryDequeue(out var responseJson);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseJson ?? """{"status":"success","data":{"resultType":"vector","result":[]}}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        return new PrometheusDeployTelemetrySignalEvaluator(
            new TestControlPlaneOptionsMonitor(new ControlPlaneOptions
            {
                TelemetryConnections =
                [
                    connection ?? new DeployTelemetryConnectionOptions
                    {
                        ConnectionId = "prod-prom",
                        Provider = "prometheus",
                        BaseUrl = "https://example.com",
                        TimeoutSeconds = 2
                    }
                ]
            }),
            new StubHttpClientFactory(new HttpClient(handler)),
            NullLogger<PrometheusDeployTelemetrySignalEvaluator>.Instance);
    }

    private static string[] CreateSuccessfulResponses(string sampleCount, string errorRate, string latencyP95)
        =>
        [
            CreateSuccessResponse(sampleCount),
            CreateSuccessResponse(errorRate),
            CreateSuccessResponse(latencyP95)
        ];

    private static string CreateSuccessResponse(string value)
        => $@"{{""status"":""success"",""data"":{{""resultType"":""vector"",""result"":[{{""metric"":{{}},""value"":[1710000000,""{value}""]}}]}}}}";

    private static WorkflowOperationRecord CreateOperation(
        DeployTargetKind targetKind,
        IReadOnlyDictionary<string, string> parameters,
        DateTimeOffset? createdAt = null)
    {
        var now = createdAt ?? DateTimeOffset.UtcNow.AddMinutes(-4);
        return new WorkflowOperationRecord
        {
            OperationId = $"deploy-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Reconciling,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Reconciling rollout",
            Audit = new OperationAuditInfo(),
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = "production:prod-api",
                RequiresExclusiveLease = true
            },
            Deploy = new DeployOperationSpec
            {
                TargetId = "prod-api",
                TargetKind = targetKind,
                Backend = "honua-gitops-kubernetes",
                Environment = "production",
                TargetName = "honua-server",
                ArtifactReference = "ghcr.io/honua/server",
                RuntimeProfile = "dotnet-api",
                CurrentRevision = "sha256:old",
                DesiredRevision = "sha256:new",
                Parameters = new Dictionary<string, string>(parameters, StringComparer.Ordinal)
            }
        };
    }

    private sealed class TestControlPlaneOptionsMonitor(ControlPlaneOptions currentValue) : IOptionsMonitor<ControlPlaneOptions>
    {
        public ControlPlaneOptions CurrentValue => currentValue;

        public ControlPlaneOptions Get(string? name) => currentValue;

        public IDisposable? OnChange(Action<ControlPlaneOptions, string?> listener) => null;
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
