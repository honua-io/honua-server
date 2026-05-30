// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class AwsBatchStateMapperTests
{
    [Theory]
    [InlineData("SUBMITTED", ExecutionJobStatus.Queued)]
    [InlineData("PENDING", ExecutionJobStatus.Queued)]
    [InlineData("RUNNABLE", ExecutionJobStatus.Queued)]
    [InlineData("STARTING", ExecutionJobStatus.Provisioning)]
    [InlineData("RUNNING", ExecutionJobStatus.Running)]
    [InlineData("SUCCEEDED", ExecutionJobStatus.Succeeded)]
    [InlineData("FAILED", ExecutionJobStatus.Failed)]
    public void MapStatus_MapsKnownAwsStatuses(string awsStatus, ExecutionJobStatus expected)
    {
        AwsBatchStateMapper.MapStatus(awsStatus).Should().Be(expected);
    }

    [Theory]
    [InlineData("submitted", ExecutionJobStatus.Queued)]
    [InlineData("  RUNNING  ", ExecutionJobStatus.Running)]
    [InlineData("Succeeded", ExecutionJobStatus.Succeeded)]
    public void MapStatus_IsCaseAndWhitespaceInsensitive(string awsStatus, ExecutionJobStatus expected)
    {
        AwsBatchStateMapper.MapStatus(awsStatus).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("UNKNOWN_FUTURE_STATE")]
    public void MapStatus_UnknownOrMissingStatuses_DefaultToRunning(string? awsStatus)
    {
        AwsBatchStateMapper.MapStatus(awsStatus).Should().Be(ExecutionJobStatus.Running);
    }

    [Theory]
    [InlineData("SUBMITTED", true)]
    [InlineData("PENDING", true)]
    [InlineData("RUNNABLE", true)]
    [InlineData("STARTING", false)]
    [InlineData("RUNNING", false)]
    [InlineData("SUCCEEDED", false)]
    [InlineData("FAILED", false)]
    [InlineData(null, false)]
    public void CanCancelWithoutTerminate_ReturnsTrueOnlyForPreSchedulingStates(string? awsStatus, bool expected)
    {
        AwsBatchStateMapper.CanCancelWithoutTerminate(awsStatus).Should().Be(expected);
    }

    [Theory]
    [InlineData("SUBMITTED", true)]
    [InlineData("PENDING", true)]
    [InlineData("RUNNABLE", true)]
    [InlineData("STARTING", true)]
    [InlineData("RUNNING", true)]
    [InlineData("SUCCEEDED", false)]
    [InlineData("FAILED", false)]
    public void IsInFlight_ReflectsTerminalMapping(string awsStatus, bool expected)
    {
        AwsBatchStateMapper.IsInFlight(awsStatus).Should().Be(expected);
    }

    [Fact]
    public void MapStatusWithReason_PromotesFailedWithCancelReasonToCancelled()
    {
        var mapped = AwsBatchStateMapper.MapStatusWithReason(
            "FAILED",
            statusReason: AwsBatchStateMapper.CancelReason + " (request id abc)");

        mapped.Should().Be(ExecutionJobStatus.Cancelled);
    }

    [Fact]
    public void MapStatusWithReason_LeavesFailedUnchangedWhenReasonIsWorkloadFailure()
    {
        var mapped = AwsBatchStateMapper.MapStatusWithReason(
            "FAILED",
            statusReason: "Container exited with non-zero status 137");

        mapped.Should().Be(ExecutionJobStatus.Failed);
    }

    [Fact]
    public void MapStatusWithReason_IgnoresCancelReasonForNonTerminalStatuses()
    {
        var mapped = AwsBatchStateMapper.MapStatusWithReason(
            "RUNNING",
            statusReason: AwsBatchStateMapper.CancelReason);

        mapped.Should().Be(ExecutionJobStatus.Running);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Container failure", false)]
    [InlineData("Cancelled by Honua control plane", true)]
    [InlineData("Workflow abort: Cancelled by Honua control plane", true)]
    public void MatchesCancelReason_DetectsHonuaCancelMarker(string? reason, bool expected)
    {
        AwsBatchStateMapper.MatchesCancelReason(reason).Should().Be(expected);
    }
}
