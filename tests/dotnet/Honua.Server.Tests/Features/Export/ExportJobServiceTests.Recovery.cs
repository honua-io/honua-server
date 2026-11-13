// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.


using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;
using Honua.Io.Export;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Export;

public sealed partial class ExportJobServiceTests
{
    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ReadQueuedJobIdsAsync_InvalidProgress_DoesNotPreventHealthyJobRecovery()
    {
        var progressStore = Substitute.For<IUniversalProgressStore>();
        var requestCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var channel = Channel.CreateUnbounded<string>();
        using var services = new ServiceCollection().BuildServiceProvider();
        var sut = new ExportJobService(progressStore, requestCache, channel,
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ExportJobService>.Instance);
        var job = CreateJob("healthy-export");
        progressStore.GetActiveOperationIdsAsync(OperationType.Export, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["invalid-export", job.JobId]));
        progressStore.GetProgressAsync<ExportProgress>("invalid-export", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ExportProgress?>(new InvalidPersistedExportProgressException()));
        progressStore.GetProgressAsync<ExportProgress>(job.JobId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ExportProgress?>(ExportProgress.CreateInitial(
                job.JobId, job.Format, job.ServiceName, job.ServiceLayerIndex, job.TotalFeatures)));
        await requestCache.SetStringAsync($"export:request:{job.JobId}", JsonSerializer.Serialize(
            new ExportJobService.PersistedExportJobRequest { Job = job },
            ExportJsonContext.Default.PersistedExportJobRequest));

        await using var enumerator = sut.ReadQueuedJobIdsAsync().GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.Should().Be(job.JobId);
        (await requestCache.GetStringAsync($"export:request:{job.JobId}")).Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ReadQueuedJobIdsAsync_LegacyRequest_MarksOnlyThatJobFailed()
    {
        var progressStore = new InMemoryUniversalProgressStore();
        var requestCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var channel = Channel.CreateUnbounded<string>();
        channel.Writer.Complete();
        using var services = new ServiceCollection().BuildServiceProvider();
        var sut = new ExportJobService(progressStore, requestCache, channel,
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ExportJobService>.Instance);
        var job = CreateJob("legacy-request");
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(
            new ExportJobService.PersistedExportJobRequest { Job = job },
            ExportJsonContext.Default.PersistedExportJobRequest))!;
        var persistedJob = json["job"]!.AsObject();
        persistedJob.Remove("storageLayerId").Should().BeTrue();
        persistedJob.Remove("serviceLayerIndex").Should().BeTrue();
        persistedJob["layerId"] = job.ServiceLayerIndex;
        await requestCache.SetStringAsync($"export:request:{job.JobId}", json.ToJsonString());
        await progressStore.SetProgressAsync(job.JobId, ExportProgress.CreateInitial(
            job.JobId, job.Format, job.ServiceName, job.ServiceLayerIndex, job.TotalFeatures));

        await using var enumerator = sut.ReadQueuedJobIdsAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).Should().BeFalse();

        var failed = await progressStore.GetProgressAsync<ExportProgress>(job.JobId);
        failed!.Status.Should().Be(OperationStatus.Failed);
        failed.ErrorMessage.Should().Be("Export request metadata is no longer available.");
        (await requestCache.GetStringAsync($"export:request:{job.JobId}")).Should().BeNull();
    }
}
