// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Exceptions;
using Honua.Core.Features.AnalysisContent.Abstractions;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Query;
using Honua.Server.Features.AnalysisContent;
using Honua.Server.Features.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.AnalysisContent;

[Protocol(TestProtocols.Admin)]
public sealed class AnalysisContentServiceTests
{
    [UnitTest]
    [Operation(Operations.Create)]
    public async Task AddVersionAsync_WhenStoreConflicts_RereadsLatestAndCreatesNextVersion()
    {
        var store = new ConflictOnceAnalysisContentStore();
        var sut = new AnalysisContentService(
            store,
            Substitute.For<IMetadataV2GraphProvider>(),
            Substitute.For<IQueryProcessor>(),
            Substitute.For<IFeatureReader>(),
            Substitute.For<IGeoprocessingJobService>(),
            Array.Empty<IExecutionLogStore>(),
            TimeProvider.System,
            NullLogger<AnalysisContentService>.Instance);

        var result = await sut.AddVersionAsync(
            ConflictOnceAnalysisContentStore.ItemId,
            new CreateAnalysisContentVersionCommand(
                SavedQuery: CreateSavedQuery("after conflict"),
                AnalysisPackage: null,
                BasedOnVersionId: null,
                CreatedFromJobId: null,
                CreatedFromArtifactIds: []),
            new ClaimsPrincipal(new ClaimsIdentity("Test")),
            CancellationToken.None);

        Assert.Equal(2, store.AddVersionAttempts);
        Assert.Equal(3, result.Version.Version);
        Assert.Equal("analysis-content-conflict:v2", result.Version.BasedOnVersionId);
        Assert.Equal(3, result.Item.CurrentVersion);
    }

    [UnitTest]
    [Operation(Operations.JobStatus)]
    public async Task GetJobFailureAsync_WithSecretBearingErrorMessage_RedactsMessage()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.GetJobAsync("job-secret", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(CreateFailedJob(
                "job-secret",
                "Auth refused: client_secret=topsecret123 rejected by the token endpoint"));
        var sut = CreateService(jobService: jobService);

        var failure = await sut.GetJobFailureAsync("job-secret", Principal(), CancellationToken.None);

        Assert.True(failure.IsTerminal);
        Assert.DoesNotContain("topsecret123", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("client_secret", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    [Operation(Operations.JobStatus)]
    public async Task GetJobLogsAsync_WithSecretBearingMetadataValue_RedactsValue()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.GetJobAsync("job-logs", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(CreateFailedJob("job-logs", "validation failed"));
        var logStore = Substitute.For<IExecutionLogStore>();
        logStore.TailAsync("job-logs", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ExecutionLogTail
            {
                Items =
                [
                    new ExecutionLogEntry
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        Level = ExecutionLogLevel.Error,
                        Message = "token exchange failed",
                        // Secret-bearing value under a non-sensitive key.
                        Metadata = new Dictionary<string, string> { ["detail"] = "api_key=abc123xyz" }
                    }
                ],
                TotalCount = 1
            });
        var sut = CreateService(jobService: jobService, logStores: [logStore]);

        var logs = await sut.GetJobLogsAsync("job-logs", 50, Principal(), CancellationToken.None);

        var entry = Assert.Single(logs.Entries);
        Assert.DoesNotContain("token exchange failed", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(entry.Metadata);
        Assert.DoesNotContain("abc123xyz", entry.Metadata!["detail"], StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    [Operation(Operations.JobStatus)]
    public async Task GetJobLogsAsync_WhenJobNotFound_PropagatesNotFoundWithoutReadingLogs()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.GetJobAsync("missing", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Throws(new GeoprocessingNotFoundException("Job 'missing' not found."));
        var logStore = Substitute.For<IExecutionLogStore>();
        var sut = CreateService(jobService: jobService, logStores: [logStore]);

        await Assert.ThrowsAsync<GeoprocessingNotFoundException>(
            () => sut.GetJobLogsAsync("missing", null, Principal(), CancellationToken.None));

        await logStore.DidNotReceive()
            .TailAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.JobStatus)]
    public async Task GetJobLogsAsync_WhenLogStoreUnavailable_ThrowsStoreUnavailable()
    {
        // A Redis-backed log store outage must surface as the documented retryable 503
        // (AnalysisContentStoreUnavailableException), not a generic 500.
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.GetJobAsync("job-logs", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(CreateFailedJob("job-logs", "validation failed"));
        var logStore = Substitute.For<IExecutionLogStore>();
        logStore.TailAsync("job-logs", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new RedisConnectionException(ConnectionFailureType.SocketFailure, "redis unavailable"));
        var sut = CreateService(jobService: jobService, logStores: [logStore]);

        await Assert.ThrowsAsync<AnalysisContentStoreUnavailableException>(
            () => sut.GetJobLogsAsync("job-logs", 50, Principal(), CancellationToken.None));
    }

    [UnitTest]
    [Operation(Operations.JobStatus)]
    public async Task GetJobLogsAsync_WhenJobStoreUnavailable_ThrowsStoreUnavailable()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.GetJobAsync("job-logs", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Throws(new ServiceUnavailableException("job store unavailable"));
        var logStore = Substitute.For<IExecutionLogStore>();
        var sut = CreateService(jobService: jobService, logStores: [logStore]);

        await Assert.ThrowsAsync<AnalysisContentStoreUnavailableException>(
            () => sut.GetJobLogsAsync("job-logs", 50, Principal(), CancellationToken.None));

        // The job resolve failed, so the log store must not be read.
        await logStore.DidNotReceive()
            .TailAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.JobStatus)]
    public async Task GetJobFailureAsync_WhenJobStoreUnavailable_ThrowsStoreUnavailable()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.GetJobAsync("job-failure", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Throws(new RedisConnectionException(ConnectionFailureType.SocketFailure, "redis unavailable"));
        var sut = CreateService(jobService: jobService);

        await Assert.ThrowsAsync<AnalysisContentStoreUnavailableException>(
            () => sut.GetJobFailureAsync("job-failure", Principal(), CancellationToken.None));
    }

    [UnitTest]
    [Operation(Operations.JobStatus)]
    public async Task GetJobLogsAsync_WithSecretBearingMetadataKey_DropsEntireEntry()
    {
        // The value is opaque (no secret marker), so value-only inspection cannot catch it; the
        // sensitive key name must drop the entry while non-sensitive keys are preserved.
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.GetJobAsync("job-logs", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(CreateFailedJob("job-logs", "validation failed"));
        var logStore = Substitute.For<IExecutionLogStore>();
        logStore.TailAsync("job-logs", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ExecutionLogTail
            {
                Items =
                [
                    new ExecutionLogEntry
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        Level = ExecutionLogLevel.Error,
                        Message = "request rejected",
                        Metadata = new Dictionary<string, string>
                        {
                            ["token"] = "abc123xyz",
                            ["apiKey"] = "opaque-value",
                            ["region"] = "us-west-2"
                        }
                    }
                ],
                TotalCount = 1
            });
        var sut = CreateService(jobService: jobService, logStores: [logStore]);

        var logs = await sut.GetJobLogsAsync("job-logs", 50, Principal(), CancellationToken.None);

        var entry = Assert.Single(logs.Entries);
        Assert.NotNull(entry.Metadata);
        Assert.False(entry.Metadata!.ContainsKey("token"));
        Assert.False(entry.Metadata.ContainsKey("apiKey"));
        Assert.DoesNotContain(
            "abc123xyz",
            string.Join("|", entry.Metadata.Values),
            StringComparison.OrdinalIgnoreCase);
        // Non-sensitive metadata still passes through.
        Assert.Equal("us-west-2", entry.Metadata["region"]);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task AddAnalysisContent_InMemoryFallbackStore_PersistsAcrossRequestScopes()
    {
        // The in-memory fallback must be a process-wide singleton: an item written in one request
        // scope has to remain visible from a later scope. A scoped-per-instance registration would
        // resolve a fresh empty store and silently lose the item.
        var services = new ServiceCollection();
        services.AddAnalysisContent(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();

        const string itemId = "analysis-content-scope-test";
        var item = new AnalysisContentItem
        {
            ItemId = itemId,
            Kind = AnalysisContentKind.SavedQuery,
            Name = "scope-test",
            CurrentVersion = 1,
            CurrentVersionId = $"{itemId}:v1",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var version = new AnalysisContentVersion
        {
            VersionId = $"{itemId}:v1",
            ItemId = itemId,
            Version = 1,
            Kind = AnalysisContentKind.SavedQuery,
            SavedQuery = CreateSavedQuery("scope-test"),
            ContentHash = "hash-1",
            CreatedAt = DateTimeOffset.UtcNow
        };

        await using (var writeScope = provider.CreateAsyncScope())
        {
            var store = writeScope.ServiceProvider.GetRequiredService<IAnalysisContentStore>();
            await store.CreateItemAsync(item, version, CancellationToken.None);
        }

        await using (var readScope = provider.CreateAsyncScope())
        {
            var store = readScope.ServiceProvider.GetRequiredService<IAnalysisContentStore>();
            var resolved = await store.GetItemAsync(itemId, CancellationToken.None);
            Assert.NotNull(resolved);
            Assert.Equal(itemId, resolved!.ItemId);
        }
    }

    private static AnalysisContentService CreateService(
        IGeoprocessingJobService? jobService = null,
        IEnumerable<IExecutionLogStore>? logStores = null)
        => new(
            Substitute.For<IAnalysisContentStore>(),
            Substitute.For<IMetadataV2GraphProvider>(),
            Substitute.For<IQueryProcessor>(),
            Substitute.For<IFeatureReader>(),
            jobService ?? Substitute.For<IGeoprocessingJobService>(),
            logStores ?? Array.Empty<IExecutionLogStore>(),
            TimeProvider.System,
            NullLogger<AnalysisContentService>.Instance);

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity("Test"));

    private static ExecutionJobRecord CreateFailedJob(string jobId, string errorMessage)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Failed,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now,
            ErrorMessage = errorMessage,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "test",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "test",
                Parameters = new Dictionary<string, string>()
            }
        };
    }

    private static SavedQueryContent CreateSavedQuery(string query)
        => new()
        {
            LayerId = 0,
            NaturalLanguageQuery = query
        };

    private sealed class ConflictOnceAnalysisContentStore : IAnalysisContentStore
    {
        public const string ItemId = "analysis-content-conflict";

        private readonly SortedDictionary<int, AnalysisContentVersion> _versions = new()
        {
            [1] = CreateVersion(1, "initial")
        };

        private AnalysisContentItem _item = new()
        {
            ItemId = ItemId,
            Kind = AnalysisContentKind.SavedQuery,
            Name = "conflict-test",
            CurrentVersion = 1,
            CurrentVersionId = "analysis-content-conflict:v1",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        private bool _conflictPending = true;

        public int AddVersionAttempts { get; private set; }

        public Task<AnalysisContentItem> CreateItemAsync(
            AnalysisContentItem item,
            AnalysisContentVersion version,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AnalysisContentItem?> GetItemAsync(
            string itemId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AnalysisContentItem?>(string.Equals(itemId, ItemId, StringComparison.Ordinal) ? _item : null);

        public Task<AnalysisContentVersion> AddVersionAsync(
            string itemId,
            AnalysisContentVersion version,
            CancellationToken cancellationToken = default)
        {
            AddVersionAttempts++;
            if (_conflictPending)
            {
                _conflictPending = false;
                StoreVersion(CreateVersion(2, "external"));
                throw new AnalysisContentStoreConflictException("version conflict");
            }

            StoreVersion(version);
            return Task.FromResult(version);
        }

        public Task<AnalysisContentVersion?> GetVersionAsync(
            string itemId,
            int? version = null,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(itemId, ItemId, StringComparison.Ordinal))
            {
                return Task.FromResult<AnalysisContentVersion?>(null);
            }

            var resolved = version.HasValue
                ? _versions.GetValueOrDefault(version.Value)
                : _versions.Last().Value;
            return Task.FromResult(resolved);
        }

        public Task<IReadOnlyList<AnalysisContentVersion>> ListVersionsAsync(
            string itemId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AnalysisContentVersion>>(_versions.Values.ToArray());

        public Task<ResultArtifactRecord> UpsertArtifactAsync(
            ResultArtifactRecord artifact,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ResultArtifactRecord?> GetArtifactAsync(
            string artifactId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ResultArtifactRecord>> ListArtifactsForJobAsync(
            string jobId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private void StoreVersion(AnalysisContentVersion version)
        {
            _versions[version.Version] = version;
            _item = _item with
            {
                CurrentVersion = version.Version,
                CurrentVersionId = version.VersionId,
                UpdatedAt = version.CreatedAt
            };
        }

        private static AnalysisContentVersion CreateVersion(int version, string query)
            => new()
            {
                VersionId = $"analysis-content-conflict:v{version}",
                ItemId = ItemId,
                Version = version,
                Kind = AnalysisContentKind.SavedQuery,
                SavedQuery = CreateSavedQuery(query),
                ContentHash = $"hash-{version}",
                CreatedAt = DateTimeOffset.UtcNow
            };
    }
}
