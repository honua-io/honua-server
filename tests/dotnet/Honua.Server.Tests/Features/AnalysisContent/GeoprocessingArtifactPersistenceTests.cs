// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AnalysisContent;
using Honua.Core.Features.AnalysisContent.Abstractions;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Ai.AnalysisContent;
using Honua.Geoprocessing;
using Honua.Server.Features.ControlPlane;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.AnalysisContent;

[Protocol(TestProtocols.Admin)]
public sealed class GeoprocessingArtifactPersistenceTests
{
    [UnitTest]
    [Operation(Operations.ProcessExecution)]
    public async Task TerminalCallback_WithScopedAnalysisContentStore_PersistsDurableArtifactRecord()
    {
        var artifacts = new Dictionary<string, ResultArtifactRecord>(StringComparer.Ordinal);
        var services = new ServiceCollection();
        services.AddSingleton<IUniversalProgressStore, NullProgressStore>();
        services.AddSingleton<IProcessCatalog, EmptyProcessCatalog>();
        services.AddSingleton<IGeoprocessingResultPackageStore, NullResultPackageStore>();
        services.AddScoped<IAnalysisContentStore>(_ => new RecordingAnalysisContentStore(artifacts));
        services.AddSingleton<ILogger<GeoprocessingJobTerminalCallback>>(
            NullLogger<GeoprocessingJobTerminalCallback>.Instance);
        services.AddSingleton<IJobTerminalCallback, GeoprocessingJobTerminalCallback>();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

        var callback = provider.GetRequiredService<IJobTerminalCallback>();

        var job = CreateTerminalJob();

        await callback.OnTerminalAsync(job, CancellationToken.None);

        artifacts.TryGetValue("job-1182:artifact:1", out var artifact);

        Assert.NotNull(artifact);
        Assert.Equal("content-1182", artifact!.SourceItemId);
        Assert.Equal(3, artifact.SourceVersion);
        Assert.Equal("content-1182:v3", artifact.SourceVersionId);
        Assert.Equal("job-1182", artifact.JobId);
        Assert.Equal(ResultArtifactRetentionState.Retained, artifact.RetentionState);
        Assert.Equal("4326", artifact.Provenance[AnalysisContentMetadataKeys.SourceSrid]);
        Assert.Equal("meters", artifact.Provenance[AnalysisContentMetadataKeys.SourceUnits]);
    }

    private static ExecutionJobRecord CreateTerminalJob()
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = "job-1182",
            Version = 7,
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = now.AddMinutes(-1),
            UpdatedAt = now,
            CompletedAt = now,
            ArtifactReferences = ["s3://bucket/result.geojson"],
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "test",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "test analysis",
                Parameters = new Dictionary<string, string>
                {
                    [AnalysisContentMetadataKeys.ItemId] = "content-1182",
                    [AnalysisContentMetadataKeys.Version] = "3",
                    [AnalysisContentMetadataKeys.VersionId] = "content-1182:v3",
                    [AnalysisContentMetadataKeys.Kind] = "AnalysisPackage",
                    [AnalysisContentMetadataKeys.SourceSrid] = "4326",
                    [AnalysisContentMetadataKeys.SourceUnits] = "meters",
                    [ExecutionJobParameterKeys.GeoprocessingPlanId] = "plan-1182",
                    [ExecutionJobParameterKeys.GeoprocessingOutputArtifactKinds] = "FeatureLayer"
                }
            }
        };
    }

    private sealed class NullProgressStore : IUniversalProgressStore
    {
        public Task SetProgressAsync(
            string operationId,
            IOperationProgress progress,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<TProgress?> GetProgressAsync<TProgress>(
            string operationId,
            CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult<TProgress?>(null);

        public Task<IOperationProgress?> GetProgressAsync(
            string operationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IOperationProgress?>(null);

        public Task DeleteProgressAsync(
            string operationId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(
            OperationType? operationType = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(
            OperationType operationType,
            CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult<IReadOnlyList<TProgress>>(Array.Empty<TProgress>());
    }

    private sealed class EmptyProcessCatalog : IProcessCatalog
    {
        public ProcessDefinition? GetProcess(string processId) => null;

        public IReadOnlyList<ProcessDefinition> ListProcesses() => [];

        public IReadOnlyList<ProcessDefinition> GetProcessesByCategory(string category) => [];
    }

    private sealed class NullResultPackageStore : IGeoprocessingResultPackageStore
    {
        public Task<AnalysisResultPackage?> GetAsync(
            string jobId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AnalysisResultPackage?>(null);

        public Task SetAsync(
            string jobId,
            AnalysisResultPackage package,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingAnalysisContentStore(
        Dictionary<string, ResultArtifactRecord> artifacts) : IAnalysisContentStore
    {
        public Task<AnalysisContentItem> CreateItemAsync(
            AnalysisContentItem item,
            AnalysisContentVersion version,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AnalysisContentItem?> GetItemAsync(
            string itemId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AnalysisContentVersion> AddVersionAsync(
            string itemId,
            AnalysisContentVersion version,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AnalysisContentVersion?> GetVersionAsync(
            string itemId,
            int? version = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AnalysisContentVersion>> ListVersionsAsync(
            string itemId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ResultArtifactRecord> UpsertArtifactAsync(
            ResultArtifactRecord artifact,
            CancellationToken cancellationToken = default)
        {
            artifacts[artifact.ArtifactId] = artifact;
            return Task.FromResult(artifact);
        }

        public Task<ResultArtifactRecord?> GetArtifactAsync(
            string artifactId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(artifacts.GetValueOrDefault(artifactId));

        public Task<IReadOnlyList<ResultArtifactRecord>> ListArtifactsForJobAsync(
            string jobId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ResultArtifactRecord>>(
                artifacts.Values.Where(artifact => artifact.JobId == jobId).ToArray());
    }
}
