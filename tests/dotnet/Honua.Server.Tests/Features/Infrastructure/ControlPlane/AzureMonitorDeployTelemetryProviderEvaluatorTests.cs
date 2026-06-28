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
/// Verifies the Azure Monitor deploy telemetry provider end-to-end through the multi-provider
/// dispatcher with a faked Log Analytics query client (no live Azure subscription required). Mirrors
/// <see cref="CloudWatchDeployTelemetryProviderEvaluatorTests"/> so the Azure evaluator is held to
/// the same promote/rollback/wait contract as the AWS one.
/// </summary>
public sealed class AzureMonitorDeployTelemetryProviderEvaluatorTests
{
    private const string ErrorRateQuery = "requests | summarize errors=todouble(countif(success==false))/count()";
    private const string LatencyQuery = "requests | summarize percentile(duration, 95)";
    private const string SampleQuery = "requests | summarize count()";
    private const string WorkspaceId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task EvaluateAsync_HealthyAzureMonitorMetrics_ReturnsPromoteSignal()
    {
        var client = new FakeAzureMonitorMetricClient(new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            [SampleQuery] = 50,
            [ErrorRateQuery] = 0.01,
            [LatencyQuery] = 120
        });

        var decision = await Evaluate(client);

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeFalse();
        decision.RollbackRecommended.Should().BeFalse();
        client.RequestedQueries.Should().Contain([SampleQuery, ErrorRateQuery, LatencyQuery]);
    }

    [Fact]
    public async Task EvaluateAsync_BreachingErrorRate_ReturnsRollbackSignal()
    {
        var client = new FakeAzureMonitorMetricClient(new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            [SampleQuery] = 50,
            [ErrorRateQuery] = 0.4,
            [LatencyQuery] = 120
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
        var client = new FakeAzureMonitorMetricClient(new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            [SampleQuery] = 50,
            [ErrorRateQuery] = 0.01,
            [LatencyQuery] = 9000
        });

        var decision = await Evaluate(client);

        decision.Should().NotBeNull();
        decision!.RollbackRecommended.Should().BeTrue();
        decision.Message.Should().Contain("latency");
    }

    [Fact]
    public async Task EvaluateAsync_BelowMinimumSampleCount_WaitsForMoreTelemetry()
    {
        var client = new FakeAzureMonitorMetricClient(new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            [SampleQuery] = 3,
            [ErrorRateQuery] = 0.4,
            [LatencyQuery] = 9000
        });

        var decision = await Evaluate(client);

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeTrue();
        decision.RollbackRecommended.Should().BeFalse();
        // Error-rate / latency must not be read until the minimum sample gate clears.
        client.RequestedQueries.Should().ContainSingle().Which.Should().Be(SampleQuery);
    }

    [Fact]
    public async Task EvaluateAsync_WithoutExplicitQueries_FailsClosedToWait()
    {
        // Azure Monitor has no preset query dialect (presets emit PromQL); a connection that relies
        // on a preset must fall back to a wait rather than shipping PromQL to Log Analytics.
        var client = new FakeAzureMonitorMetricClient(new Dictionary<string, double?>(StringComparer.Ordinal));

        var evaluator = CreateDispatcher(client);
        var decision = await evaluator.EvaluateAsync(CreateOperation(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-azmon",
            ["telemetry.policy"] = "azure-aca-canary",
            ["telemetry.prometheus.canary_job"] = "honua-canary"
        }));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeTrue();
        client.RequestedQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_PassesWorkspaceId_FromConnectionRegion()
    {
        var client = new FakeAzureMonitorMetricClient(new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            [SampleQuery] = 50,
            [ErrorRateQuery] = 0.01,
            [LatencyQuery] = 120
        });

        await Evaluate(client);

        client.LastWorkspaceId.Should().Be(WorkspaceId);
    }

    [Fact]
    public async Task EvaluateAsync_StandardPublicEndpoint_DoesNotForwardEndpointOverride()
    {
        // The public Log Analytics endpoint is the client default and must NOT be forwarded as an
        // override, so production connections keep the built-in endpoint + audience.
        var client = new FakeAzureMonitorMetricClient(new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            [SampleQuery] = 50,
            [ErrorRateQuery] = 0.01,
            [LatencyQuery] = 120
        });

        await Evaluate(client, baseUrl: "https://api.loganalytics.io");

        client.LastEndpointOverride.Should().BeNull();
    }

    [Fact]
    public async Task EvaluateAsync_SovereignEndpoint_ForwardsEndpointOverride()
    {
        // A non-default Log Analytics endpoint (sovereign cloud) must be forwarded so the gate signs
        // and queries against the matching authority.
        var client = new FakeAzureMonitorMetricClient(new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            [SampleQuery] = 50,
            [ErrorRateQuery] = 0.01,
            [LatencyQuery] = 120
        });

        await Evaluate(client, baseUrl: "https://api.loganalytics.us");

        client.LastEndpointOverride.Should().Be("https://api.loganalytics.us");
    }

    private static async Task<DeployTelemetryDecision?> Evaluate(
        FakeAzureMonitorMetricClient client,
        string baseUrl = "https://api.loganalytics.io")
    {
        var evaluator = CreateDispatcher(client, baseUrl);
        return await evaluator.EvaluateAsync(CreateOperation(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-azmon",
            ["telemetry.error_rate.query"] = ErrorRateQuery,
            ["telemetry.error_rate.threshold"] = "0.05",
            ["telemetry.latency_p95.query"] = LatencyQuery,
            ["telemetry.latency_p95.threshold_ms"] = "2000",
            ["telemetry.sample_count.query"] = SampleQuery,
            ["telemetry.sample_count.minimum"] = "10"
        }));
    }

    private static DeployTelemetrySignalEvaluator CreateDispatcher(
        FakeAzureMonitorMetricClient client,
        string baseUrl = "https://api.loganalytics.io")
    {
        var azureMonitorProvider = new AzureMonitorDeployTelemetryProviderEvaluator(client);
        var options = new ControlPlaneOptions
        {
            TelemetryConnections =
            [
                new DeployTelemetryConnectionOptions
                {
                    ConnectionId = "prod-azmon",
                    Provider = "azuremonitor",
                    BaseUrl = baseUrl,
                    Region = WorkspaceId,
                    TimeoutSeconds = 2
                }
            ]
        };

        return new DeployTelemetrySignalEvaluator(
            new TestControlPlaneOptionsMonitor(options),
            [azureMonitorProvider],
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
                TargetKind = DeployTargetKind.AzureContainerApps,
                Backend = "honua-azure-container-apps-revision",
                Environment = "production",
                TargetName = "honua-server",
                ArtifactReference = "ghcr.io/honua/server",
                RuntimeProfile = "dotnet-api",
                CurrentRevision = "honua-server--old",
                DesiredRevision = "honua-server--new",
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

    private sealed class FakeAzureMonitorMetricClient(IReadOnlyDictionary<string, double?> values) : IAzureMonitorMetricClient
    {
        public ConcurrentQueue<string> RequestedQueries { get; } = new();

        public string? LastWorkspaceId { get; private set; }

        public string? LastEndpointOverride { get; private set; }

        public Task<double?> GetScalarValueAsync(
            string workspaceId,
            string query,
            TimeSpan window,
            string? endpointOverride = null,
            CancellationToken cancellationToken = default)
        {
            LastWorkspaceId = workspaceId;
            LastEndpointOverride = endpointOverride;
            RequestedQueries.Enqueue(query);
            return Task.FromResult(values.TryGetValue(query, out var value) ? value : null);
        }
    }
}
