// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using System.Net;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.Server.Features.Admin.TileOperations;

namespace Honua.Server.Tests.Features.Admin.Jobs;

public sealed partial class ConsoleJobEndpointsTests
{
    [IntegrationTheory]
    [InlineData("accepted", 1)]
    [InlineData("queue-failure", 0)]
    [Endpoint("POST /api/v1/admin/jobs/{jobId}/retry")]
    public async Task Retry_TileJob_AccountsForQueueAdmission(string outcome, long expectedDepth)
    {
        var operation = Guid.NewGuid().ToString("N");
        var failed = (await _jobStore.GetAsync("job-failed"))!;
        await _jobStore.SetAsync(failed with
        {
            Spec = failed.Spec with
            {
                Kind = ExecutionJobKind.TileCache,
                Parameters = new Dictionary<string, string>(failed.Spec.Parameters)
                {
                    [TileCacheJobParameterKeys.Operation] = operation
                }
            }
        });
        _jobQueue.ThrowOnRequeue = outcome == "queue-failure";
        using var depth = new TileQueueDepthObserver(operation);

        var retry = await _client.PostAsync("/api/v1/admin/jobs/job-failed/retry", null);

        retry.StatusCode.Should().Be(outcome == "accepted" ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
        depth.Value.Should().Be(expectedDepth);
    }
}
