// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;

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
}
