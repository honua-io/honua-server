// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Publishing.Domain;

public class RefreshPolicyTests
{
    [UnitTest]
    public void Manual_ShouldCreateManualPolicy()
    {
        var policy = RefreshPolicy.Manual();

        policy.Mode.Should().Be(RefreshMode.Manual);
        policy.Interval.Should().BeNull();
        policy.LastRefreshAt.Should().BeNull();
        policy.NextRefreshAt.Should().BeNull();
    }

    [UnitTest]
    public void Scheduled_ShouldCreatePolicyWithIntervalAndNextRefresh()
    {
        var interval = TimeSpan.FromHours(4);

        var policy = RefreshPolicy.Scheduled(interval);

        policy.Mode.Should().Be(RefreshMode.Scheduled);
        policy.Interval.Should().Be(interval);
        policy.LastRefreshAt.Should().BeNull();
        policy.NextRefreshAt.Should().NotBeNull();
        policy.NextRefreshAt.Should().BeCloseTo(DateTimeOffset.UtcNow + interval, TimeSpan.FromSeconds(2));
    }

    [UnitTest]
    public void WithRefreshCompleted_Manual_ShouldUpdateLastRefreshWithoutNext()
    {
        var policy = RefreshPolicy.Manual();

        var updated = policy.WithRefreshCompleted();

        updated.LastRefreshAt.Should().NotBeNull();
        updated.LastRefreshAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
        updated.NextRefreshAt.Should().BeNull();
    }

    [UnitTest]
    public void WithRefreshCompleted_Scheduled_ShouldAdvanceNextRefreshTime()
    {
        var interval = TimeSpan.FromHours(2);
        var policy = RefreshPolicy.Scheduled(interval);

        var updated = policy.WithRefreshCompleted();

        updated.LastRefreshAt.Should().NotBeNull();
        updated.NextRefreshAt.Should().NotBeNull();
        updated.NextRefreshAt.Should().BeCloseTo(DateTimeOffset.UtcNow + interval, TimeSpan.FromSeconds(2));
    }

    [UnitTest]
    public void WithRefreshCompleted_ShouldPreserveMode()
    {
        var scheduled = RefreshPolicy.Scheduled(TimeSpan.FromMinutes(30));
        var manual = RefreshPolicy.Manual();

        scheduled.WithRefreshCompleted().Mode.Should().Be(RefreshMode.Scheduled);
        manual.WithRefreshCompleted().Mode.Should().Be(RefreshMode.Manual);
    }
}

public class RefreshRequestTests
{
    [UnitTest]
    public void Create_ShouldCreateRequestWithTimestamp()
    {
        var request = RefreshRequest.Create("req-001", "svc-001", RefreshScope.Full);

        request.RequestId.Should().Be("req-001");
        request.ServiceId.Should().Be("svc-001");
        request.Scope.Should().Be(RefreshScope.Full);
        request.RequestedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
    }

    [UnitTest]
    public void Create_WithAudit_ShouldPopulateAuditInfo()
    {
        var audit = new OperationAuditInfo
        {
            RequestedBy = "scheduler",
            CorrelationId = "corr-123"
        };

        var request = RefreshRequest.Create("req-002", "svc-002", RefreshScope.Incremental, audit);

        request.Audit.RequestedBy.Should().Be("scheduler");
        request.Audit.CorrelationId.Should().Be("corr-123");
        request.Scope.Should().Be(RefreshScope.Incremental);
    }
}

public class RefreshResultTests
{
    [UnitTest]
    public void CreateSucceeded_ShouldCreateSuccessfulResult()
    {
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-10);

        var result = RefreshResult.CreateSucceeded("req-001", "svc-001", startedAt, 42);

        result.RequestId.Should().Be("req-001");
        result.ServiceId.Should().Be("svc-001");
        result.Outcome.Should().Be(RefreshOutcome.Succeeded);
        result.StartedAt.Should().Be(startedAt);
        result.CompletedAt.Should().NotBeNull();
        result.ItemsUpdated.Should().Be(42);
        result.Errors.Should().BeEmpty();
        result.Warnings.Should().BeEmpty();
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [UnitTest]
    public void CreateSucceeded_WithWarnings_ShouldIncludeWarnings()
    {
        var warnings = new[] { "Truncated field 'notes'" };

        var result = RefreshResult.CreateSucceeded("req-002", "svc-002", DateTimeOffset.UtcNow, 10, warnings);

        result.Outcome.Should().Be(RefreshOutcome.Succeeded);
        result.Warnings.Should().HaveCount(1);
    }

    [UnitTest]
    public void CreateFailed_ShouldCreateFailedResult()
    {
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        var errors = new[] { "Source table dropped", "Connection timeout" };

        var result = RefreshResult.CreateFailed("req-003", "svc-003", startedAt, errors, 3);

        result.Outcome.Should().Be(RefreshOutcome.Failed);
        result.Errors.Should().HaveCount(2);
        result.ItemsUpdated.Should().Be(3);
        result.CompletedAt.Should().NotBeNull();
    }

    [UnitTest]
    public void Duration_WhenNotCompleted_ShouldReturnElapsedSoFar()
    {
        var result = new RefreshResult
        {
            RequestId = "req-dur",
            ServiceId = "svc-dur",
            Outcome = RefreshOutcome.Succeeded,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5)
        };

        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }
}
