// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class AwsBatchRequestCancellationTests
{
    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("submit", false)]
    [InlineData("observe", false)]
    [InlineData("discover", false)]
    [InlineData("cancel", false)]
    [InlineData("terminate", false)]
    [InlineData("cancel-discovery", false)]
    [InlineData("submit", true)]
    [InlineData("observe", true)]
    [InlineData("discover", true)]
    [InlineData("cancel", true)]
    [InlineData("terminate", true)]
    [InlineData("cancel-discovery", true)]
    public async Task RequestCancellation_OnlyCallerCancellationPropagates(string operation, bool callerCancels)
    {
        using var caller = new CancellationTokenSource();
        var client = Substitute.For<IAwsBatchJobClient>();
        Task<T> Interrupt<T>()
        {
            if (callerCancels)
            {
                caller.Cancel();
            }
            return Task.FromException<T>(new TaskCanceledException("Provider request deadline."));
        }
        client.SubmitJobAsync(Arg.Any<AwsBatchJobSubmission>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interrupt<AwsBatchSubmitResult>());
        client.ListJobsByNameAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interrupt<IReadOnlyList<AwsBatchJobState>>());
        client.DescribeJobAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => operation == "observe"
                ? Interrupt<AwsBatchJobState?>()
                : Task.FromResult<AwsBatchJobState?>(new AwsBatchJobState
                {
                    JobId = "original-provider-job",
                    Status = operation == "cancel" ? "RUNNABLE" : "RUNNING"
                }));
        client.CancelJobAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interrupt<bool>());
        client.TerminateJobAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interrupt<bool>());
        var backend = new AwsBatchComputeBackend(client, NullLogger<AwsBatchComputeBackend>.Instance);
        var job = new ExecutionJobRecord
        {
            OperationId = "handoff-cancelled-request",
            Status = ExecutionJobStatus.Running,
            PercentComplete = 37,
            ProviderOperationId = operation is "discover" or "cancel-discovery"
                ? AwsBatchComputeBackend.PendingSubmissionMarkerPrefix + "original-job-name"
                : "original-provider-job",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = backend.BackendName,
                WorkloadName = "gp",
                Parameters = new Dictionary<string, string>
                {
                    [AwsBatchParameterKeys.JobDefinitionArn] = "worker:1",
                    [AwsBatchParameterKeys.JobQueueArn] = "queue"
                }
            }
        };

        async Task Act()
        {
            if (operation == "submit")
            {
                var result = await backend.StartAsync(job, caller.Token);
                result.Status.Should().Be(ExecutionJobStatus.Queued);
                AwsBatchComputeBackend.TryExtractPendingJobName(result.ProviderOperationId, out var name).Should().BeTrue();
                name.Should().Be(AwsBatchComputeBackend.BuildJobName(job.OperationId, job.AttemptCount));
            }
            else
            {
                var result = operation is "observe" or "discover"
                    ? await backend.ObserveAsync(job, caller.Token)
                    : await backend.CancelAsync(job, caller.Token);
                result.Status.Should().Be(job.Status);
                result.ProviderOperationId.Should().Be(job.ProviderOperationId);
                result.PercentComplete.Should().Be(37);
            }
        }

        if (callerCancels)
        {
            await ((Func<Task>)Act).Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await Act();
        }
    }
}
