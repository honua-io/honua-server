// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin.Jobs;

// A cancel is applied asynchronously, so a job can stay Running after the signal is recorded.
// The actions must not offer a second cancel meanwhile.
public sealed partial class ConsoleJobEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}")]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/actions")]
    public async Task RunningJobWithPendingCancel_DoesNotOfferCancelAgain()
    {
        var now = DateTimeOffset.UtcNow;
        _jobStore.Set(
            CreateJob("job-cancel-pending", ExecutionJobStatus.Running, now.AddMinutes(-5), "corr-cancel-pending") with
            {
                ClaimedAt = now.AddMinutes(-4),
                CancellationRequestedAt = now.AddSeconds(-30)
            },
            CreateJob("job-cancel-open", ExecutionJobStatus.Running, now.AddMinutes(-5), "corr-cancel-open") with
            {
                ClaimedAt = now.AddMinutes(-4)
            });

        using var pending = await ReadJsonAsync(await _client.GetAsync("/api/v1/admin/jobs/job-cancel-pending/actions"));
        var cancel = pending.RootElement.GetProperty("actions").EnumerateArray()
            .Single(action => action.GetProperty("name").GetString() == "cancel");
        cancel.GetProperty("allowed").GetBoolean().Should().BeFalse();
        cancel.GetProperty("disabledReason").GetString().Should().Be("cancellation requested; waiting for the worker to stop");

        var detailResponse = await _client.GetAsync("/api/v1/admin/jobs/job-cancel-pending");
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var detail = await ReadJsonAsync(detailResponse);
        detail.RootElement.GetProperty("actions").EnumerateArray()
            .Single(action => action.GetProperty("name").GetString() == "cancel")
            .GetProperty("allowed").GetBoolean().Should().BeFalse();

        using var open = await ReadJsonAsync(await _client.GetAsync("/api/v1/admin/jobs/job-cancel-open/actions"));
        open.RootElement.GetProperty("actions").EnumerateArray()
            .Single(action => action.GetProperty("name").GetString() == "cancel")
            .GetProperty("allowed").GetBoolean().Should().BeTrue();
    }
}
