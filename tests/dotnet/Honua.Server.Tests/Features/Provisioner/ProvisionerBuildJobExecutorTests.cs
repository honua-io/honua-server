// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Provisioner;
using Honua.Server.Features.Provisioner.BuildJobs;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Provisioner;

/// <summary>
/// Regression coverage for PR #1752: the default zero-config <c>local</c> backend enqueues
/// <see cref="ExecutionJobKind.GeocoderBuild"/> / <see cref="ExecutionJobKind.RouterBuild"/>
/// jobs, so an in-process <see cref="IJobExecutor"/> must be registered for each kind or
/// <c>JobExecutionService</c> never claims them and they stay <c>Queued</c> forever.
/// </summary>
public sealed class ProvisionerBuildJobExecutorTests
{
    [UnitTest]
    public void AddHonuaProvisionerBuildJobs_RegistersExecutorsForBothBuildKinds()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IUniversalProgressStore>(new InMemoryProgressStore());
        services.AddLogging();
        services.AddHonuaProvisionerBuildJobs(
            new ConfigurationBuilder().AddInMemoryCollection().Build());

        using var provider = services.BuildServiceProvider();
        var kinds = provider.GetServices<IJobExecutor>().Select(e => e.Kind).ToHashSet();

        kinds.Should().Contain(ExecutionJobKind.GeocoderBuild);
        kinds.Should().Contain(ExecutionJobKind.RouterBuild);
    }

    [UnitTest]
    public async Task ExecuteAsync_GeocoderBuild_CompletesAndPublishesArtifact()
    {
        var progressStore = new InMemoryProgressStore();
        var executor = ProvisionerBuildJobExecutor.ForGeocoder(
            progressStore, NullLogger<ProvisionerBuildJobExecutor>.Instance);

        executor.Kind.Should().Be(ExecutionJobKind.GeocoderBuild);

        var request = new GeocoderBuildRequest
        {
            SourceId = "census-tiger",
            ProductId = "addresses",
            Area = ParseArea("geoid:15009"),
            FeedstockTable = "od_census_tiger_addresses",
            ArtifactName = "maui",
            ArtifactKey = "locators/maui/maui.osm.pbf"
        };
        var job = JobFor(GeocoderBuildExecutionSpecBuilder.Build(request, new ProvisionerBuildBatchOptions()));
        var context = new RecordingContext(job.OperationId);

        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        context.PublishedArtifacts.Should().Contain("locators/maui/maui.osm.pbf");

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>(job.OperationId);
        progress!.Status.Should().Be(OperationStatus.Completed);
    }

    [UnitTest]
    public async Task ExecuteAsync_RouterBuild_InvalidSpec_FailsWithoutThrowing()
    {
        var progressStore = new InMemoryProgressStore();
        var executor = ProvisionerBuildJobExecutor.ForRouter(
            progressStore, NullLogger<ProvisionerBuildJobExecutor>.Instance);

        // Empty parameters: the spec decode fails on the first missing required key.
        var job = JobFor(new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.RouterBuild,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = "local",
            WorkloadName = "router-build:bad",
            Parameters = new Dictionary<string, string>()
        });
        var context = new RecordingContext(job.OperationId);

        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        context.PublishedArtifacts.Should().BeEmpty();

        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>(job.OperationId);
        progress!.Status.Should().Be(OperationStatus.Failed);
    }

    private static ProvisionerArea ParseArea(string value)
    {
        ProvisionerArea.TryParse(value, out var area, out _).Should().BeTrue();
        return area;
    }

    private static ExecutionJobRecord JobFor(ExecutionJobSpec spec)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = $"test-{Guid.NewGuid():N}",
            Status = ExecutionJobStatus.Running,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Running",
            Spec = spec
        };
    }

    private sealed class RecordingContext(string operationId) : IJobExecutionContext
    {
        public string OperationId { get; } = operationId;
        public List<string> PublishedArtifacts { get; } = [];

        public Task ReportProgressAsync(double? percentComplete, string? phase, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AppendLogAsync(ExecutionLogEntry entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
        {
            PublishedArtifacts.Add(artifactReference);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryProgressStore : IUniversalProgressStore
    {
        private readonly ConcurrentDictionary<string, IOperationProgress> _progress = new(StringComparer.Ordinal);

        public Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _progress[operationId] = progress;
            return Task.CompletedTask;
        }

        public Task<ProgressCompareAndSetResult> TrySetProgressAsync(string operationId, IOperationProgress progress, OperationStatus expectedStatus, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _progress[operationId] = progress;
            return Task.FromResult(ProgressCompareAndSetResult.Updated);
        }

        public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult(_progress.TryGetValue(operationId, out var p) ? p as TProgress : null);

        public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_progress.TryGetValue(operationId, out var p) ? p : null);

        public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
        {
            _progress.TryRemove(operationId, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_progress.Keys.ToArray());

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult<IReadOnlyList<TProgress>>(_progress.Values.OfType<TProgress>().ToArray());
    }
}
