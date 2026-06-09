// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Verifies the CloudWatch deploy telemetry provider end-to-end through the multi-provider
/// dispatcher with a faked CloudWatch metric client (no live AWS account required).
/// </summary>
public sealed class CloudWatchDeployTelemetryProviderEvaluatorTests
{
    private const string ErrorRateExpression = "errors / requests";
    private const string LatencyExpression = "p95_latency";
    private const string SampleExpression = "request_count";

    [Fact]
    public async Task EvaluateAsync_HealthyCloudWatchMetrics_ReturnsPromoteSignal()
    {
        var client = new FakeCloudWatchMetricClient(new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            [SampleExpression] = 50,
            [ErrorRateExpression] = 0.01,
            [LatencyExpression] = 120
        });

        var decision = await Evaluate(client);

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeFalse();
        decision.RollbackRecommended.Should().BeFalse();
        client.RequestedExpressions.Should().Contain([SampleExpression, ErrorRateExpression, LatencyExpression]);
    }

    [Fact]
    public async Task EvaluateAsync_BreachingErrorRate_ReturnsRollbackSignal()
    {
        var client = new FakeCloudWatchMetricClient(new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            [SampleExpression] = 50,
            [ErrorRateExpression] = 0.4,
            [LatencyExpression] = 120
        });

        var decision = await Evaluate(client);

        decision.Should().NotBeNull();
        decision!.RollbackRecommended.Should().BeTrue();
        decision.WaitForMoreTelemetry.Should().BeFalse();
        decision.Message.Should().Contain("error rate");
    }

    [Fact]
    public async Task EvaluateAsync_BreachingLatency_ReturnsRollbackSignal()
    {
        var client = new FakeCloudWatchMetricClient(new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            [SampleExpression] = 50,
            [ErrorRateExpression] = 0.01,
            [LatencyExpression] = 9000
        });

        var decision = await Evaluate(client);

        decision.Should().NotBeNull();
        decision!.RollbackRecommended.Should().BeTrue();
        decision.Message.Should().Contain("latency");
    }

    [Fact]
    public async Task EvaluateAsync_BelowMinimumSampleCount_WaitsForMoreTelemetry()
    {
        var client = new FakeCloudWatchMetricClient(new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            [SampleExpression] = 3,
            [ErrorRateExpression] = 0.4,
            [LatencyExpression] = 9000
        });

        var decision = await Evaluate(client);

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeTrue();
        decision.RollbackRecommended.Should().BeFalse();
        // Error-rate / latency must not be read until the minimum sample gate clears.
        client.RequestedExpressions.Should().ContainSingle().Which.Should().Be(SampleExpression);
    }

    [Fact]
    public async Task EvaluateAsync_WithoutExplicitQueries_FailsClosedToWait()
    {
        // CloudWatch has no preset query dialect (presets emit PromQL); a connection that relies on
        // a preset must fall back to a wait rather than shipping PromQL to CloudWatch.
        var client = new FakeCloudWatchMetricClient(new Dictionary<string, double?>(StringComparer.Ordinal));

        var evaluator = CreateDispatcher(client);
        var decision = await evaluator.EvaluateAsync(CreateOperation(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-cw",
            ["telemetry.policy"] = "aws-lambda-canary",
            ["telemetry.prometheus.canary_job"] = "honua-canary"
        }));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeTrue();
        client.RequestedExpressions.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_ResolvesRegion_FromConnectionRegion()
    {
        var client = new FakeCloudWatchMetricClient(new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            [SampleExpression] = 50,
            [ErrorRateExpression] = 0.01,
            [LatencyExpression] = 120
        });

        await Evaluate(client, region: "eu-west-1");

        client.LastRegion.Should().Be("eu-west-1");
    }

    [Fact]
    public async Task EvaluateAsync_InfersRegion_FromRegionalBaseUrl()
    {
        var client = new FakeCloudWatchMetricClient(new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            [SampleExpression] = 50,
            [ErrorRateExpression] = 0.01,
            [LatencyExpression] = 120
        });

        await Evaluate(client, baseUrl: "https://monitoring.us-east-2.amazonaws.com");

        client.LastRegion.Should().Be("us-east-2");
    }

    private static async Task<DeployTelemetryDecision?> Evaluate(
        FakeCloudWatchMetricClient client,
        string? region = null,
        string baseUrl = "https://monitoring.us-east-1.amazonaws.com")
    {
        var evaluator = CreateDispatcher(client, region, baseUrl);
        return await evaluator.EvaluateAsync(CreateOperation(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-cw",
            ["telemetry.error_rate.query"] = ErrorRateExpression,
            ["telemetry.error_rate.threshold"] = "0.05",
            ["telemetry.latency_p95.query"] = LatencyExpression,
            ["telemetry.latency_p95.threshold_ms"] = "2000",
            ["telemetry.sample_count.query"] = SampleExpression,
            ["telemetry.sample_count.minimum"] = "10"
        }));
    }

    private static DeployTelemetrySignalEvaluator CreateDispatcher(
        FakeCloudWatchMetricClient client,
        string? region = null,
        string baseUrl = "https://monitoring.us-east-1.amazonaws.com")
    {
        var cloudWatchProvider = new CloudWatchDeployTelemetryProviderEvaluator(client);
        var options = new ControlPlaneOptions
        {
            TelemetryConnections =
            [
                new DeployTelemetryConnectionOptions
                {
                    ConnectionId = "prod-cw",
                    Provider = "cloudwatch",
                    BaseUrl = baseUrl,
                    Region = region,
                    TimeoutSeconds = 2
                }
            ]
        };

        return new DeployTelemetrySignalEvaluator(
            new TestControlPlaneOptionsMonitor(options),
            [cloudWatchProvider],
            NullLogger<DeployTelemetrySignalEvaluator>.Instance);
    }

    private static WorkflowOperationRecord CreateOperation(IReadOnlyDictionary<string, string> parameters)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
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
                TargetKind = DeployTargetKind.AwsEcs,
                Backend = "honua-aws-ecs-alb",
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

    private sealed class FakeCloudWatchMetricClient(IReadOnlyDictionary<string, double?> values) : ICloudWatchMetricClient
    {
        public ConcurrentQueue<string> RequestedExpressions { get; } = new();

        public string? LastRegion { get; private set; }

        public Task<double?> GetExpressionValueAsync(
            string? region,
            string expression,
            DateTime startUtc,
            DateTime endUtc,
            int periodSeconds,
            CancellationToken cancellationToken)
        {
            LastRegion = region;
            RequestedExpressions.Enqueue(expression);
            return Task.FromResult(values.TryGetValue(expression, out var value) ? value : null);
        }
    }
}
