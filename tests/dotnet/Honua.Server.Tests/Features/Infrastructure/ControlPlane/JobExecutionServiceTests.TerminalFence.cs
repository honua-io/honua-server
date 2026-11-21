// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class JobExecutionServiceTests
{
    private const string TerminalCommittedFence = "terminal-committed-registration-pending";

    [UnitTest]
    public async Task ProcessJob_ArmedTerminalFence_HoldsAfterDurableSuccessBeforeQueueCleanup()
    {
        var root = Directory.CreateTempSubdirectory("honua-qualification-terminal-");
        var previousRoot = Environment.GetEnvironmentVariable(ExecutionQualificationBarrier.RootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ExecutionQualificationBarrier.RootEnvironmentVariable, root.FullName);
            var job = CreateProvisioningJob();
            var directory = PrepareTerminalFence(root, job.OperationId);
            var store = Substitute.For<IExecutionJobStore>();
            store.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(_ => job);
            store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
                .Returns(call => { job = call.Arg<ExecutionJobRecord>(); return true; });
            var queue = Substitute.For<IJobQueue>();
            using var service = new JobExecutionService(queue, store, [CreateSucceedingExecutor()],
                new ExecutionJobCancellationTokens(), [], null, NullLogger<JobExecutionService>.Instance);

            var execution = InvokeProcessJobAsync(service, job.OperationId, job.ClaimedBy!);
            await WaitForFenceReadyAsync(directory);

            Assert.False(execution.IsCompleted);
            Assert.Equal(ExecutionJobStatus.Succeeded, job.Status);
            await queue.DidNotReceive().RemoveAsync(job.OperationId, Arg.Any<CancellationToken>());
            await File.WriteAllTextAsync(Path.Join(directory, $"{TerminalCommittedFence}.release"), string.Empty);
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
            await queue.Received(1).RemoveAsync(job.OperationId, Arg.Any<CancellationToken>());
        }
        finally
        {
            // Release a regressed hold before removing its directory so no wait outlives the test.
            await File.WriteAllTextAsync(
                Path.Join(root.FullName, "job-1", $"{TerminalCommittedFence}.release"), string.Empty);
            Environment.SetEnvironmentVariable(ExecutionQualificationBarrier.RootEnvironmentVariable, previousRoot);
            root.Delete(recursive: true);
        }
    }

    private static string PrepareTerminalFence(DirectoryInfo root, string operationId)
    {
        var directory = Directory.CreateDirectory(Path.Join(root.FullName, operationId)).FullName;
        foreach (var file in new[]
                 {
                     "claimed.release",
                     "artifact-reference-published-terminal-cas-pending.release",
                     $"{TerminalCommittedFence}.arm",
                 })
        {
            File.WriteAllText(Path.Join(directory, file), string.Empty);
        }

        return directory;
    }

    private static IJobExecutor CreateSucceedingExecutor()
    {
        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(JobExecutionResult.Succeeded());
        return executor;
    }

    private static async Task WaitForFenceReadyAsync(string directory)
    {
        var readyPath = Path.Join(directory, $"{TerminalCommittedFence}.ready.json");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(readyPath))
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
