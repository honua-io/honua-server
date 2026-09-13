// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Dedicated coverage for <see cref="DeployTelemetryPolicy.Parse"/> (#2161). The parser turns deploy
/// spec parameters into a provider-neutral telemetry gate policy; before this suite it had no direct
/// tests. Exercises the malformed / missing-threshold / preset-and-override combinations that decide
/// whether the gate is valid (settles the deploy) or invalid (parks/escalates it).
/// </summary>
public sealed class DeployTelemetryPolicyTests
{
    [Fact]
    public void Parse_WithNoTelemetryParameters_ReturnsNull()
    {
        // Only a deploy with no operator-authored telemetry.* parameter has "no gate". The evaluator's
        // own runtime bookkeeping key does not count as configuration.
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["deployment.canary_weight_percentage"] = "10",
            [DeployTelemetrySignalEvaluator.BreachStreakParameterKey] = "2"
        }));

        policy.Should().BeNull("no telemetry parameter means the gate is not configured");
    }

    [Fact]
    public void Parse_WithTelemetryParametersButNoConnection_ProducesInvalidPolicy()
    {
        // #4617: this used to return null, silently dropping the operator's telemetry configuration.
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.prometheus.job"] = "honua-prod"
        }));

        policy.Should().NotBeNull();
        policy!.IsValid.Should().BeFalse("configured telemetry without a connection would otherwise be silently ignored");
        policy.ValidationError.Should().Contain("telemetry.connection is missing").And.Contain("telemetry.prometheus.job");
    }

    [Fact]
    public void Parse_WithConnectionAndUnsupportedPreset_ProducesInvalidPolicy()
    {
        // #4617: an unknown preset with no query used to yield null, so the connection the operator
        // configured was silently ignored and the deploy ran ungated.
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.policy"] = "no-such-preset"
            }));

        policy.Should().NotBeNull();
        policy!.IsValid.Should().BeFalse();
        policy.ValidationError.Should().Contain("'no-such-preset' is not supported");
    }

    [Theory]
    [MemberData(nameof(InvalidPolicyCases))]
    public void Parse_WithMalformedThresholds_ProducesInvalidPolicy(
        string because,
        IReadOnlyDictionary<string, string> parameters,
        string expectedErrorFragment)
    {
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(parameters));

        policy.Should().NotBeNull(because);
        policy!.IsValid.Should().BeFalse(because);
        policy.ValidationError.Should().NotBeNullOrWhiteSpace();
        policy.ValidationError!.Should().Contain(expectedErrorFragment);
    }

    [Theory]
    [MemberData(nameof(ValidPolicyCases))]
    public void Parse_WithWellFormedPolicy_ProducesValidPolicy(
        string because,
        IReadOnlyDictionary<string, string> parameters)
    {
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(parameters));

        policy.Should().NotBeNull(because);
        policy!.IsValid.Should().BeTrue(because);
        policy.ValidationError.Should().BeNullOrWhiteSpace();
    }

    [Fact]
    public void Parse_UnsupportedPresetName_ProducesInvalidPolicy()
    {
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.policy"] = "totally-made-up-preset",
            // Provide an explicit query so Parse does not early-return null on "no signals"; the
            // unsupported-preset validation error must still surface.
            ["telemetry.error_rate.query"] = "errors / requests",
            ["telemetry.error_rate.threshold"] = "0.05"
        }));

        // #4617: an explicit override no longer hides an unsupported preset name — silently ignoring
        // the preset the operator asked for is itself a rejected configuration.
        policy.Should().NotBeNull();
        policy!.IsValid.Should().BeFalse("an unsupported preset is rejected even alongside explicit query overrides");
        policy.ValidationError.Should().Contain("'totally-made-up-preset' is not supported");
    }

    [Fact]
    public void Parse_UnsupportedPresetWithoutOverride_ProducesInvalidPolicy()
    {
        // An unsupported preset synthesizes no queries, so telemetry.prometheus.job has nothing to feed.
        // #4617: this used to short-circuit to null (ungated deploy); it is now a rejected policy.
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.policy"] = "totally-made-up-preset",
            ["telemetry.prometheus.job"] = "honua-prod"
        }));

        policy.Should().NotBeNull();
        policy!.IsValid.Should().BeFalse();
        policy.ValidationError.Should().Contain("'totally-made-up-preset' is not supported");
    }

    [Fact]
    public void Parse_PresetWithOverrideAndThreshold_PrefersExplicitQuery()
    {
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.policy"] = "kubernetes-honua-http",
            ["telemetry.prometheus.job"] = "honua-prod",
            ["telemetry.error_rate.query"] = "custom_error_ratio",
            ["telemetry.error_rate.threshold"] = "0.02"
        }));

        policy.Should().NotBeNull();
        policy!.IsValid.Should().BeTrue();
        policy.ErrorRateQuery.Should().Be("custom_error_ratio", "the explicit override replaces the preset query");
        policy.ErrorRateThreshold.Should().Be(0.02);
        policy.HasExplicitQueryOverride.Should().BeTrue();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-30")]
    [InlineData("soon")]
    [InlineData("NaN")]
    [InlineData("99999999")]
    public void Parse_ZeroNegativeMalformedOrUnboundedWarmup_IsRejected(string rawValue)
    {
        // A zero/negative warmup must not produce an instant or negative warmup window, and an unusable
        // value must not be silently replaced by the preset default either (#4617): it is rejected.
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.policy"] = "kubernetes-honua-http",
            ["telemetry.prometheus.job"] = "honua-prod",
            ["telemetry.warmup_seconds"] = rawValue
        }));

        policy.Should().NotBeNull();
        policy!.IsValid.Should().BeFalse();
        policy.ValidationError.Should().Contain("telemetry.warmup_seconds must be");
    }

    [Fact]
    public void Parse_GpBatchPreset_ProducesValidPolicyWithLongerWarmup()
    {
        // The serverless-GP substrate preset bakes longer (5m) than the standard 2m HTTP preset
        // because GP per-job metrics are sparse/bursty (honua-server#2165).
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.policy"] = "gp-batch",
            ["telemetry.prometheus.job"] = "honua-gp"
        }));

        policy.Should().NotBeNull();
        policy!.IsValid.Should().BeTrue();
        policy.WarmupDuration.Should().Be(TimeSpan.FromMinutes(5));
        policy.HasMetricSignals.Should().BeTrue();
    }

    [Fact]
    public void Parse_HealthProbe_AugmentsMetricGate_WithDefaults()
    {
        // The synthetic /healthz/ready gate (#1849) is provider-independent and layers onto the metric
        // gate. With a Kubernetes target the default honua-http preset still synthesizes the metric
        // queries; the probe URL is parsed alongside them with its documented defaults.
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.prometheus.job"] = "honua-prod",
            ["telemetry.healthz.url"] = "https://example.com/healthz/ready"
        }));

        policy.Should().NotBeNull();
        policy!.IsValid.Should().BeTrue();
        policy.HasHealthProbe.Should().BeTrue();
        policy.HasMetricSignals.Should().BeTrue("the preset synthesizes metric queries alongside the probe");
        policy.HealthProbeUrl.Should().Be("https://example.com/healthz/ready");
        policy.HealthProbeFailureThreshold.Should().Be(1, "defaults to a single failure when unset");
        policy.HealthProbeSamples.Should().Be(3);
        policy.HealthProbeExpectedStatusCode.Should().Be(200);
    }

    [Fact]
    public void Parse_HealthProbeOverrides_AreHonoured()
    {
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.policy"] = "kubernetes-honua-http",
            ["telemetry.prometheus.job"] = "honua-prod",
            ["telemetry.healthz.url"] = "https://example.com/healthz/ready",
            ["telemetry.healthz.failure_threshold"] = "2",
            ["telemetry.healthz.samples"] = "5",
            ["telemetry.healthz.expected_status"] = "204",
            ["telemetry.healthz.timeout_seconds"] = "8"
        }));

        policy.Should().NotBeNull();
        policy!.IsValid.Should().BeTrue();
        policy.HasHealthProbe.Should().BeTrue();
        policy.HasMetricSignals.Should().BeTrue("the preset still synthesizes metric queries alongside the probe");
        policy.HealthProbeFailureThreshold.Should().Be(2);
        policy.HealthProbeSamples.Should().Be(5);
        policy.HealthProbeExpectedStatusCode.Should().Be(204);
        policy.HealthProbeTimeoutSeconds.Should().Be(8);
    }

    [Fact]
    public void Parse_HealthProbeNonPositiveOrMalformedOverrides_AreRejected()
    {
        // #4617: these used to fall back to defaults silently, so the probe ran with settings the
        // operator never chose. Each unusable value is now named in the validation error.
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.healthz.url"] = "https://example.com/healthz/ready",
            ["telemetry.healthz.failure_threshold"] = "0",
            ["telemetry.healthz.samples"] = "-3",
            ["telemetry.healthz.timeout_seconds"] = "not-a-number"
        }));

        policy.Should().NotBeNull();
        policy!.IsValid.Should().BeFalse();
        policy.ValidationError.Should()
            .Contain("telemetry.healthz.failure_threshold must be")
            .And.Contain("telemetry.healthz.samples must be")
            .And.Contain("telemetry.healthz.timeout_seconds must be");
    }

    // Each invalid case pins an EXPLICIT per-signal query override (so HasExplicitQueryOverride is true)
    // with no threshold, alongside an unsupported preset name. Using an unsupported preset is deliberate:
    // the built-in Kubernetes/honua-http presets always synthesize ALL three queries AND thresholds from
    // the default job, which would bleed a valid threshold into the merged policy and mask the missing
    // override threshold. The unknown preset contributes nothing, so the per-query threshold check fires.
    public static TheoryData<string, IReadOnlyDictionary<string, string>, string> InvalidPolicyCases()
        => new()
        {
            {
                "error-rate query without a threshold is invalid",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.policy"] = "no-such-preset",
                    ["telemetry.error_rate.query"] = "errors / requests"
                },
                "telemetry.error_rate.threshold is missing"
            },
            {
                "latency query without a threshold is invalid",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.policy"] = "no-such-preset",
                    ["telemetry.latency_p95.query"] = "p95"
                },
                "telemetry.latency_p95.threshold_ms is missing"
            },
            {
                "sample-count query without a minimum is invalid",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.policy"] = "no-such-preset",
                    ["telemetry.sample_count.query"] = "requests"
                },
                "telemetry.sample_count.minimum is missing"
            },
            {
                "a non-numeric (malformed) error-rate threshold is rejected, not replaced by a default",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.error_rate.query"] = "errors / requests",
                    ["telemetry.error_rate.threshold"] = "not-a-number"
                },
                "telemetry.error_rate.threshold must be"
            },
            {
                "a negative error-rate threshold can never be satisfied honestly and is rejected",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.error_rate.query"] = "errors / requests",
                    ["telemetry.error_rate.threshold"] = "-1"
                },
                "telemetry.error_rate.threshold must be a finite number >= 0"
            },
            {
                "a non-finite latency threshold is rejected",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.latency_p95.threshold_ms"] = "Infinity"
                },
                "telemetry.latency_p95.threshold_ms must be"
            },
            {
                "a zero sample floor admits zero traffic and is rejected",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.sample_count.minimum"] = "0"
                },
                "telemetry.sample_count.minimum must be"
            },
            {
                "error-rate/latency signals without any sample floor are rejected",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.policy"] = "no-such-preset",
                    ["telemetry.error_rate.query"] = "errors / requests",
                    ["telemetry.error_rate.threshold"] = "0.05"
                },
                "require a sample floor"
            },
            {
                "an unrecognized telemetry key is rejected rather than silently ignored",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.error_rate.treshold"] = "0.05"
                },
                "'telemetry.error_rate.treshold' is not a recognized deploy telemetry parameter"
            },
            {
                "a staleness bound of zero would reject every sample and is rejected",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.max_staleness_seconds"] = "0"
                },
                "telemetry.max_staleness_seconds must be"
            },
            {
                "a staleness bound above one hour is rejected rather than clamped",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.max_staleness_seconds"] = "86400"
                },
                "telemetry.max_staleness_seconds must be"
            },
            {
                "an exposure deadline above two hours is rejected",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.exposure_deadline_seconds"] = "90000"
                },
                "telemetry.exposure_deadline_seconds must be"
            },
            {
                "a malformed anti-flap threshold is rejected",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.rollback.consecutive_breaches"] = "0"
                },
                "telemetry.rollback.consecutive_breaches must be"
            },
            {
                "a failure threshold above the sample count means the probe can never fail",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.healthz.url"] = "https://example.com/healthz/ready",
                    ["telemetry.healthz.samples"] = "2",
                    ["telemetry.healthz.failure_threshold"] = "3"
                },
                "could never fail"
            },
            {
                "an out-of-range expected status is rejected",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.healthz.url"] = "https://example.com/healthz/ready",
                    ["telemetry.healthz.expected_status"] = "700"
                },
                "telemetry.healthz.expected_status must be"
            },
            {
                "health-probe settings without a probe URL would never apply",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.healthz.samples"] = "5"
                },
                "without telemetry.healthz.url"
            },
            {
                "golden-query expectations without a URL would never be checked",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.golden_query.forbidden_contains"] = "FALLBACK"
                },
                "without telemetry.golden_query.url"
            },
            {
                "the health-only profile rejects a metrics connection it would ignore",
                new Dictionary<string, string>
                {
                    ["telemetry.policy"] = "health-only",
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.healthz.url"] = "https://example.com/healthz/ready"
                },
                "does not use a metrics connection"
            },
            {
                "the health-only profile rejects metric parameters instead of dropping them",
                new Dictionary<string, string>
                {
                    ["telemetry.policy"] = "health-only",
                    ["telemetry.healthz.url"] = "https://example.com/healthz/ready",
                    ["telemetry.error_rate.threshold"] = "0.05"
                },
                "cannot carry metric parameters (telemetry.error_rate.threshold)"
            },
            {
                "the health-only profile needs at least one probe",
                new Dictionary<string, string>
                {
                    ["telemetry.policy"] = "health-only"
                },
                "requires telemetry.healthz.url or telemetry.golden_query.url"
            }
        };

    public static TheoryData<string, IReadOnlyDictionary<string, string>> ValidPolicyCases()
        => new()
        {
            {
                "error-rate query with a threshold is valid (including a zero threshold)",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.error_rate.query"] = "errors / requests",
                    ["telemetry.error_rate.threshold"] = "0"
                }
            },
            {
                "the explicit health-only profile is valid with a readiness probe and no metrics connection",
                new Dictionary<string, string>
                {
                    ["telemetry.policy"] = "health-only",
                    ["telemetry.healthz.url"] = "https://example.com/healthz/ready"
                }
            },
            {
                "the explicit health-only profile is valid with only a golden-query correctness probe",
                new Dictionary<string, string>
                {
                    ["telemetry.policy"] = "health-only",
                    ["telemetry.golden_query.url"] = "https://example.com/rest/services/probe",
                    ["telemetry.golden_query.expected_contains"] = "GOLDEN-OK",
                    ["telemetry.golden_query.forbidden_contains"] = "FALLBACK"
                }
            },
            {
                "a fully specified override-only policy is valid",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.error_rate.query"] = "errors / requests",
                    ["telemetry.error_rate.threshold"] = "0.05",
                    ["telemetry.latency_p95.query"] = "p95",
                    ["telemetry.latency_p95.threshold_ms"] = "2000",
                    ["telemetry.sample_count.query"] = "requests",
                    ["telemetry.sample_count.minimum"] = "10"
                }
            }
        };

    [Fact]
    public void Parse_WithoutEvidenceGraceParameter_DefaultsToFifteenMinutes()
    {
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.prometheus.job"] = "honua-prod"
        }));

        policy.Should().NotBeNull();
        policy!.EvidenceGraceDuration.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Parse_WithEvidenceGraceSeconds_UsesConfiguredValue()
    {
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.prometheus.job"] = "honua-prod",
            ["telemetry.evidence_grace_seconds"] = "600"
        }));

        policy.Should().NotBeNull();
        policy!.EvidenceGraceDuration.Should().Be(TimeSpan.FromSeconds(600));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-30")]
    [InlineData("not-a-number")]
    [InlineData("999999")]
    public void Parse_WithInvalidOrUnboundedEvidenceGraceSeconds_IsRejected(string rawValue)
    {
        // A misconfigured grace must never disable or unbound the evidence deadline (#4617), and it is
        // not silently replaced by the default either: the deploy is rejected at plan time.
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.prometheus.job"] = "honua-prod",
            ["telemetry.evidence_grace_seconds"] = rawValue
        }));

        policy.Should().NotBeNull();
        policy!.IsValid.Should().BeFalse();
        policy.ValidationError.Should().Contain("telemetry.evidence_grace_seconds must be a finite number in (0, 3600]");
    }

    [Fact]
    public void Parse_WithoutMaxStalenessParameter_AppliesFiveMinuteFreshnessBound()
    {
        // #4617: freshness is always enforced; an unconfigured policy gets the default bound rather
        // than accepting samples of any age.
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.prometheus.job"] = "honua-prod"
        }));

        policy.Should().NotBeNull();
        policy!.IsValid.Should().BeTrue();
        policy.MaximumEvidenceStaleness.Should().Be(TimeSpan.FromMinutes(5));
        policy.ToDescriptor().MaximumEvidenceStaleness.Should().Be(TimeSpan.FromMinutes(5), "providers receive the bound");
    }

    [Fact]
    public void Parse_ExposureDeadline_DefaultsToThirtyMinutes_AndHonoursConfiguredValue()
    {
        var defaulted = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.prometheus.job"] = "honua-prod"
        }));
        var configured = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.prometheus.job"] = "honua-prod",
            ["telemetry.exposure_deadline_seconds"] = "900"
        }));

        defaulted!.ExposureDeadline.Should().Be(TimeSpan.FromMinutes(30));
        configured!.IsValid.Should().BeTrue();
        configured.ExposureDeadline.Should().Be(TimeSpan.FromSeconds(900));
    }

    [Fact]
    public void Parse_HealthOnlyProfile_HasNoMetricSignalsAndNoConnection()
    {
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.policy"] = "health-only",
            ["telemetry.healthz.url"] = "https://example.com/healthz/ready",
            ["telemetry.golden_query.url"] = "https://example.com/rest/services/probe",
            ["telemetry.golden_query.expected_contains"] = "GOLDEN-OK",
            ["telemetry.golden_query.forbidden_contains"] = "FALLBACK"
        }));

        policy.Should().NotBeNull();
        policy!.IsValid.Should().BeTrue();
        policy.IsHealthOnly.Should().BeTrue();
        policy.HasMetricSignals.Should().BeFalse("the Kubernetes preset must not synthesize metric queries for a health-only profile");
        policy.ConnectionId.Should().BeEmpty();
        policy.HasHealthProbe.Should().BeTrue();
        policy.HasGoldenQuery.Should().BeTrue();
        policy.GoldenQueryForbiddenContains.Should().Be("FALLBACK");
    }

    [Fact]
    public void Parse_WithMaxStalenessSeconds_UsesConfiguredValue()
    {
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.prometheus.job"] = "honua-prod",
            ["telemetry.max_staleness_seconds"] = "120"
        }));

        policy.Should().NotBeNull();
        policy!.MaximumEvidenceStaleness.Should().Be(TimeSpan.FromSeconds(120));
    }

    // ---- target/revision identity while traffic is split (#4617) ----------

    [Theory]
    [InlineData("deployment.canary_weight_percentage", "10")]
    [InlineData("deployment.canary_ramp.step_weights", "5,25,100")]
    public void Parse_SplitRolloutWithAggregatePresetMetrics_IsRejected(string splitKey, string splitValue)
    {
        // The Kubernetes default preset reads all traffic for the job, so during a 10% canary the stable
        // revision's healthy 90% would mask a failing candidate.
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.prometheus.job"] = "honua-prod",
            [splitKey] = splitValue
        }));

        policy.Should().NotBeNull();
        policy!.IsValid.Should().BeFalse();
        policy.ValidationError.Should().Contain("cannot identify the candidate revision");
    }

    [Fact]
    public void Parse_SplitRolloutWithCanaryJob_UsesCandidateScopedKubernetesPreset()
    {
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.prometheus.canary_job"] = "honua-canary-prod",
            ["deployment.canary_weight_percentage"] = "10"
        }));

        policy.Should().NotBeNull();
        policy!.IsValid.Should().BeTrue(policy.ValidationError);
        policy.IsCandidateScoped.Should().BeTrue();
        policy.ErrorRateQuery.Should().Contain("job=\"honua-canary-prod\"");
        policy.MinimumSampleQuery.Should().Contain("job=\"honua-canary-prod\"");
        policy.LatencyP95Query.Should().Contain("job=\"honua-canary-prod\"");
    }

    [Fact]
    public void Parse_SplitRolloutWithExplicitCandidateQueries_IsValid()
    {
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["deployment.canary_weight_percentage"] = "10",
            ["telemetry.error_rate.query"] = "honua_canary_error_rate",
            ["telemetry.error_rate.threshold"] = "0.05",
            ["telemetry.sample_count.query"] = "honua_canary_sample_count",
            ["telemetry.sample_count.minimum"] = "20"
        }));

        policy!.IsValid.Should().BeTrue(policy.ValidationError);
    }

    [Fact]
    public void Parse_FullReplacementRolloutWithAggregatePreset_IsValid()
    {
        // Without a traffic split every post-exposure request is served by the candidate.
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.prometheus.job"] = "honua-prod"
        }));

        policy!.IsValid.Should().BeTrue(policy.ValidationError);
        policy.IsCandidateScoped.Should().BeFalse();
    }

    [Fact]
    public void Parse_HonuaHttpPreset_ErrorRateReadsZeroWhenThereAreNoServerErrors()
    {
        // A healthy Honua server exports no 5xx series, and sum() over no series is an empty vector. Checked
        // against a real Prometheus scraping the 9f2f16a server image: with 108 requests, all HTTP 200, the
        // plain ratio came back empty (absent evidence, which never passes the gate) while the "or vector(0)"
        // numerator read 0. With no traffic at all the denominator stays empty, so the ratio stays absent.
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.prometheus.job"] = "honua-prod"
        }));

        policy!.IsValid.Should().BeTrue(policy.ValidationError);
        policy.ErrorRateQuery.Should().Be(
            "(sum(rate(honua_http_request_total{job=\"honua-prod\",status_code=~\"5..\"}[5m])) or vector(0)) / " +
            "clamp_min(sum(rate(honua_http_request_total{job=\"honua-prod\"}[5m])), 0.001)");
    }

    private static DeployOperationSpec CreateSpec(IReadOnlyDictionary<string, string> parameters)
        => new()
        {
            TargetId = "prod-api",
            TargetKind = DeployTargetKind.Kubernetes,
            Backend = "honua-gitops-kubernetes",
            Environment = "production",
            TargetName = "honua-server",
            ArtifactReference = "ghcr.io/honua/server",
            DesiredRevision = "sha256:new",
            Parameters = new Dictionary<string, string>(parameters, StringComparer.Ordinal)
        };
}
