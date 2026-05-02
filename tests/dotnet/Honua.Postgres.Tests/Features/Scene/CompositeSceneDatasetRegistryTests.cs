// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Postgres.Features.Scene;

namespace Honua.Postgres.Tests.Features.Scene;

public sealed class CompositeSceneDatasetRegistryTests
{
    private const string SlugFromPostgres = "downtown";
    private const string SlugOnlyInConfig = "config-only";

    [Fact]
    public async Task FindAsync_PrimaryHit_DoesNotConsultFallback()
    {
        var primary = new StubRegistry { LookupResult = BuildDataset(SlugFromPostgres) };
        var ownership = new StubOwnership();
        var fallback = new StubRegistry { LookupResult = BuildDataset(SlugFromPostgres + "-from-fallback") };
        var composite = new CompositeSceneDatasetRegistry(primary, ownership, fallback);

        var result = await composite.FindAsync(SlugFromPostgres);

        result.Should().NotBeNull();
        result!.Id.Should().Be(SlugFromPostgres);
        primary.FindCalls.Should().Be(1);
        ownership.OwnershipLookupCalls.Should().Be(0);
        fallback.FindCalls.Should().Be(0);
    }

    [Fact]
    public async Task FindAsync_PrimaryMiss_AndPrimaryDoesNotOwnSlug_FallsBackToConfig()
    {
        var primary = new StubRegistry { LookupResult = null };
        var ownership = new StubOwnership { OwnershipResult = null };
        var fallback = new StubRegistry { LookupResult = BuildDataset(SlugOnlyInConfig) };
        var composite = new CompositeSceneDatasetRegistry(primary, ownership, fallback);

        var result = await composite.FindAsync(SlugOnlyInConfig);

        result.Should().NotBeNull();
        result!.Id.Should().Be(SlugOnlyInConfig);
        primary.FindCalls.Should().Be(1);
        ownership.OwnershipLookupCalls.Should().Be(1);
        fallback.FindCalls.Should().Be(1);
    }

    [Fact]
    public async Task FindAsync_PrimaryMissBecauseDeactivated_DoesNotResurrectFromConfig()
    {
        // The primary FindAsync filters by status='active', so a deactivated
        // record returns null even though the slug is owned by Postgres. The
        // composite must short-circuit before reaching the configuration
        // fallback so the deactivation stays authoritative.
        var primary = new StubRegistry { LookupResult = null };
        var ownership = new StubOwnership
        {
            OwnershipResult = BuildRecord(SlugFromPostgres, SceneDatasetStatus.Inactive)
        };
        var fallback = new StubRegistry { LookupResult = BuildDataset(SlugFromPostgres) };
        var composite = new CompositeSceneDatasetRegistry(primary, ownership, fallback);

        var result = await composite.FindAsync(SlugFromPostgres);

        result.Should().BeNull();
        primary.FindCalls.Should().Be(1);
        ownership.OwnershipLookupCalls.Should().Be(1);
        fallback.FindCalls.Should().Be(0);
    }

    private static SceneDataset BuildDataset(string id) => new()
    {
        Id = id,
        Name = id,
        AssetRoot = "/var/lib/honua/scenes/" + id,
        TilesetFileName = "tileset.json"
    };

    private static SceneDatasetRecord BuildRecord(string id, SceneDatasetStatus status) => new()
    {
        DatasetId = Guid.NewGuid(),
        Id = id,
        Name = id,
        AssetRoot = "/var/lib/honua/scenes/" + id,
        Status = status,
        CreatedBy = "test"
    };

    private sealed class StubRegistry : ISceneDatasetRegistry
    {
        public SceneDataset? LookupResult { get; set; }

        public int FindCalls { get; private set; }

        public ValueTask<SceneDataset?> FindAsync(string id, CancellationToken cancellationToken = default)
        {
            FindCalls++;
            return ValueTask.FromResult(LookupResult);
        }
    }

    private sealed class StubOwnership : ISceneRegistrationService
    {
        public SceneDatasetRecord? OwnershipResult { get; set; }

        public int OwnershipLookupCalls { get; private set; }

        public Task<SceneDatasetRecord?> GetBySceneIdAsync(string id, CancellationToken cancellationToken = default)
        {
            OwnershipLookupCalls++;
            return Task.FromResult(OwnershipResult);
        }

        public Task<SceneDatasetRecord> RegisterAsync(SceneDatasetRecord record, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SceneDatasetRecord?> GetAsync(Guid datasetId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SceneDatasetRecord>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SceneDatasetRecord> UpdateAsync(SceneDatasetRecord record, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeactivateAsync(Guid datasetId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
