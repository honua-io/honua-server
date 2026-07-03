// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.FeatureStore;

/// <summary>
/// PA-021: <see cref="VersionJobRunner"/> detaches reconcile/post execution onto
/// <c>Task.Run</c>, which flows the ambient <see cref="ExecutionContext"/> by default. Without
/// an explicit fix, the job's span would silently become a child of the HTTP request's activity
/// — one that has already ended by the time the background job finishes. These tests assert the
/// job's span is instead a root span carrying an <see cref="ActivityLink"/> back to the caller.
/// </summary>
public sealed class VersionJobRunnerTelemetryTests
{
    [UnitTest]
    public async Task StartPostAsync_WithAmbientCallerActivity_EmitsRootSpanLinkedToCaller()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IVersionManager>(new FakeVersionManager());
        await using var provider = services.BuildServiceProvider();

        var runner = new VersionJobRunner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new InMemoryVersionJobStore(),
            NullLogger<VersionJobRunner>.Instance);

        var jobActivityStopped = new TaskCompletionSource<Activity>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var jobListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Honua",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName.StartsWith("VersionJob.", StringComparison.Ordinal))
                {
                    jobActivityStopped.TrySetResult(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(jobListener);

        using var callerSource = new ActivitySource("Test.VersionJobRunner.Caller");
        using var callerListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Test.VersionJobRunner.Caller",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(callerListener);

        ActivityContext callerContext;
        using (var callerActivity = callerSource.StartActivity("http.request"))
        {
            callerActivity.Should().NotBeNull();
            callerContext = callerActivity!.Context;

            // Simulate the HTTP request completing (and its activity ending) while the
            // fire-and-forget job keeps running in the background.
            await runner.StartPostAsync("svc", Guid.NewGuid());
        }

        var jobActivity = await jobActivityStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));

        jobActivity.Parent.Should().BeNull(
            "the job runs after the caller's request activity ends and must not be an implicit child of it");
        jobActivity.ParentSpanId.Should().NotBe(callerContext.SpanId);
        jobActivity.Links.Should().ContainSingle(link =>
            link.Context.TraceId == callerContext.TraceId && link.Context.SpanId == callerContext.SpanId);
    }

    private sealed class FakeVersionManager : IVersionManager
    {
        public bool SupportsVersioning => true;

        public Task<GdbVersion> CreateAsync(CreateVersionRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid versionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GdbVersion?> AlterAsync(AlterVersionRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<GdbVersion>> ListAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<VersionContext?> ResolveAsync(string? gdbVersion, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<VersionReconcileResult> ReconcileAsync(
            Guid versionId,
            VersionReconcilePolicy policy = VersionReconcilePolicy.None,
            VersionConflictDetection detection = VersionConflictDetection.ByAttribute,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<VersionReconcileConflict>> GetPendingConflictsAsync(
            Guid versionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<VersionConflictResolutionResult> ResolveConflictsAsync(
            Guid versionId,
            IReadOnlyList<VersionConflictResolution> resolutions,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<VersionPostResult> PostAsync(Guid versionId, CancellationToken cancellationToken = default)
            => Task.FromResult(new VersionPostResult(Posted: true, AppliedChanges: 3, ServerGeneration: 1, BlockedByConflicts: false));
    }
}
