// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Tests.Features.ControlPlane;

/// <summary>
/// Tests for job retry, heartbeat, and timeout policy models.
/// </summary>
public class JobPoliciesTests
{
    // -----------------------------------------------------------------------
    // JobRetryPolicy
    // -----------------------------------------------------------------------

    [Fact]
    public void RetryPolicy_Default_HasReasonableDefaults()
    {
        var policy = JobRetryPolicy.Default;
        Assert.Equal(3, policy.MaxAttempts);
        Assert.Equal(BackoffStrategy.Exponential, policy.Strategy);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.BaseDelay);
        Assert.Equal(TimeSpan.FromMinutes(10), policy.MaxDelay);
    }

    [Fact]
    public void RetryPolicy_None_DoesNotRetry()
    {
        var policy = JobRetryPolicy.None;
        Assert.Equal(1, policy.MaxAttempts);
        Assert.False(policy.ShouldRetry(1));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void RetryPolicy_ShouldRetry_RespectsMaxAttempts(int attemptCount, bool expected)
    {
        var policy = JobRetryPolicy.Default; // MaxAttempts = 3
        Assert.Equal(expected, policy.ShouldRetry(attemptCount));
    }

    [Fact]
    public void RetryPolicy_ComputeDelay_FirstAttemptIsZero()
    {
        var policy = JobRetryPolicy.Default;
        Assert.Equal(TimeSpan.Zero, policy.ComputeDelay(1));
    }

    [Fact]
    public void RetryPolicy_ComputeDelay_FixedStrategy_ReturnsSameDelay()
    {
        var policy = new JobRetryPolicy
        {
            MaxAttempts = 5,
            Strategy = BackoffStrategy.Fixed,
            BaseDelay = TimeSpan.FromSeconds(10),
            MaxDelay = TimeSpan.FromMinutes(5)
        };

        Assert.Equal(TimeSpan.FromSeconds(10), policy.ComputeDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.ComputeDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.ComputeDelay(4));
    }

    [Fact]
    public void RetryPolicy_ComputeDelay_LinearStrategy_ScalesLinearly()
    {
        var policy = new JobRetryPolicy
        {
            MaxAttempts = 5,
            Strategy = BackoffStrategy.Linear,
            BaseDelay = TimeSpan.FromSeconds(10),
            MaxDelay = TimeSpan.FromMinutes(5)
        };

        Assert.Equal(TimeSpan.FromSeconds(10), policy.ComputeDelay(2));  // retryIndex=0: 10*(0+1)
        Assert.Equal(TimeSpan.FromSeconds(20), policy.ComputeDelay(3));  // retryIndex=1: 10*(1+1)
        Assert.Equal(TimeSpan.FromSeconds(30), policy.ComputeDelay(4));  // retryIndex=2: 10*(2+1)
    }

    [Fact]
    public void RetryPolicy_ComputeDelay_ExponentialStrategy_DoublesEachTime()
    {
        var policy = new JobRetryPolicy
        {
            MaxAttempts = 5,
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = TimeSpan.FromSeconds(10),
            MaxDelay = TimeSpan.FromMinutes(5)
        };

        Assert.Equal(TimeSpan.FromSeconds(10), policy.ComputeDelay(2));  // 10 * 2^0
        Assert.Equal(TimeSpan.FromSeconds(20), policy.ComputeDelay(3));  // 10 * 2^1
        Assert.Equal(TimeSpan.FromSeconds(40), policy.ComputeDelay(4));  // 10 * 2^2
    }

    [Fact]
    public void RetryPolicy_ComputeDelay_CapsAtMaxDelay()
    {
        var policy = new JobRetryPolicy
        {
            MaxAttempts = 10,
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = TimeSpan.FromSeconds(30),
            MaxDelay = TimeSpan.FromMinutes(2)
        };

        // attempt 5: retryIndex=3 => 30 * 2^3 = 240 seconds = 4 minutes > MaxDelay
        var delay = policy.ComputeDelay(5);
        Assert.Equal(TimeSpan.FromMinutes(2), delay);
    }

    // -----------------------------------------------------------------------
    // JobHeartbeatPolicy
    // -----------------------------------------------------------------------

    [Fact]
    public void HeartbeatPolicy_Default_TimeoutExceedsInterval()
    {
        var policy = JobHeartbeatPolicy.Default;
        Assert.True(policy.Timeout > policy.Interval);
    }

    [Fact]
    public void HeartbeatPolicy_IsExpired_ReturnsFalse_WhenWithinTimeout()
    {
        var policy = JobHeartbeatPolicy.Default;
        var now = DateTimeOffset.UtcNow;
        var lastHeartbeat = now.AddSeconds(-30); // Exactly at interval

        Assert.False(policy.IsExpired(lastHeartbeat, now));
    }

    [Fact]
    public void HeartbeatPolicy_IsExpired_ReturnsTrue_WhenBeyondTimeout()
    {
        var policy = JobHeartbeatPolicy.Default; // Timeout = 90s
        var now = DateTimeOffset.UtcNow;
        var lastHeartbeat = now.AddSeconds(-91);

        Assert.True(policy.IsExpired(lastHeartbeat, now));
    }

    [Fact]
    public void HeartbeatPolicy_IsExpired_ReturnsTrue_WhenExactlyAtTimeout()
    {
        var policy = new JobHeartbeatPolicy
        {
            Interval = TimeSpan.FromSeconds(10),
            Timeout = TimeSpan.FromSeconds(30)
        };
        var now = DateTimeOffset.UtcNow;
        var lastHeartbeat = now.AddSeconds(-31);

        Assert.True(policy.IsExpired(lastHeartbeat, now));
    }

    // -----------------------------------------------------------------------
    // JobTimeoutPolicy
    // -----------------------------------------------------------------------

    [Fact]
    public void TimeoutPolicy_Default_IsOneHour()
    {
        Assert.Equal(TimeSpan.FromHours(1), JobTimeoutPolicy.Default.MaxDuration);
    }

    [Fact]
    public void TimeoutPolicy_LongRunning_Is24Hours()
    {
        Assert.Equal(TimeSpan.FromHours(24), JobTimeoutPolicy.LongRunning.MaxDuration);
    }

    [Fact]
    public void TimeoutPolicy_IsExpired_ReturnsFalse_WhenWithinDuration()
    {
        var policy = JobTimeoutPolicy.Default;
        var now = DateTimeOffset.UtcNow;
        var startedAt = now.AddMinutes(-30);

        Assert.False(policy.IsExpired(startedAt, now));
    }

    [Fact]
    public void TimeoutPolicy_IsExpired_ReturnsTrue_WhenBeyondDuration()
    {
        var policy = JobTimeoutPolicy.Default;
        var now = DateTimeOffset.UtcNow;
        var startedAt = now.AddHours(-1).AddSeconds(-1);

        Assert.True(policy.IsExpired(startedAt, now));
    }

    // -----------------------------------------------------------------------
    // ExecutionJobRecord with new fields
    // -----------------------------------------------------------------------

    [Fact]
    public void ExecutionJobRecord_NewFields_HaveSaneDefaults()
    {
        var record = new ExecutionJobRecord
        {
            OperationId = "test-1",
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test"
            }
        };

        Assert.Null(record.ClaimedBy);
        Assert.Null(record.ClaimedAt);
        Assert.Null(record.LastHeartbeatAt);
        Assert.Equal(0, record.AttemptCount);
        Assert.Null(record.NextRetryAt);
        Assert.Null(record.RetryPolicy);
        Assert.Null(record.HeartbeatPolicy);
        Assert.Null(record.TimeoutPolicy);
        Assert.Empty(record.ArtifactReferences);
    }

    [Fact]
    public void ExecutionJobRecord_WithClaimFields_PreservesValues()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new ExecutionJobRecord
        {
            OperationId = "test-2",
            Status = ExecutionJobStatus.Running,
            CreatedAt = now.AddMinutes(-5),
            UpdatedAt = now,
            ClaimedBy = "worker-1",
            ClaimedAt = now.AddMinutes(-1),
            LastHeartbeatAt = now,
            AttemptCount = 2,
            RetryPolicy = JobRetryPolicy.Default,
            HeartbeatPolicy = JobHeartbeatPolicy.Default,
            TimeoutPolicy = JobTimeoutPolicy.Default,
            ArtifactReferences = ["artifact://output/layer1"],
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test"
            }
        };

        Assert.Equal("worker-1", record.ClaimedBy);
        Assert.Equal(now.AddMinutes(-1), record.ClaimedAt);
        Assert.Equal(now, record.LastHeartbeatAt);
        Assert.Equal(2, record.AttemptCount);
        Assert.NotNull(record.RetryPolicy);
        Assert.NotNull(record.HeartbeatPolicy);
        Assert.NotNull(record.TimeoutPolicy);
        Assert.Single(record.ArtifactReferences);
    }

    // -----------------------------------------------------------------------
    // JobExecutionResult
    // -----------------------------------------------------------------------

    [Fact]
    public void JobExecutionResult_Succeeded_SetsCorrectStatus()
    {
        var result = JobExecutionResult.Succeeded();
        Assert.Equal(ExecutionJobStatus.Succeeded, result.Status);
        Assert.Null(result.ErrorMessage);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void JobExecutionResult_Failed_SetsErrorMessage()
    {
        var result = JobExecutionResult.Failed("Something broke", ["warn1"]);
        Assert.Equal(ExecutionJobStatus.Failed, result.Status);
        Assert.Equal("Something broke", result.ErrorMessage);
        Assert.Single(result.Warnings);
    }

    // -----------------------------------------------------------------------
    // ExecutionLogEntry
    // -----------------------------------------------------------------------

    [Fact]
    public void ExecutionLogEntry_CanBeConstructed_WithMinimalFields()
    {
        var entry = new ExecutionLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = ExecutionLogLevel.Info,
            Message = "Processing step 1"
        };

        Assert.Equal(ExecutionLogLevel.Info, entry.Level);
        Assert.Null(entry.Phase);
        Assert.Null(entry.Metadata);
    }

    [Fact]
    public void ExecutionLogEntry_CanInclude_PhaseAndMetadata()
    {
        var entry = new ExecutionLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = ExecutionLogLevel.Warning,
            Message = "CRS mismatch",
            Phase = "Step 2/5",
            Metadata = new Dictionary<string, string>
            {
                ["sourceCrs"] = "EPSG:4326",
                ["targetCrs"] = "EPSG:3857"
            }
        };

        Assert.Equal("Step 2/5", entry.Phase);
        Assert.Equal(2, entry.Metadata!.Count);
    }
}
