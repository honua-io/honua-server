// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AnalysisContent.Abstractions;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Query;
using Honua.Server.Features.AnalysisContent;
using Honua.Server.Features.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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
            Substitute.For<ILayerCatalog>(),
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
