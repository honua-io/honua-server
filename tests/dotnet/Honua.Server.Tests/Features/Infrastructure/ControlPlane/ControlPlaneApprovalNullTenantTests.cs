// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// An install without tenancy stores approval evidence with no tenant; the control-plane context
// omits the null on write, and the record must still read back.
public sealed class ControlPlaneApprovalNullTenantTests
{
    private static readonly DateTimeOffset ApprovedAt = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

    [UnitTest]
    public void DeployApproval_WithoutTenant_ReadsBack()
    {
        var approval = new DeployHumanApproval
        {
            WorkflowId = "wf-deploy",
            WorkflowVersion = 3,
            Action = "approve",
            ActorId = "user:reviewer",
            Channel = "console",
            SessionReference = "session-1",
            CapabilityReference = "capability-1",
            PlanHash = "sha256:plan",
            ApprovedAt = ApprovedAt,
        };

        var restored = RoundTrip(approval);

        restored.Should().Be(approval);
    }

    [UnitTest]
    public void CoordinatedReleaseApproval_WithoutTenant_ReadsBack()
    {
        var approval = new CoordinatedReleaseHumanApproval
        {
            Version = CoordinatedReleaseHumanApproval.CurrentVersion,
            WorkflowId = "wf-release",
            WorkflowVersion = 2,
            Gate = CoordinatedReleaseStep.ContainerRollout,
            ActorId = "user:reviewer",
            Channel = "console",
            SessionReference = "session-2",
            CapabilityReference = "capability-2",
            PlanHash = "sha256:release",
            ApprovedAt = ApprovedAt,
        };

        var restored = RoundTrip(approval);

        restored.Should().Be(approval);
    }

    // A row written before the fix has no tenantId property at all.
    private static T RoundTrip<T>(T value)
    {
        var typeInfo = (JsonTypeInfo<T>)ControlPlaneJsonContext.Default.GetTypeInfo(typeof(T))!;
        var json = JsonSerializer.Serialize(value, typeInfo);
        json.Should().NotContain("tenantId");
        return JsonSerializer.Deserialize(json, typeInfo)!;
    }
}
