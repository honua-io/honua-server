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
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler(_ =>
            throw new InvalidOperationException("Prometheus provider must not be invoked.")));
        var prometheusProvider = new PrometheusDeployTelemetryProviderEvaluator(
            new StubHttpClientFactory(httpClient));

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

    // ---- missing/invalid evidence never satisfies a health requirement (#4617) --------------------

    [Fact]
    public void Evaluate_AbsentErrorRate_WithNoSampleFloorConfigured_WaitsRatherThanPassing()
    {
        // The named defect: with no sample-count floor configured, an absent error-rate reading must
        // not fall through to "healthy". Only latency is populated; error-rate is unavailable.
        var policy = new DeployTelemetryPolicy
        {
            ConnectionId = "prod-prom",
            ErrorRateQuery = "errors / requests",
            ErrorRateThreshold = 0.05,
            LatencyP95Query = "p95",
            LatencyP95ThresholdMs = 2000
        };
        var readings = new DeployTelemetryReadings { LatencyP95 = 120 };

        var decision = DeployTelemetrySignalEvaluator.Evaluate(policy, readings);

        decision.WaitForMoreTelemetry.Should().BeTrue("absent evidence must never silently satisfy a configured health requirement");
        decision.RollbackRecommended.Should().BeFalse();
        decision.Message.Should().Contain("error-rate");
    }

    [Fact]
    public void Evaluate_AbsentLatency_WithNoSampleFloorConfigured_WaitsRatherThanPassing()
    {
        var policy = new DeployTelemetryPolicy
        {
            ConnectionId = "prod-prom",
            ErrorRateQuery = "errors / requests",
            ErrorRateThreshold = 0.05,
            LatencyP95Query = "p95",
            LatencyP95ThresholdMs = 2000
        };
        var readings = new DeployTelemetryReadings { ErrorRate = 0.01 };

        var decision = DeployTelemetrySignalEvaluator.Evaluate(policy, readings);

        decision.WaitForMoreTelemetry.Should().BeTrue("absent evidence must never silently satisfy a configured health requirement");
        decision.RollbackRecommended.Should().BeFalse();
        decision.Message.Should().Contain("latency");
    }

    [Fact]
    public void Evaluate_ErrorRateBreach_WinsOverAMissingLatencySignal()
    {
        // A confirmed breach must not be masked by a different signal simply being unavailable.
        var policy = CreateThresholdPolicy(errorThreshold: 0.05, latencyThreshold: 2000);
        var readings = new DeployTelemetryReadings { SampleCount = 100, ErrorRate = 0.5 };

        var decision = DeployTelemetrySignalEvaluator.Evaluate(policy, readings);

        decision.RollbackRecommended.Should().BeTrue();
        decision.Message.Should().Contain("error rate");
    }

    // ---- bounded evidence-missing grace (#4617) ---------------------------

    [Fact]
    public async Task EvaluateAsync_MissingConnection_WithinEvidenceGrace_Waits()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(capturedQueries);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "no-such-connection",
                ["telemetry.error_rate.query"] = "errors / requests",
                ["telemetry.error_rate.threshold"] = "0.05"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeTrue();
        decision.RollbackRecommended.Should().BeFalse();
        decision.Message.Should().Contain("not configured");
    }

    [Fact]
    public async Task EvaluateAsync_MissingConnection_PastEvidenceGrace_EscalatesToRollback()
    {
        // Provider outages/misconfiguration must not park a deploy in Reconciling forever (#4617):
        // past warmup + the configured evidence grace window this escalates to rollback.
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(capturedQueries);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "no-such-connection",
                ["telemetry.error_rate.query"] = "errors / requests",
                ["telemetry.error_rate.threshold"] = "0.05",
                ["telemetry.evidence_grace_seconds"] = "60"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10)));

        decision.Should().NotBeNull();
        decision!.RollbackRecommended.Should().BeTrue("missing telemetry evidence must not park a deploy forever");
        decision.WaitForMoreTelemetry.Should().BeFalse();
        decision.Message.Should().Contain("not configured");
    }

    [Fact]
    public async Task EvaluateAsync_AbsentMetricEvidence_PastEvidenceGrace_EscalatesToRollback()
    {
        // Sustained missing metric evidence (not a config error, an empty provider response) also
        // escalates rather than parking forever once warmup + grace elapse.
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(
            capturedQueries,
            responses: ["""{"status":"success","data":{"resultType":"vector","result":[]}}"""]);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.error_rate.query"] = "errors / requests",
                ["telemetry.error_rate.threshold"] = "0.05",
                ["telemetry.evidence_grace_seconds"] = "60"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10)));

        decision.Should().NotBeNull();
        decision!.RollbackRecommended.Should().BeTrue();
        decision.WaitForMoreTelemetry.Should().BeFalse();
    }

    // ---- traffic-exposure anchored warmup (#4617) -------------------------

    [Fact]
    public async Task EvaluateAsync_WarmupAnchorsOnTrafficExposedAt_NotOperationCreation()
    {
        // The operation record is old (provisioning took a while), but the candidate only just
        // started receiving traffic: warmup must still be held, proving the anchor is exposure, not
        // creation.
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(
            capturedQueries,
            responses: CreateSuccessfulResponses("25", "0.01", "150"));

        var operation = CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod"
            },
            createdAt: DateTimeOffset.UtcNow.AddHours(-1));
        operation = operation with
        {
            Deploy = operation.Deploy! with { TrafficExposedAt = DateTimeOffset.UtcNow }
        };

        var decision = await evaluator.EvaluateAsync(operation);

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeTrue("warmup must anchor on traffic exposure, not the older operation creation time");
        decision.RollbackRecommended.Should().BeFalse();
        capturedQueries.Should().BeEmpty("the metrics gate must not be queried before warmup (anchored on exposure) elapses");
    }

    [Fact]
    public async Task EvaluateAsync_WithoutTrafficExposedAt_FallsBackToCreatedAt()
    {
        // Operations persisted before TrafficExposedAt was tracked (or backends that never report
        // Reconciling from ObserveAsync) must keep the prior CreatedAt-anchored behavior.
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
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeFalse();
        decision.RollbackRecommended.Should().BeFalse();
        capturedQueries.Should().HaveCount(3);
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
        HttpStatusCode statusCode = HttpStatusCode.OK,
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
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    responseJson ?? """{"status":"success","data":{"resultType":"vector","result":[]}}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        // Intentionally not disposed: this in-memory HttpClient/DelegateHttpMessageHandler
        // never opens a real socket, and the returned evaluator (not IDisposable) is shared
        // by 17+ call sites in this file, so threading a `using` through every caller here
        // would be disproportionate to the (non-existent) unmanaged resource risk.
        var prometheusProvider = new PrometheusDeployTelemetryProviderEvaluator(
            new StubHttpClientFactory(new Honua.TestKit.CallerOwnedHttpClient(handler)));

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

    // ---- Prometheus data-validity negatives (#4617) ------------------------

    [Fact]
    public async Task EvaluateAsync_PrometheusReturnsNaN_TreatsSignalAsAbsent_NotHealthy()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(
            capturedQueries,
            responses: CreateSuccessfulResponses("25", "NaN", "150"));

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod",
                ["telemetry.evidence_grace_seconds"] = "3600"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeTrue("a NaN reading must never silently satisfy the error-rate threshold as healthy");
        decision.RollbackRecommended.Should().BeFalse();
    }

    [Theory]
    [InlineData("+Inf")]
    [InlineData("-Inf")]
    public async Task EvaluateAsync_PrometheusReturnsInfinite_TreatsSignalAsAbsent_NotHealthy(string infiniteValue)
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(
            capturedQueries,
            responses: CreateSuccessfulResponses("25", infiniteValue, "150"));

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod",
                ["telemetry.evidence_grace_seconds"] = "3600"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeTrue("a non-finite reading must never silently satisfy a threshold as healthy");
        decision.RollbackRecommended.Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_PrometheusReturnsAmbiguousMultiSeries_WaitsRatherThanGuessing()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        const string ambiguousResponse =
            """{"status":"success","data":{"resultType":"vector","result":[{"metric":{"pod":"a"},"value":[1710000000,"25"]},{"metric":{"pod":"b"},"value":[1710000000,"30"]}]}}""";
        var evaluator = CreateEvaluator(
            capturedQueries,
            responses: [ambiguousResponse]);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod",
                ["telemetry.evidence_grace_seconds"] = "3600"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeTrue("an ambiguous multi-series result must not be silently resolved by guessing result[0]");
        decision.RollbackRecommended.Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_PrometheusReturnsStaleSample_WhenStalenessBoundConfigured_TreatsAsAbsent()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        // A fixed, ancient observation timestamp — far outside any reasonable staleness bound.
        const string staleResponse =
            """{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1710000000,"0.5"]}]}}""";
        var evaluator = CreateEvaluator(
            capturedQueries,
            responses: [staleResponse]);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod",
                ["telemetry.max_staleness_seconds"] = "300",
                ["telemetry.evidence_grace_seconds"] = "3600"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeTrue("a sample older than the configured staleness bound must never satisfy the threshold as healthy");
        decision.RollbackRecommended.Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_PrometheusReturnsStaleSample_WithoutStalenessParameter_DefaultBoundTreatsAsAbsent()
    {
        // Freshness is enforced by default (#4617): the provider's observation timestamp (2024-03-09)
        // is far outside the 5-minute default bound, so the healthy-looking values are not evidence.
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(
            capturedQueries,
            responses:
            [
                CreateSuccessResponse("25", observedAtUnixSeconds: 1710000000),
                CreateSuccessResponse("0.01", observedAtUnixSeconds: 1710000000),
                CreateSuccessResponse("150", observedAtUnixSeconds: 1710000000)
            ]);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision.Should().NotBeNull();
        decision!.WaitForMoreTelemetry.Should().BeTrue("a stale sample must never satisfy the gate, even without an explicit bound");
        decision.RollbackRecommended.Should().BeFalse();
        decision.Message.Should().Contain("sample-count", "the stale sample floor reads as absent evidence");
        capturedQueries.Should().HaveCount(1, "the gate stops at the absent sample floor");
    }

    [Theory]
    [InlineData("\"not-a-timestamp\"")]
    [InlineData("null")]
    public async Task EvaluateAsync_PrometheusSampleWithoutUsableTimestamp_TreatsAsAbsent(string timestampJson)
    {
        // Missing freshness information is not assumed fresh (#4617).
        var capturedQueries = new ConcurrentQueue<string>();
        var response = $@"{{""status"":""success"",""data"":{{""resultType"":""vector"",""result"":[{{""metric"":{{}},""value"":[{timestampJson},""25""]}}]}}}}";
        var evaluator = CreateEvaluator(capturedQueries, responses: [response]);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision!.WaitForMoreTelemetry.Should().BeTrue();
        decision.RollbackRecommended.Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_PrometheusSampleFromTheFuture_TreatsAsAbsent()
    {
        // Clock skew beyond the bound in the other direction is equally untrustworthy.
        var capturedQueries = new ConcurrentQueue<string>();
        var future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var evaluator = CreateEvaluator(
            capturedQueries,
            responses:
            [
                CreateSuccessResponse("25", observedAtUnixSeconds: future),
                CreateSuccessResponse("0.01", observedAtUnixSeconds: future),
                CreateSuccessResponse("150", observedAtUnixSeconds: future)
            ]);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision!.WaitForMoreTelemetry.Should().BeTrue();
        decision.RollbackRecommended.Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_PrometheusReturnsEmptyResult_PastGrace_RollsBackWithoutEverPassing()
    {
        // Null/empty vectors for every signal: within grace this holds, and once the declared grace
        // after exposure elapses it escalates — there is no missing-data success path.
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(capturedQueries);

        var operation = CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod",
                ["telemetry.evidence_grace_seconds"] = "60"
            },
            createdAt: DateTimeOffset.UtcNow.AddHours(-1));
        var withinGrace = operation with { Deploy = operation.Deploy! with { TrafficExposedAt = DateTimeOffset.UtcNow.AddMinutes(-2.5) } };
        var pastGrace = operation with { Deploy = operation.Deploy! with { TrafficExposedAt = DateTimeOffset.UtcNow.AddMinutes(-10) } };

        var holding = await evaluator.EvaluateAsync(withinGrace);
        var escalated = await evaluator.EvaluateAsync(pastGrace);

        holding!.WaitForMoreTelemetry.Should().BeTrue();
        holding.RollbackRecommended.Should().BeFalse();
        escalated!.RollbackRecommended.Should().BeTrue();
        escalated.WaitForMoreTelemetry.Should().BeFalse();
        escalated.Message.Should().Contain("sample-count");
    }

    [Fact]
    public async Task EvaluateAsync_InsufficientSamples_PastGrace_RollsBack()
    {
        // 3 requests against a floor of 20: the error rate is never read, and sustained insufficient
        // traffic after exposure escalates instead of eventually counting as healthy.
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(capturedQueries, responses: CreateSuccessfulResponses("3", "0", "10"));

        var operation = CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod",
                ["telemetry.evidence_grace_seconds"] = "60"
            },
            createdAt: DateTimeOffset.UtcNow.AddHours(-1));
        operation = operation with { Deploy = operation.Deploy! with { TrafficExposedAt = DateTimeOffset.UtcNow.AddMinutes(-10) } };

        var decision = await evaluator.EvaluateAsync(operation);

        decision!.RollbackRecommended.Should().BeTrue();
        decision.Message.Should().Contain("sample count 3 is below the required minimum 20");
        capturedQueries.Should().HaveCount(1);
    }

    [Fact]
    public async Task EvaluateAsync_NegativeErrorRate_IsNotEvidenceOfHealth()
    {
        // A negative ratio (counter reset / bad metric math) would pass "rate <= threshold"; it is
        // invalid evidence and must hold instead.
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(capturedQueries, responses: CreateSuccessfulResponses("25", "-0.2", "150"));

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision!.WaitForMoreTelemetry.Should().BeTrue();
        decision.RollbackRecommended.Should().BeFalse();
        decision.Message.Should().Contain("error-rate");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task EvaluateAsync_PrometheusOutage_HoldsWithinGrace_ThenRollsBack(HttpStatusCode outageStatus)
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(capturedQueries, statusCode: outageStatus);
        var operation = CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod",
                ["telemetry.evidence_grace_seconds"] = "120"
            },
            createdAt: DateTimeOffset.UtcNow.AddHours(-1));

        var holding = await evaluator.EvaluateAsync(
            operation with { Deploy = operation.Deploy! with { TrafficExposedAt = DateTimeOffset.UtcNow.AddMinutes(-3) } });
        var escalated = await evaluator.EvaluateAsync(
            operation with { Deploy = operation.Deploy! with { TrafficExposedAt = DateTimeOffset.UtcNow.AddMinutes(-5) } });

        holding!.WaitForMoreTelemetry.Should().BeTrue("an outage inside the grace window holds the rollout");
        holding.RollbackRecommended.Should().BeFalse();
        holding.Message.Should().Contain("unavailable");
        escalated!.RollbackRecommended.Should().BeTrue("an outage that outlasts warmup + grace must not park the deploy forever");
        escalated.Message.Should().Contain("unavailable");
    }

    // ---- pre-exposure deadline (#4617) -----------------------------------

    [Fact]
    public async Task EvaluateAsync_SubmittedAndNotYetExposed_HoldsWithoutQuerying()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(capturedQueries, responses: CreateSuccessfulResponses("25", "0.01", "150"));
        var operation = CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        operation = operation with { Status = WorkflowOperationStatus.Submitted };

        var decision = await evaluator.EvaluateAsync(operation);

        decision!.WaitForMoreTelemetry.Should().BeTrue();
        decision.RollbackRecommended.Should().BeFalse();
        decision.Message.Should().Contain("receive traffic");
        capturedQueries.Should().BeEmpty("no telemetry is meaningful before the candidate is exposed");
    }

    [Fact]
    public async Task EvaluateAsync_NeverExposedPastDeadline_FailsWithoutActivation()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(capturedQueries, responses: CreateSuccessfulResponses("25", "0.01", "150"));
        var operation = CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod",
                ["telemetry.exposure_deadline_seconds"] = "600"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-11));
        operation = operation with { Status = WorkflowOperationStatus.Submitted };

        var decision = await evaluator.EvaluateAsync(operation);

        decision!.RollbackRecommended.Should().BeTrue();
        decision.WaitForMoreTelemetry.Should().BeFalse();
        decision.Message.Should().Contain("never observed receiving traffic within the 600-second exposure deadline");
        capturedQueries.Should().BeEmpty();
    }

    // ---- explicit health-only profile (#4617) ------------------------------

    [Fact]
    public async Task EvaluateAsync_HealthOnlyProfile_PassesOnHealthyProbesWithoutAnyMetricsConnection()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var probe = new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 0 });
        var evaluator = CreateEvaluator(capturedQueries, healthProbe: probe);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.policy"] = "health-only",
                ["telemetry.healthz.url"] = "https://example.com/healthz/ready"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision!.WaitForMoreTelemetry.Should().BeFalse();
        decision.RollbackRecommended.Should().BeFalse();
        decision.Message.Should().Contain("health-only");
        probe.Invocations.Should().Be(1);
        capturedQueries.Should().BeEmpty("a health-only profile never consults a metrics provider");
    }

    [Fact]
    public async Task EvaluateAsync_HealthOnlyProfile_UnhealthyProbe_RollsBack()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var probe = new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 2 });
        var evaluator = CreateEvaluator(capturedQueries, healthProbe: probe);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.policy"] = "health-only",
                ["telemetry.healthz.url"] = "https://example.com/healthz/ready"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        decision!.RollbackRecommended.Should().BeTrue();
        capturedQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_HealthOnlyProfile_WithoutProbeService_IsBoundedNotPassed()
    {
        // No probe service means no evidence: hold, then escalate — never pass by default.
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(capturedQueries);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.policy"] = "health-only",
                ["telemetry.healthz.url"] = "https://example.com/healthz/ready",
                ["telemetry.evidence_grace_seconds"] = "60"
            },
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10)));

        decision!.RollbackRecommended.Should().BeTrue();
        decision.Message.Should().Contain("no health-probe service");
    }

    [Fact]
    public async Task EvaluateAsync_ProbeOnlyGateWithoutHealthOnlyProfile_IsRejectedNotDegraded()
    {
        // A metric-required profile whose metrics are unusable must not quietly fall back to the probe:
        // an unknown preset with only a readiness probe is an invalid policy, not a health-only gate.
        var capturedQueries = new ConcurrentQueue<string>();
        var probe = new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 0 });
        var evaluator = CreateEvaluator(capturedQueries, healthProbe: probe);

        var decision = await evaluator.EvaluateAsync(CreateOperation(
            DeployTargetKind.Kubernetes,
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.policy"] = "no-such-preset",
                ["telemetry.healthz.url"] = "https://example.com/healthz/ready"
            },
            createdAt: DateTimeOffset.UtcNow));

        decision!.WaitForMoreTelemetry.Should().BeTrue();
        decision.Message.Should().Contain("not supported");
        probe.Invocations.Should().Be(0, "an invalid policy is never evaluated as if it were health-only");
    }

    // ---- staged candidate before cutover (#4617) ----------------------------

    private static Dictionary<string, string> StagedMetricsParameters(params (string Key, string Value)[] extra)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.prometheus.job"] = "honua",
            ["telemetry.healthz.url"] = "http://127.0.0.1:18081/healthz/ready"
        };
        foreach (var (key, value) in extra)
        {
            parameters[key] = value;
        }

        return parameters;
    }

    [Fact]
    public async Task EvaluateStagedCandidateAsync_BackendHealthGateNotPassed_HoldsWithoutProbingOrQuerying()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var probe = new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 0 });
        var evaluator = CreateEvaluator(capturedQueries, healthProbe: probe, responses: CreateSuccessfulResponses("25", "0.01", "150"));
        var operation = CreateOperation(
            DeployTargetKind.SelfHostedRolling,
            StagedMetricsParameters(),
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));

        var decision = await evaluator.EvaluateStagedCandidateAsync(operation, candidateReady: false);

        decision!.WaitForMoreTelemetry.Should().BeTrue();
        decision.RollbackRecommended.Should().BeFalse();
        decision.Message.Should().Contain("pass the backend health gate before cutover");
        probe.Invocations.Should().Be(0);
        capturedQueries.Should().BeEmpty("a staged candidate serves no traffic for the metrics gate to measure");
    }

    [Fact]
    public async Task EvaluateStagedCandidateAsync_NeverReadyPastExposureDeadline_FailsWithoutActivation()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(capturedQueries, responses: CreateSuccessfulResponses("25", "0.01", "150"));
        var operation = CreateOperation(
            DeployTargetKind.SelfHostedRolling,
            StagedMetricsParameters(("telemetry.exposure_deadline_seconds", "600")),
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-11));

        var decision = await evaluator.EvaluateStagedCandidateAsync(operation, candidateReady: false);

        decision!.RollbackRecommended.Should().BeTrue();
        decision.WaitForMoreTelemetry.Should().BeFalse();
        decision.Message.Should().Contain("never passed the backend health gate within the 600-second exposure deadline");
        capturedQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateStagedCandidateAsync_ReadyOnlyAfterExposureDeadline_FailsWithoutActivation()
    {
        // The backend health gate first passes after the deadline. Healthy probes must not cut the
        // candidate over late.
        var capturedQueries = new ConcurrentQueue<string>();
        var probe = new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 0 });
        var evaluator = CreateEvaluator(capturedQueries, healthProbe: probe);
        var operation = CreateOperation(
            DeployTargetKind.SelfHostedRolling,
            StagedMetricsParameters(("telemetry.exposure_deadline_seconds", "600")),
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-11));

        var decision = await evaluator.EvaluateStagedCandidateAsync(operation, candidateReady: true);

        decision!.RollbackRecommended.Should().BeTrue();
        decision.WaitForMoreTelemetry.Should().BeFalse();
        decision.Message.Should().Contain("was not ready for cutover within the 600-second exposure deadline");
        probe.Invocations.Should().Be(0, "an expired staged phase fails before any probe could clear it");
        capturedQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateStagedCandidateAsync_ReadyWithHealthyProbe_ClearsCutoverWithoutReadingMetrics()
    {
        // Created one minute ago: inside the preset warmup. EvaluateAsync would hold on warmup and then on
        // a sample floor that a candidate with no traffic can never reach; the staged path clears the
        // cutover on the checks that can actually run.
        var capturedQueries = new ConcurrentQueue<string>();
        var probe = new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 0 });
        var evaluator = CreateEvaluator(capturedQueries, healthProbe: probe);
        var operation = CreateOperation(
            DeployTargetKind.SelfHostedRolling,
            StagedMetricsParameters(),
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var decision = await evaluator.EvaluateStagedCandidateAsync(operation, candidateReady: true);

        decision!.WaitForMoreTelemetry.Should().BeFalse();
        decision.RollbackRecommended.Should().BeFalse();
        decision.Message.Should().Contain("telemetry gate is evaluated from cutover");
        probe.Invocations.Should().Be(1);
        capturedQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateStagedCandidateAsync_ReadinessProbeUnhealthy_RecommendsRollback()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var probe = new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 3 });
        var evaluator = CreateEvaluator(capturedQueries, healthProbe: probe);
        var operation = CreateOperation(
            DeployTargetKind.SelfHostedRolling,
            StagedMetricsParameters(),
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var decision = await evaluator.EvaluateStagedCandidateAsync(operation, candidateReady: true);

        decision!.RollbackRecommended.Should().BeTrue();
        decision.Message.Should().Contain("synthetic health probe is unhealthy");
        capturedQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateStagedCandidateAsync_GoldenQueryWrongResult_RecommendsRollback()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var probe = new FakeHealthProbe(
            new DeployHealthProbeResult { Attempts = 3, Failures = 0 },
            new DeployGoldenQueryResult { Matched = false, Detail = "response body contained the forbidden marker" });
        var evaluator = CreateEvaluator(capturedQueries, healthProbe: probe);
        var operation = CreateOperation(
            DeployTargetKind.SelfHostedRolling,
            StagedMetricsParameters(
                ("telemetry.golden_query.url", "http://127.0.0.1:18081/rest/services/parcels/FeatureServer/0/query"),
                ("telemetry.golden_query.expected_contains", "\"features\"")),
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var decision = await evaluator.EvaluateStagedCandidateAsync(operation, candidateReady: true);

        decision!.RollbackRecommended.Should().BeTrue();
        decision.Message.Should().Contain("golden-query correctness gate");
        probe.GoldenInvocations.Should().Be(1);
        capturedQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateStagedCandidateAsync_ProbeServiceMissing_IsBoundedByTheExposureDeadline()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(capturedQueries);
        var parameters = StagedMetricsParameters(("telemetry.exposure_deadline_seconds", "600"));

        var holding = await evaluator.EvaluateStagedCandidateAsync(
            CreateOperation(DeployTargetKind.SelfHostedRolling, parameters, createdAt: DateTimeOffset.UtcNow.AddMinutes(-1)),
            candidateReady: true);
        var escalated = await evaluator.EvaluateStagedCandidateAsync(
            CreateOperation(DeployTargetKind.SelfHostedRolling, parameters, createdAt: DateTimeOffset.UtcNow.AddMinutes(-11)),
            candidateReady: true);

        holding!.WaitForMoreTelemetry.Should().BeTrue("a probe that cannot run is not a passing probe");
        holding.RollbackRecommended.Should().BeFalse();
        escalated!.RollbackRecommended.Should().BeTrue();
        escalated.Message.Should().Contain("beyond the 600-second exposure deadline");
        capturedQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateStagedCandidateAsync_GoldenQueryServiceMissing_PreservesReasonAtExposureDeadline()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var evaluator = CreateEvaluator(capturedQueries);
        var parameters = StagedMetricsParameters(
            ("telemetry.exposure_deadline_seconds", "600"),
            ("telemetry.golden_query.url", "http://127.0.0.1:18081/query"),
            ("telemetry.golden_query.expected_contains", "\"features\""));
        parameters.Remove("telemetry.healthz.url");

        var holding = await evaluator.EvaluateStagedCandidateAsync(
            CreateOperation(DeployTargetKind.SelfHostedRolling, parameters, createdAt: DateTimeOffset.UtcNow.AddMinutes(-1)),
            candidateReady: true);
        var escalated = await evaluator.EvaluateStagedCandidateAsync(
            CreateOperation(DeployTargetKind.SelfHostedRolling, parameters, createdAt: DateTimeOffset.UtcNow.AddMinutes(-11)),
            candidateReady: true);

        holding!.WaitForMoreTelemetry.Should().BeTrue();
        holding.RollbackRecommended.Should().BeFalse();
        holding.Message.Should().Contain("golden-query correctness gate is configured but no probe service is available");
        escalated!.RollbackRecommended.Should().BeTrue();
        escalated.WaitForMoreTelemetry.Should().BeFalse();
        escalated.Message.Should().Contain("beyond the 600-second exposure deadline");
        escalated.Message.Should().Contain("golden-query correctness gate is configured but no probe service is available");
        capturedQueries.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvaluateStagedCandidateAsync_ReadyAfterDeadlineWithoutConfiguredProbes_FailsWithoutActivation(bool probeServiceAvailable)
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var probe = new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 0 });
        var evaluator = CreateEvaluator(capturedQueries, healthProbe: probeServiceAvailable ? probe : null);
        var parameters = StagedMetricsParameters(("telemetry.exposure_deadline_seconds", "600"));
        parameters.Remove("telemetry.healthz.url");

        var decision = await evaluator.EvaluateStagedCandidateAsync(
            CreateOperation(DeployTargetKind.SelfHostedRolling, parameters, createdAt: DateTimeOffset.UtcNow.AddMinutes(-11)),
            candidateReady: true);

        decision!.RollbackRecommended.Should().BeTrue();
        decision.WaitForMoreTelemetry.Should().BeFalse();
        decision.Message.Should().Contain("was not ready for cutover within the 600-second exposure deadline");
        probe.Invocations.Should().Be(0);
        probe.GoldenInvocations.Should().Be(0);
        capturedQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateStagedCandidateAsync_ReadyAfterDeadlineWithGoldenQuery_FailsWithoutProbing()
    {
        var capturedQueries = new ConcurrentQueue<string>();
        var probe = new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 0 });
        var evaluator = CreateEvaluator(capturedQueries, healthProbe: probe);
        var parameters = StagedMetricsParameters(
            ("telemetry.exposure_deadline_seconds", "600"),
            ("telemetry.golden_query.url", "http://127.0.0.1:18081/query"),
            ("telemetry.golden_query.expected_contains", "\"features\""));
        parameters.Remove("telemetry.healthz.url");

        var decision = await evaluator.EvaluateStagedCandidateAsync(
            CreateOperation(DeployTargetKind.SelfHostedRolling, parameters, createdAt: DateTimeOffset.UtcNow.AddMinutes(-11)),
            candidateReady: true);

        decision!.RollbackRecommended.Should().BeTrue();
        decision.WaitForMoreTelemetry.Should().BeFalse();
        decision.Message.Should().Contain("was not ready for cutover within the 600-second exposure deadline");
        probe.Invocations.Should().Be(0);
        probe.GoldenInvocations.Should().Be(0);
        capturedQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateStagedCandidateAsync_NoTelemetryPolicy_ReturnsNull()
    {
        var evaluator = CreateEvaluator(new ConcurrentQueue<string>());
        var operation = CreateOperation(
            DeployTargetKind.SelfHostedRolling,
            new Dictionary<string, string>(StringComparer.Ordinal),
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var decision = await evaluator.EvaluateStagedCandidateAsync(operation, candidateReady: true);

        decision.Should().BeNull("without a telemetry policy the backend's own health gate decides the cutover");
    }

    private static string[] CreateSuccessfulResponses(string sampleCount, string errorRate, string latencyP95)
        =>
        [
            CreateSuccessResponse(sampleCount),
            CreateSuccessResponse(errorRate),
            CreateSuccessResponse(latencyP95)
        ];

    // Samples are stamped "now" by default so the 5-minute freshness bound (#4617) accepts them; pass an
    // explicit observation time to exercise stale/future samples.
    private static string CreateSuccessResponse(string value, long? observedAtUnixSeconds = null)
        => $@"{{""status"":""success"",""data"":{{""resultType"":""vector"",""result"":[{{""metric"":{{}},""value"":[{(observedAtUnixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(System.Globalization.CultureInfo.InvariantCulture)},""{value}""]}}]}}}}";

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
