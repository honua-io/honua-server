// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Verifies the <see cref="DeployTargetKind.SelfHostedRolling"/> arm of the deploy telemetry policy
/// preset resolver (#2460). The self-hosted rolling target maps to the health-probe-capable
/// <c>honua-http</c> preset; without the arm the resolver would return no preset and the gate would
/// never be evaluated for a self-hosted deploy.
/// </summary>
public sealed class SelfHostedRollingTelemetryPolicyTests
{
    [Fact]
    public void Parse_SelfHostedRolling_MapsToHonuaHttpPreset()
    {
        var spec = new DeployOperationSpec
        {
            TargetId = "self-host-prod",
            TargetKind = DeployTargetKind.SelfHostedRolling,
            Backend = "honua-yarp-rolling",
            Environment = "prod",
            TargetName = "honua",
            DesiredRevision = "honua/app:2.0",
            Parameters = new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua"
            }
        };

        var policy = DeployTelemetryPolicy.Parse(spec);

        policy.Should().NotBeNull("the SelfHostedRolling arm resolves to the honua-http preset");
        policy!.IsValid.Should().BeTrue();
        policy.ErrorRateQuery.Should().Contain("honua_http_request_total");
    }

    [Fact]
    public void Parse_SelfHostedRolling_WithHealthProbeOnly_IsValid()
    {
        var spec = new DeployOperationSpec
        {
            TargetId = "self-host-prod",
            TargetKind = DeployTargetKind.SelfHostedRolling,
            Backend = "honua-yarp-rolling",
            Environment = "prod",
            TargetName = "honua",
            DesiredRevision = "honua/app:2.0",
            Parameters = new Dictionary<string, string>
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.healthz.url"] = "https://honua.example/healthz/ready"
            }
        };

        var policy = DeployTelemetryPolicy.Parse(spec);

        policy.Should().NotBeNull();
        policy!.HasHealthProbe.Should().BeTrue();
    }
}
