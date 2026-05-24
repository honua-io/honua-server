// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AnalysisContent;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.AnalysisContent;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.AnalysisContent;

[Protocol(TestProtocols.Admin)]
public sealed class GeoprocessingArtifactPersistenceTests
{
    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    public async Task TerminalCallback_WithAnalysisContentMetadata_PersistsDurableArtifactRecord()
    {
        var store = new InMemoryAnalysisContentStore();
        var callback = new GeoprocessingJobTerminalCallback(
            new NullProgressStore(),
            new EmptyProcessCatalog(),
            resultPackageStore: null,
            analysisContentStore: store,
            NullLogger<GeoprocessingJobTerminalCallback>.Instance);

        var job = CreateTerminalJob();

        await callback.OnTerminalAsync(job, CancellationToken.None);

        var artifact = await store.GetArtifactAsync("job-1182:artifact:1");

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
}
