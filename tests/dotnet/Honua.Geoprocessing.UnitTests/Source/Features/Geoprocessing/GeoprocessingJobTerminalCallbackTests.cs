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
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class GeoprocessingJobTerminalCallbackTests
{
    [UnitTest]
    public async Task OnTerminalAsync_ArtifactPersistFailure_MarksDurableJobFailed()
    {
        var now = DateTimeOffset.UtcNow;
        var job = new ExecutionJobRecord
        {
            OperationId = "job-artifact-fail",
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = now.AddMinutes(-1),
            UpdatedAt = now,
            CompletedAt = now,
            ArtifactReferences = ["s3://bucket/result.geojson"],
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "managed",
                WorkloadName = "analysis",
                Parameters = new Dictionary<string, string>
                {
                    [AnalysisContentMetadataKeys.ItemId] = "content-1",
                    [AnalysisContentMetadataKeys.Version] = "2",
                    [AnalysisContentMetadataKeys.VersionId] = "content-1:v2"
                }
            }
        };

        ExecutionJobRecord saved = job;
        var jobs = Substitute.For<IExecutionJobStore>();
        jobs.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(_ => saved);
        jobs.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                saved = call.Arg<ExecutionJobRecord>();
                return true;
            });

        var artifacts = Substitute.For<IAnalysisContentStore>();
        artifacts.UpsertArtifactAsync(Arg.Any<ResultArtifactRecord>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("store down"));
        var services = new ServiceCollection();
        services.AddSingleton(artifacts);
        using var provider = services.BuildServiceProvider();

        var progress = Substitute.For<IUniversalProgressStore>();
        progress.GetProgressAsync<GeoprocessingProgress>(job.OperationId, Arg.Any<CancellationToken>())
            .Returns((GeoprocessingProgress?)null);
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());

        var callback = new GeoprocessingJobTerminalCallback(
            progress,
            Substitute.For<IProcessCatalog>(),
            options,
            resultPackageStore: null,
            serviceScopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
            logger: NullLogger<GeoprocessingJobTerminalCallback>.Instance,
            executionJobStore: jobs);

        await callback.OnTerminalAsync(job, CancellationToken.None);

        Assert.Equal(ExecutionJobStatus.Failed, saved.Status);
        Assert.Contains("incomplete", saved.ErrorMessage!);
    }
}
