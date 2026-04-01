// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.Migration.Domain;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Import;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Focused tests for migration-evidence distributed coordination behavior.
/// </summary>
public sealed class MigrationEvidenceCoordinationTests
{
    [Fact]
    public async Task RequestAsync_RunningJobWithoutLocalToken_PersistsDistributedCancellationRequest()
    {
        using var manager = CreateJobManager(
            new TestHostEnvironment("Test"),
            distributedCache: null,
            redis: null,
            out _);

        const string jobId = "job-123";
        var request = CreateRequest();
        await manager.RequestStore.SetProgressAsync(
            jobId,
            new MigrationEvidenceJobState
            {
                Request = request
            },
            TimeSpan.FromHours(24),
            CancellationToken.None);

        var progress = MigrationEvidenceProgress.CreateInitial(jobId, request) with
        {
            Status = MigrationEvidenceJobStatus.ResolvingSourceBaseline,
            CurrentPhase = "Resolving source baseline"
        };

        var decision = await MigrationEvidenceCancellationCoordinator.RequestAsync(
            jobId,
            progress,
            manager,
            new MigrationEvidenceCancellationTokens(),
            CancellationToken.None);

        decision.Success.Should().BeTrue();

        var updatedState = await manager.RequestStore.GetProgressAsync(jobId, CancellationToken.None);
        updatedState.Should().NotBeNull();
        updatedState!.CancellationRequested.Should().BeTrue();
        updatedState.CancellationRequestedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CanAcceptNewJobs_RequestStoreFallsBack_TracksLiveDurabilityState()
    {
        var distributedCache = new ToggleDistributedCache();
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        using var manager = CreateJobManager(
            new TestHostEnvironment("Production"),
            distributedCache,
            redis,
            out var universalProgressStore);

        manager.IsClusterDurable.Should().BeTrue();
        manager.CanAcceptNewJobs.Should().BeTrue();

        distributedCache.ThrowOnWrite = true;

        await manager.RequestStore.SetProgressAsync(
            "job-456",
            new MigrationEvidenceJobState
            {
                Request = CreateRequest()
            },
            TimeSpan.FromHours(24),
            CancellationToken.None);

        manager.IsClusterDurable.Should().BeFalse();
        manager.CanAcceptNewJobs.Should().BeFalse();
        universalProgressStore.IsUsingFallback.Should().BeFalse();
    }

    private static MigrationEvidenceJobManager CreateJobManager(
        IHostEnvironment hostEnvironment,
        IDistributedCache? distributedCache,
        IConnectionMultiplexer? redis,
        out UniversalProgressStore universalProgressStore)
    {
        universalProgressStore = new UniversalProgressStore(
            distributedCache,
            NullLogger<UniversalProgressStore>.Instance,
            redis);

        return new MigrationEvidenceJobManager(
            universalProgressStore,
            distributedCache,
            NullLogger<MigrationEvidenceJobManager>.Instance,
            hostEnvironment,
            redis);
    }

    private static MigrationEvidenceRequest CreateRequest() => new()
    {
        Provider = MigrationEvidenceProvider.ArcGisGeoservices,
        SourceServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
        TargetBaseUrl = "https://example.com",
        TargetServiceName = "test",
        Layers =
        [
            new MigrationEvidenceLayerMapping
            {
                SourceLayerId = 0,
                TargetLayerId = 0
            }
        ],
        CutoverProfile = MigrationCutoverProfile.Pilot,
        RollbackPlanReference = "runbook://rollback/pilot"
    };

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Honua.Server.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ToggleDistributedCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _entries = new(StringComparer.Ordinal);

        public bool ThrowOnRead { get; set; }

        public bool ThrowOnWrite { get; set; }

        public byte[]? Get(string key)
        {
            ThrowIfReadFailed();
            return _entries.TryGetValue(key, out var value) ? value : null;
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            ThrowIfReadFailed();
            return Task.FromResult(Get(key));
        }

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default)
            => Task.CompletedTask;

        public void Remove(string key)
        {
            _entries.TryRemove(key, out _);
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            ThrowIfWriteFailed();
            _entries[key] = value;
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            ThrowIfWriteFailed();
            Set(key, value, options);
            return Task.CompletedTask;
        }

        private void ThrowIfReadFailed()
        {
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("Simulated cache read failure.");
            }
        }

        private void ThrowIfWriteFailed()
        {
            if (ThrowOnWrite)
            {
                throw new InvalidOperationException("Simulated cache write failure.");
            }
        }
    }
}
