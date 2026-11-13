// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Caching;

public sealed partial class InMemoryCacheServiceTests
{
    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task IsCacheHealthyAsync_CanceledCaller_ThrowsOperationCanceled()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        var probe = () => _cacheService.IsCacheHealthyAsync(canceled.Token);

        await probe.Should().ThrowAsync<OperationCanceledException>();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task IsCacheHealthyAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var cache = new InMemoryCacheService(
            Options.Create(_options),
            NullLogger<InMemoryCacheService>.Instance,
            _performanceMonitor);
        cache.Dispose();
        cache.Dispose();

        var probe = () => cache.IsCacheHealthyAsync();

        await probe.Should().ThrowAsync<ObjectDisposedException>();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetStorageMetricsAsync_ReportsMemoryBackendAndLocalKeyCount()
    {
        await _cacheService.SetAsync("layer:1", new MetadataV2Field { Name = "Layer1", Type = MetadataV2FieldType.String, Length = 10 });
        await _cacheService.SetAsync("layer:2", new MetadataV2Field { Name = "Layer2", Type = MetadataV2FieldType.String, Length = 10 });

        var metrics = await _cacheService.GetStorageMetricsAsync();

        metrics.Backend.Should().Be("memory");
        metrics.IsUsingFallback.Should().BeFalse();
        metrics.LocalKeyCount.Should().Be(2);
        metrics.DistributedKeyCount.Should().BeNull();
        metrics.RedisDatabaseSize.Should().BeNull();
        metrics.RedisUsedMemoryBytes.Should().BeNull();
        metrics.RedisUsedMemoryHuman.Should().BeNull();
    }
}
