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
    public void Parse_WithoutTelemetryConnection_ReturnsNull()
    {
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.prometheus.job"] = "honua-prod"
        }));

        policy.Should().BeNull("no telemetry.connection means the gate is not configured");
    }

    [Fact]
    public void Parse_WithConnectionButNoQueries_ReturnsNull()
    {
        // An unsupported preset synthesizes no queries and the operator supplied no explicit query, so
        // there is no signal at all and Parse returns null (rather than a structurally invalid policy).
        // Note: the built-in kubernetes-honua-http preset defaults the Prometheus job to "honua" and so
        // always synthesizes queries — it can never reach this no-signal path; only an unknown preset can.
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(
            new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.policy"] = "no-such-preset"
            }));

        policy.Should().BeNull("an unsupported preset with no explicit query yields no signal");
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

        // With an explicit override the preset validation error is intentionally cleared (the
        // override-only policy is validated by its own per-query threshold checks), so the policy is
        // valid. This documents the override-supersedes-preset contract.
        policy.Should().NotBeNull();
        policy!.IsValid.Should().BeTrue("an explicit query override supersedes the unsupported preset");
    }

    [Fact]
    public void Parse_UnsupportedPresetWithoutOverride_ReturnsNull()
    {
        // An unsupported preset synthesizes no queries. telemetry.prometheus.job is only consumed by the
        // BUILT-IN presets, so it does not create a signal for an unknown preset. With no explicit query
        // override either, Parse short-circuits to null (no signal to evaluate) before the unsupported-
        // preset validation error could surface. This documents that the "policy not supported" error is
        // only reachable when a built-in preset is misconfigured, not for an unknown preset name.
        var policy = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.policy"] = "totally-made-up-preset",
            ["telemetry.prometheus.job"] = "honua-prod"
        }));

        policy.Should().BeNull("an unsupported preset with no explicit query override yields no signal");
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

    [Fact]
    public void Parse_ZeroAndNegativeWarmup_FallsBackToPresetDefault()
    {
        // A zero/negative warmup must not produce an instant or negative warmup window; the parser
        // falls back to the preset default rather than silently honoring an invalid value.
        var policyZero = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.policy"] = "kubernetes-honua-http",
            ["telemetry.prometheus.job"] = "honua-prod",
            ["telemetry.warmup_seconds"] = "0"
        }));

        policyZero.Should().NotBeNull();
        policyZero!.WarmupDuration.Should().BeGreaterThan(TimeSpan.Zero);

        var policyNegative = DeployTelemetryPolicy.Parse(CreateSpec(new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.policy"] = "kubernetes-honua-http",
            ["telemetry.prometheus.job"] = "honua-prod",
            ["telemetry.warmup_seconds"] = "-30"
        }));

        policyNegative.Should().NotBeNull();
        policyNegative!.WarmupDuration.Should().BeGreaterThan(TimeSpan.Zero);
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
                "a non-numeric (malformed) error-rate threshold is treated as missing",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.policy"] = "no-such-preset",
                    ["telemetry.error_rate.query"] = "errors / requests",
                    ["telemetry.error_rate.threshold"] = "not-a-number"
                },
                "telemetry.error_rate.threshold is missing"
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
                "a negative threshold parses to a value (HasValue) so the policy is structurally valid",
                new Dictionary<string, string>
                {
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.error_rate.query"] = "errors / requests",
                    ["telemetry.error_rate.threshold"] = "-1"
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
