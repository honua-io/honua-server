// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class JobReconciliationServiceTests
{
    [UnitTheory]
    [InlineData(ExecutionJobStatus.Running, false)]
    [InlineData(ExecutionJobStatus.Running, true)]
    [InlineData(ExecutionJobStatus.Cancelled, false)]
    [InlineData(ExecutionJobStatus.Cancelled, true)]
    public async Task Sweep_ImportExpiry_RequeuesForReceiptRecovery(ExecutionJobStatus cancellationStatus, bool executionExpired)
    {
        var durable = CreateRunningJob(attemptCount: 3, retryPolicy: new JobRetryPolicy { MaxAttempts = 5 },
            timeoutPolicy: new JobTimeoutPolicy { MaxDuration = executionExpired ? TimeSpan.FromSeconds(1) : TimeSpan.FromHours(1) });
        durable = durable with
        {
            Spec = durable.Spec with { Kind = ExecutionJobKind.GeoservicesImport },
            CancellationRequestedAt = cancellationStatus == ExecutionJobStatus.Cancelled ? DateTimeOffset.UtcNow : null
        };
        var store = Substitute.For<IExecutionJobStore>();
        store.ListActiveAsync(null, null, Arg.Any<CancellationToken>()).Returns(_ => [durable]);
        store.GetAsync(durable.OperationId, Arg.Any<CancellationToken>()).Returns(_ => durable);
        store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { durable = call.Arg<ExecutionJobRecord>(); return true; });
        var queue = Substitute.For<IJobQueue>();
        using var service = new JobReconciliationService(store, queue, Substitute.For<IQueueClaimReconciler>(),
            new ExecutionJobCancellationTokens(), [], null, NullLogger<JobReconciliationService>.Instance);

        await RunSingleSweepAsync(service, CancellationToken.None);

        Assert.Equal(ExecutionJobStatus.Queued, durable.Status);
        Assert.Equal(cancellationStatus == ExecutionJobStatus.Cancelled, durable.CancellationRequestedAt.HasValue);
        Assert.Null(durable.ClaimedBy);
        Assert.Null(durable.CompletedAt);
        Assert.Equal(3, durable.AttemptCount);
        Assert.Equal(executionExpired ? 3 : 5, durable.RetryPolicy!.MaxAttempts);
        await queue.Received(1).RequeueAsync(durable.OperationId, durable.Priority,
            TimeSpan.FromSeconds(5), Arg.Any<CancellationToken>());
        await queue.DidNotReceive().RemoveAsync(durable.OperationId, Arg.Any<CancellationToken>());
    }
}
