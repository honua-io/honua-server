// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Core.Tests.Features.FeatureStore;

public sealed class VersionJobRunnerOutcomeTests
{
    [Theory]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    public async Task Reconcile_PostsOnlyWhenRequestedAndClean(bool withPost, bool canPost, bool conflicts, bool expectedPost)
    {
        await using var runtime = new JobRuntime();
        var versionId = Guid.NewGuid();
        var reconcile = new VersionReconcileResult(
            conflicts ? [new VersionReconcileConflict(1, 7, ReplicaConflictType.Attribute)] : [],
            canPost, 41, 2);
        runtime.Manager.Setup(manager => manager.ReconcileAsync(versionId,
                VersionReconcilePolicy.None, VersionConflictDetection.ByObject, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reconcile);
        runtime.Manager.Setup(manager => manager.PostAsync(versionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VersionPostResult(true, 3, 42, false));

        var pending = await runtime.Runner.StartReconcileAsync("svc", versionId,
            VersionReconcilePolicy.None, VersionConflictDetection.ByObject, withPost);
        var terminal = await runtime.Terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(VersionJobStatus.Pending, pending.Status);
        Assert.Equal(withPost, pending.WithPost);
        Assert.Equal(pending.JobId, terminal.JobId);
        Assert.Equal(VersionJobStatus.Succeeded, terminal.Status);
        Assert.Equal(expectedPost, terminal.Posted);
        Assert.Equal(conflicts ? 1 : 0, terminal.ConflictCount);
        Assert.Equal(2, terminal.AutoResolvedCount);
        Assert.Equal(expectedPost ? 3 : 0, terminal.AppliedChanges);
        Assert.Equal(expectedPost ? 42 : 41, terminal.ServerGeneration);
        Assert.True(terminal.CompletedAt >= terminal.StartedAt && terminal.StartedAt >= terminal.CreatedAt);
        runtime.Manager.Verify(manager => manager.ReconcileAsync(versionId,
            VersionReconcilePolicy.None, VersionConflictDetection.ByObject, It.IsAny<CancellationToken>()), Times.Once);
        runtime.Manager.Verify(manager => manager.PostAsync(versionId, It.IsAny<CancellationToken>()),
            expectedPost ? Times.Once() : Times.Never());
        runtime.Manager.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_WhenRefused_RecordsFailedOutcome(bool blockedByConflicts)
    {
        await using var runtime = new JobRuntime();
        runtime.Manager.Setup(manager => manager.PostAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VersionPostResult(false, 0, 0, blockedByConflicts));

        await runtime.Runner.StartPostAsync("svc", Guid.NewGuid());
        var terminal = await runtime.Terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(VersionJobStatus.Failed, terminal.Status);
        Assert.False(terminal.Posted);
        Assert.False(terminal.CanPost);
        Assert.Equal(blockedByConflicts, terminal.BlockedByConflicts);
        Assert.NotNull(terminal.ErrorMessage);
    }

    [UnitTest]
    public async Task Reconcile_WithPost_WhenDefaultDrifts_RecordsRefusedPost()
    {
        await using var runtime = new JobRuntime();
        runtime.Manager.Setup(manager => manager.ReconcileAsync(It.IsAny<Guid>(),
                VersionReconcilePolicy.None, VersionConflictDetection.ByAttribute, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VersionReconcileResult([], true, 41));
        runtime.Manager.Setup(manager => manager.PostAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VersionPostResult(false, 0, 0, true));

        await runtime.Runner.StartReconcileAsync("svc", Guid.NewGuid(),
            VersionReconcilePolicy.None, VersionConflictDetection.ByAttribute, true);
        var terminal = await runtime.Terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(VersionJobStatus.Failed, terminal.Status);
        Assert.True(terminal.BlockedByConflicts);
        Assert.False(terminal.Posted);
    }

    [UnitTest]
    public async Task Post_WhenProviderThrows_RecordsSanitizedFailure()
    {
        await using var runtime = new JobRuntime();
        runtime.Manager.Setup(manager => manager.PostAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("private-provider-connection-secret"));

        await runtime.Runner.StartPostAsync("svc", Guid.NewGuid());
        var terminal = await runtime.Terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(VersionJobStatus.Failed, terminal.Status);
        Assert.False(terminal.Posted);
        Assert.NotNull(terminal.ErrorMessage);
        Assert.DoesNotContain("private-provider", terminal.ErrorMessage, StringComparison.Ordinal);
    }

    private sealed class JobRuntime : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        public Mock<IVersionManager> Manager { get; } = new(MockBehavior.Strict);
        public TaskCompletionSource<VersionJob> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public VersionJobRunner Runner { get; }

        public JobRuntime()
        {
            var store = new Mock<IVersionJobStore>();
            store.Setup(value => value.SaveAsync(It.IsAny<VersionJob>(), It.IsAny<CancellationToken>()))
                .Callback<VersionJob, CancellationToken>((job, _) =>
                {
                    if (job.Status is VersionJobStatus.Succeeded or VersionJobStatus.Failed or VersionJobStatus.LockContended)
                    {
                        Terminal.TrySetResult(job);
                    }
                }).Returns(Task.CompletedTask);
            var services = new ServiceCollection();
            services.AddSingleton(Manager.Object);
            services.AddSingleton(store.Object);
            _provider = services.BuildServiceProvider();
            Runner = new VersionJobRunner(_provider.GetRequiredService<IServiceScopeFactory>(),
                store.Object, NullLogger<VersionJobRunner>.Instance);
        }

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }
}
