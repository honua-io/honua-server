// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
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

    [Fact]
    public async Task EvaluateAsync_WithUnsupportedProvider_WaitsWithExplicitMessage_AndDoesNotQuery()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(
            capturedQueries,
            connection: new DeployTelemetryConnectionOptions
            {
                ConnectionId = "prod-prom",
                Provider = "datadog",
                BaseUrl = "https://api.datadoghq.com",
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
        decision.RollbackRecommended.Should().BeFalse();
        decision.Message.Should().Contain("datadog");
        decision.Message.Should().Contain("not supported");
        capturedQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_SelectsProviderByConnectionProvider()
    {
        // Two providers registered; the connection's Provider ("fake-metrics") selects the fake
        // one rather than Prometheus, proving dispatch is driven by the connection.
        var fakeProvider = new FakeProviderEvaluator("fake-metrics", new DeployTelemetryReadings
        {
            SampleCount = 50,
            ErrorRate = 0.01,
            LatencyP95 = 120
        });
        var prometheusProvider = new PrometheusDeployTelemetryProviderEvaluator(
            new StubHttpClientFactory(new HttpClient(new DelegateHttpMessageHandler(_ =>
                throw new InvalidOperationException("Prometheus provider must not be invoked.")))));

        var evaluator = new DeployTelemetrySignalEvaluator(
            new TestControlPlaneOptionsMonitor(new ControlPlaneOptions
            {
                TelemetryConnections =
                [
                    new DeployTelemetryConnectionOptions
                    {
                        ConnectionId = "prod-metrics",
                        Provider = "fake-metrics",
                        BaseUrl = "https://example.com",
                        TimeoutSeconds = 2
                    }
                ]
            }),
            [prometheusProvider, fakeProvider],
            NullLogger<DeployTelemetrySignalEvaluator>.Instance);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.AwsEcs,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-metrics",
                ["telemetry.policy"] = "aws-lambda-canary",
                ["telemetry.error_rate.query"] = "errors / requests",
                ["telemetry.error_rate.threshold"] = "0.05",
                ["telemetry.latency_p95.query"] = "p95",
                ["telemetry.latency_p95.threshold_ms"] = "2000",
                ["telemetry.sample_count.query"] = "requests",
                ["telemetry.sample_count.minimum"] = "10"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeFalse();
        decision.RollbackRecommended.Should().BeFalse();
        fakeProvider.WasInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_ProviderReadingBreachesThreshold_RecommendsRollback()
    {
        var fakeProvider = new FakeProviderEvaluator("fake-metrics", new DeployTelemetryReadings
        {
            SampleCount = 50,
            ErrorRate = 0.5,
            LatencyP95 = 120
        });

        var evaluator = new DeployTelemetrySignalEvaluator(
            new TestControlPlaneOptionsMonitor(new ControlPlaneOptions
            {
                TelemetryConnections =
                [
                    new DeployTelemetryConnectionOptions
                    {
                        ConnectionId = "prod-metrics",
                        Provider = "fake-metrics",
                        BaseUrl = "https://example.com",
                        TimeoutSeconds = 2
                    }
                ]
            }),
            [fakeProvider],
            NullLogger<DeployTelemetrySignalEvaluator>.Instance);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.AwsEcs,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-metrics",
                ["telemetry.error_rate.query"] = "errors / requests",
                ["telemetry.error_rate.threshold"] = "0.05",
                ["telemetry.sample_count.query"] = "requests",
                ["telemetry.sample_count.minimum"] = "10"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.RollbackRecommended.Should().BeTrue();
        decision.WaitForMoreTelemetry.Should().BeFalse();
        decision.Message.Should().Contain("error rate");
    }

    // ---- health-gate boundary + anti-flap (#2161) ------------------------

    [Fact]
    public void Evaluate_AtExactThreshold_DoesNotRollback()
    {
        // The gate uses strict '>' so a reading AT the threshold is healthy, not a breach. This pins
        // the boundary so a refactor to '>=' cannot silently start rolling back at-threshold deploys.
        var policy = CreateThresholdPolicy(errorThreshold: 0.05, latencyThreshold: 2000);
        var readings = new DeployTelemetryReadings
        {
            SampleCount = 100,
            ErrorRate = 0.05,
            LatencyP95 = 2000
        };

        var decision = DeployTelemetrySignalEvaluator.Evaluate(policy, readings);

        decision.RollbackRecommended.Should().BeFalse("a reading exactly at the threshold is within bounds");
        decision.WaitForMoreTelemetry.Should().BeFalse();
        decision.Message.Should().Contain("passed");
    }

    [Fact]
    public void Evaluate_OneScrapeOverThreshold_RecommendsRollback()
    {
        var policy = CreateThresholdPolicy(errorThreshold: 0.05, latencyThreshold: 2000);
        var readings = new DeployTelemetryReadings
        {
            SampleCount = 100,
            ErrorRate = 0.0500001,
            LatencyP95 = 1000
        };

        var decision = DeployTelemetrySignalEvaluator.Evaluate(policy, readings);

        decision.RollbackRecommended.Should().BeTrue("a single reading over the threshold breaches the instantaneous gate");
        decision.Message.Should().Contain("error rate");
    }

    [Fact]
    public async Task EvaluateAsync_DebounceConfigured_SingleNoisyScrape_DoesNotRollback()
    {
        // GP metrics are burstier: with the opt-in N-consecutive-breach debounce a single breaching
        // scrape must NOT trigger a production rollback. The evaluator suppresses the rollback and
        // records a breach streak of 1.
        var fakeProvider = new FakeProviderEvaluator("fake-metrics", new DeployTelemetryReadings
        {
            SampleCount = 50,
            ErrorRate = 0.5,
            LatencyP95 = 120
        });

        var evaluator = CreateProviderEvaluator(fakeProvider);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.AwsEcs,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-metrics",
                ["telemetry.error_rate.query"] = "errors / requests",
                ["telemetry.error_rate.threshold"] = "0.05",
                ["telemetry.sample_count.query"] = "requests",
                ["telemetry.sample_count.minimum"] = "10",
                ["telemetry.rollback.consecutive_breaches"] = "3"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.RollbackRecommended.Should().BeFalse("one breach is below the configured debounce threshold of 3");
        decision.WaitForMoreTelemetry.Should().BeTrue();
        decision.UpdatedDeployParameters.Should().NotBeNull();
        decision.UpdatedDeployParameters!["telemetry.rollback.breach_streak"].Should().Be("1");
    }

    [Fact]
    public async Task EvaluateAsync_DebounceConfigured_HealthyScrapeResetsBreachStreak()
    {
        // breach → recover: a healthy scrape after a prior breach must reset the streak so a later
        // single breach does not piggy-back on stale state and trip the gate.
        var fakeProvider = new FakeProviderEvaluator("fake-metrics", new DeployTelemetryReadings
        {
            SampleCount = 50,
            ErrorRate = 0.01,
            LatencyP95 = 120
        });

        var evaluator = CreateProviderEvaluator(fakeProvider);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.AwsEcs,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-metrics",
                ["telemetry.error_rate.query"] = "errors / requests",
                ["telemetry.error_rate.threshold"] = "0.05",
                ["telemetry.sample_count.query"] = "requests",
                ["telemetry.sample_count.minimum"] = "10",
                ["telemetry.rollback.consecutive_breaches"] = "3",
                // A prior breach streak that a healthy scrape must clear.
                ["telemetry.rollback.breach_streak"] = "2"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.RollbackRecommended.Should().BeFalse();
        decision.UpdatedDeployParameters.Should().NotBeNull();
        decision.UpdatedDeployParameters!["telemetry.rollback.breach_streak"].Should().Be("0", "a healthy scrape resets the streak");
    }

    [Fact]
    public async Task EvaluateAsync_DebounceConfigured_NSustainedBreaches_RecommendsRollback()
    {
        // N sustained breaches DOES roll back: with a prior streak of N-1 the next breach reaches the
        // configured threshold and the rollback fires.
        var fakeProvider = new FakeProviderEvaluator("fake-metrics", new DeployTelemetryReadings
        {
            SampleCount = 50,
            ErrorRate = 0.5,
            LatencyP95 = 120
        });

        var evaluator = CreateProviderEvaluator(fakeProvider);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.AwsEcs,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-metrics",
                ["telemetry.error_rate.query"] = "errors / requests",
                ["telemetry.error_rate.threshold"] = "0.05",
                ["telemetry.sample_count.query"] = "requests",
                ["telemetry.sample_count.minimum"] = "10",
                ["telemetry.rollback.consecutive_breaches"] = "3",
                ["telemetry.rollback.breach_streak"] = "2"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.RollbackRecommended.Should().BeTrue("the third consecutive breach reaches the debounce threshold");
        decision.WaitForMoreTelemetry.Should().BeFalse();
        decision.Message.Should().Contain("error rate");
    }

    [Fact]
    public async Task EvaluateAsync_DebounceNotConfigured_PreservesInstantaneousRollback()
    {
        // Default (no debounce param): behavior is unchanged — a single breach rolls back immediately.
        var fakeProvider = new FakeProviderEvaluator("fake-metrics", new DeployTelemetryReadings
        {
            SampleCount = 50,
            ErrorRate = 0.5,
            LatencyP95 = 120
        });

        var evaluator = CreateProviderEvaluator(fakeProvider);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.AwsEcs,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-metrics",
                ["telemetry.error_rate.query"] = "errors / requests",
                ["telemetry.error_rate.threshold"] = "0.05",
                ["telemetry.sample_count.query"] = "requests",
                ["telemetry.sample_count.minimum"] = "10"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.RollbackRecommended.Should().BeTrue();
        decision.UpdatedDeployParameters.Should().BeNull("no debounce configured means no streak bookkeeping");
    }

    [Fact]
    public async Task EvaluateAsync_GpBatchPolicy_SingleBreach_DefaultsToBurstTolerantAntiFlap()
    {
        // GP substrate deploys (telemetry.policy = gp-batch) are burstier, so with NO explicit
        // consecutive-breach param the gate defaults to the burst-tolerant streak (3) rather than
        // the single-scrape default — one breaching scrape must NOT roll back (honua-server#2165).
        var fakeProvider = new FakeProviderEvaluator("fake-metrics", new DeployTelemetryReadings
        {
            SampleCount = 50,
            ErrorRate = 0.5,
            LatencyP95 = 120
        });

        var evaluator = CreateProviderEvaluator(fakeProvider);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.AwsEcs,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-metrics",
                ["telemetry.policy"] = "gp-batch",
                ["telemetry.prometheus.job"] = "honua-gp",
                ["telemetry.error_rate.query"] = "errors / requests",
                ["telemetry.error_rate.threshold"] = "0.05",
                ["telemetry.sample_count.query"] = "requests",
                ["telemetry.sample_count.minimum"] = "10"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-6)));

        decision.Should().NotBeNull();
        decision!.RollbackRecommended.Should().BeFalse("the GP-batch policy defaults to a burst-tolerant anti-flap streak");
        decision.WaitForMoreTelemetry.Should().BeTrue();
        decision.UpdatedDeployParameters!["telemetry.rollback.breach_streak"].Should().Be("1");
    }

    [Fact]
    public async Task EvaluateAsync_GpBatchPolicy_ExplicitThreshold_OverridesGpDefault()
    {
        // An operator can still pin the threshold: an explicit consecutive_breaches=1 on a GP-batch
        // deploy rolls back on the first breach, overriding the GP burst-tolerant default.
        var fakeProvider = new FakeProviderEvaluator("fake-metrics", new DeployTelemetryReadings
        {
            SampleCount = 50,
            ErrorRate = 0.5,
            LatencyP95 = 120
        });

        var evaluator = CreateProviderEvaluator(fakeProvider);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.AwsEcs,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-metrics",
                ["telemetry.policy"] = "gp-batch",
                ["telemetry.prometheus.job"] = "honua-gp",
                ["telemetry.error_rate.query"] = "errors / requests",
                ["telemetry.error_rate.threshold"] = "0.05",
                ["telemetry.sample_count.query"] = "requests",
                ["telemetry.sample_count.minimum"] = "10",
                ["telemetry.rollback.consecutive_breaches"] = "1"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-6)));

        decision.Should().NotBeNull();
        decision!.RollbackRecommended.Should().BeTrue("an explicit threshold of 1 overrides the GP burst-tolerant default");
    }

    // ---- synthetic /healthz/ready gate (#1849) ---------------------------

    [Fact]
    public async Task EvaluateAsync_HealthProbeUnhealthy_RecommendsRollback_WithoutQueryingMetrics()
    {
        // A synthetic probe that fails the configured threshold must roll back exactly like an
        // error-rate/latency breach — short-circuiting before (and so without needing) a metrics
        // provider to read.
        var probe = new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 3 });
        var evaluator = CreateHealthProbeEvaluator(probe);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.healthz.url"] = "https://example.com/healthz/ready"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.RollbackRecommended.Should().BeTrue();
        decision.WaitForMoreTelemetry.Should().BeFalse();
        decision.Message.Should().Contain("health probe is unhealthy");
        probe.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task EvaluateAsync_HealthProbeHealthy_FallsThroughToMetricGate_AndPasses()
    {
        // A healthy synthetic probe does not promote on its own: it falls through to the metric gate,
        // which is also queried and must pass before the deploy settles.
        var capturedQueries = new ConcurrentQueue<string>();
        var probe = new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 0 });
        var evaluator = CreateEvaluator(
            capturedQueries,
            healthProbe: probe,
            responses: CreateSuccessfulResponses("25", "0.01", "150"));

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod",
                ["telemetry.healthz.url"] = "https://example.com/healthz/ready"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.RollbackRecommended.Should().BeFalse();
        decision.WaitForMoreTelemetry.Should().BeFalse();
        probe.Invocations.Should().Be(1);
        capturedQueries.Should().HaveCount(3, "a healthy probe still defers to the metric gate");
    }

    [Fact]
    public async Task EvaluateAsync_HealthProbeUnhealthy_ShortCircuitsBeforeMetrics()
    {
        // When both a probe and metric queries are configured, an unhealthy probe is a first-class
        // trigger that short-circuits before any metrics provider read.
        var capturedQueries = new ConcurrentQueue<string>();
        var probe = new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 2 });
        var evaluator = CreateEvaluator(
            capturedQueries,
            healthProbe: probe,
            responses: CreateSuccessfulResponses("50", "0.01", "100"));

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod",
                ["telemetry.healthz.url"] = "https://example.com/healthz/ready",
                ["telemetry.healthz.failure_threshold"] = "2"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.RollbackRecommended.Should().BeTrue();
        capturedQueries.Should().BeEmpty("an unhealthy probe rolls back before the metrics gate is read");
    }

    [Fact]
    public async Task EvaluateAsync_HealthProbeConfigured_ButNoProbeService_Waits()
    {
        // A configured health gate with no probe service available must not silently promote past an
        // unverified health check — it holds (waits) instead.
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(capturedQueries, healthProbe: null);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod",
                ["telemetry.healthz.url"] = "https://example.com/healthz/ready"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeTrue();
        decision.RollbackRecommended.Should().BeFalse();
        capturedQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_HealthProbeUnhealthy_WithDebounce_SuppressesFirstBreach()
    {
        // The anti-flap debounce applies to the synthetic health gate too: a single failing scrape with
        // a debounce of 2 holds rather than rolling back, and records a breach streak of 1.
        var probe = new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 3 });
        var evaluator = CreateHealthProbeEvaluator(probe);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.healthz.url"] = "https://example.com/healthz/ready",
                ["telemetry.rollback.consecutive_breaches"] = "2"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.RollbackRecommended.Should().BeFalse();
        decision.WaitForMoreTelemetry.Should().BeTrue();
        decision.UpdatedDeployParameters!["telemetry.rollback.breach_streak"].Should().Be("1");
    }

    private static DeployTelemetrySignalEvaluator CreateHealthProbeEvaluator(FakeHealthProbe probe)
        => new(
            new TestControlPlaneOptionsMonitor(new ControlPlaneOptions
            {
                TelemetryConnections =
                [
                    new DeployTelemetryConnectionOptions
                    {
                        ConnectionId = "prod-prom",
                        Provider = "prometheus",
                        BaseUrl = "https://example.com",
                        TimeoutSeconds = 2
                    }
                ]
            }),
            [],
            NullLogger<DeployTelemetrySignalEvaluator>.Instance,
            probe);

    // ---- invalid-policy bounded wait (#2161) -----------------------------

    [Fact]
    public async Task EvaluateAsync_InvalidPolicy_WithinGraceWindow_Waits()
    {
        // A freshly-created operation with an invalid policy holds (does not promote, does not roll
        // back) inside the bounded grace window.
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(capturedQueries);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                // Unsupported preset so the built-in Kubernetes preset does not synthesize a fallback
                // threshold; an explicit error-rate query without a threshold => invalid policy.
                ["telemetry.policy"] = "no-such-preset",
                ["telemetry.error_rate.query"] = "errors / requests"
            },
            createdAt: DateTimeOffset.UtcNow));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeTrue();
        decision.RollbackRecommended.Should().BeFalse();
        capturedQueries.Should().BeEmpty("an invalid policy must not query the backend");
    }

    [Fact]
    public async Task EvaluateAsync_InvalidPolicy_PastGraceWindow_EscalatesToRollback()
    {
        // Past the (configurable) grace window an invalid policy escalates to a rollback recommendation
        // rather than parking the deploy in Reconciling forever.
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(capturedQueries);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                // Unsupported preset (no fallback threshold) + explicit query with no threshold => invalid.
                ["telemetry.policy"] = "no-such-preset",
                ["telemetry.error_rate.query"] = "errors / requests",
                ["telemetry.invalid_policy_grace_seconds"] = "60"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10)));

        decision.Should().NotBeNull();
        decision!.RollbackRecommended.Should().BeTrue("an invalid policy must not silently park a deploy forever");
        decision.WaitForMoreTelemetry.Should().BeFalse();
        decision.Message.Should().Contain("invalid");
        capturedQueries.Should().BeEmpty();
    }

    private static DeployTelemetryPolicy CreateThresholdPolicy(double errorThreshold, double latencyThreshold)
        => new()
        {
            ConnectionId = "prod-prom",
            ErrorRateQuery = "errors / requests",
            ErrorRateThreshold = errorThreshold,
            LatencyP95Query = "p95",
            LatencyP95ThresholdMs = latencyThreshold,
            MinimumSampleQuery = "requests",
            MinimumSampleCount = 10
        };

    private static DeployTelemetrySignalEvaluator CreateProviderEvaluator(FakeProviderEvaluator fakeProvider)
        => new(
            new TestControlPlaneOptionsMonitor(new ControlPlaneOptions
            {
                TelemetryConnections =
                [
                    new DeployTelemetryConnectionOptions
                    {
                        ConnectionId = "prod-metrics",
                        Provider = "fake-metrics",
                        BaseUrl = "https://example.com",
                        TimeoutSeconds = 2
                    }
                ]
            }),
            [fakeProvider],
            NullLogger<DeployTelemetrySignalEvaluator>.Instance);

    private static DeployTelemetrySignalEvaluator CreateEvaluator(
        ConcurrentQueue<string> capturedQueries,
        DeployTelemetryConnectionOptions? connection = null,
        IDeployHealthProbe? healthProbe = null,
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

        var prometheusProvider = new PrometheusDeployTelemetryProviderEvaluator(
            new StubHttpClientFactory(new HttpClient(handler)));

        return new DeployTelemetrySignalEvaluator(
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
            [prometheusProvider],
            NullLogger<DeployTelemetrySignalEvaluator>.Instance,
            healthProbe);
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

    private sealed class FakeProviderEvaluator(string provider, DeployTelemetryReadings readings) : IDeployTelemetryProviderEvaluator
    {
        public string Provider => provider;

        public bool WasInvoked { get; private set; }

        public Task<DeployTelemetryReadings> ReadAsync(
            DeployTelemetryPolicyDescriptor policy,
            DeployTelemetryConnectionDescriptor connection,
            CancellationToken cancellationToken)
        {
            WasInvoked = true;
            return Task.FromResult(readings);
        }
    }

    private sealed class FakeHealthProbe(
        DeployHealthProbeResult result,
        DeployGoldenQueryResult? goldenResult = null) : IDeployHealthProbe
    {
        public int Invocations { get; private set; }

        public int GoldenInvocations { get; private set; }

        public Task<DeployHealthProbeResult> ProbeAsync(DeployHealthProbeRequest request, CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult(result);
        }

        public Task<DeployGoldenQueryResult> ProbeGoldenQueryAsync(DeployGoldenQueryRequest request, CancellationToken cancellationToken)
        {
            GoldenInvocations++;
            return Task.FromResult(goldenResult ?? new DeployGoldenQueryResult { Matched = true });
        }
    }
}
