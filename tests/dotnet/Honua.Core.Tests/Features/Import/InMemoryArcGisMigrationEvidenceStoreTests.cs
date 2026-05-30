// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Behavioral contract tests for <see cref="InMemoryArcGisMigrationEvidenceStore"/>. The store
/// is shared with the Postgres-backed implementation through the
/// <see cref="Core.Features.Migration.Abstractions.IArcGisMigrationEvidenceStore"/> seam introduced
/// in #1025 slice 6.
/// </summary>
public sealed class InMemoryArcGisMigrationEvidenceStoreTests
{
    [Fact]
    public async Task SaveManifestAsync_ThenList_ReturnsManifestOnlyStatus()
    {
        var store = new InMemoryArcGisMigrationEvidenceStore();
        var record = NewRecord("run-1", "https://example.com/arcgis/rest/services/Roads/FeatureServer");

        await store.SaveManifestAsync(record, BuildManifest(targetCount: 2));

        var list = await store.ListAsync(new ArcGisMigrationRunFilter());
        list.TotalCount.Should().Be(1);
        list.Items.Should().HaveCount(1);
        list.Items[0].RunId.Should().Be("run-1");
        list.Items[0].Status.Should().Be(ArcGisMigrationRunStatuses.ManifestOnly);
        list.Items[0].HasParity.Should().BeFalse();
        list.Items[0].TargetResourceCount.Should().Be(2);
    }

    [Fact]
    public async Task SaveParityAsync_AfterManifest_PromotesStatusToParityClassification()
    {
        var store = new InMemoryArcGisMigrationEvidenceStore();
        await store.SaveManifestAsync(NewRecord("run-1", "https://example.com/a"), BuildManifest());

        await store.SaveParityAsync("run-1", BuildParity(ArcGisMigrationParityClassifications.Warn));

        var list = await store.ListAsync(new ArcGisMigrationRunFilter());
        list.Items[0].Status.Should().Be(ArcGisMigrationRunStatuses.Warn);
        list.Items[0].HasParity.Should().BeTrue();
    }

    [Fact]
    public async Task SaveParityAsync_WithoutManifest_Throws()
    {
        var store = new InMemoryArcGisMigrationEvidenceStore();

        var act = async () => await store.SaveParityAsync("missing", BuildParity());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*missing*");
    }

    [Fact]
    public async Task ListAsync_FiltersBySourceUrlSubstring_CaseInsensitive()
    {
        var store = new InMemoryArcGisMigrationEvidenceStore();
        await store.SaveManifestAsync(NewRecord("a", "https://example.com/arcgis/Parcels"), BuildManifest());
        await store.SaveManifestAsync(NewRecord("b", "https://example.com/arcgis/Roads"), BuildManifest());

        var result = await store.ListAsync(new ArcGisMigrationRunFilter { SourceUrl = "parcels" });

        result.TotalCount.Should().Be(1);
        result.Items[0].RunId.Should().Be("a");
    }

    [Fact]
    public async Task ListAsync_FiltersByStatus_ManifestOnlyAndPass()
    {
        var store = new InMemoryArcGisMigrationEvidenceStore();
        await store.SaveManifestAsync(NewRecord("a", "https://example.com/a"), BuildManifest());
        await store.SaveManifestAsync(NewRecord("b", "https://example.com/b"), BuildManifest());
        await store.SaveParityAsync("b", BuildParity(ArcGisMigrationParityClassifications.Pass));

        var passOnly = await store.ListAsync(new ArcGisMigrationRunFilter { Status = ArcGisMigrationRunStatuses.Pass });
        passOnly.TotalCount.Should().Be(1);
        passOnly.Items[0].RunId.Should().Be("b");

        var manifestOnly = await store.ListAsync(new ArcGisMigrationRunFilter { Status = ArcGisMigrationRunStatuses.ManifestOnly });
        manifestOnly.TotalCount.Should().Be(1);
        manifestOnly.Items[0].RunId.Should().Be("a");
    }

    [Fact]
    public async Task ListAsync_OrdersNewestFirst_AndPaginates()
    {
        var store = new InMemoryArcGisMigrationEvidenceStore();
        var origin = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 5; i++)
        {
            await store.SaveManifestAsync(
                NewRecord($"run-{i}", $"https://example.com/{i}", origin.AddMinutes(i)),
                BuildManifest());
        }

        var page0 = await store.ListAsync(new ArcGisMigrationRunFilter { Page = 0, PageSize = 2 });
        page0.TotalCount.Should().Be(5);
        page0.Items.Select(i => i.RunId).Should().Equal("run-4", "run-3");

        var page1 = await store.ListAsync(new ArcGisMigrationRunFilter { Page = 1, PageSize = 2 });
        page1.Items.Select(i => i.RunId).Should().Equal("run-2", "run-1");

        var page2 = await store.ListAsync(new ArcGisMigrationRunFilter { Page = 2, PageSize = 2 });
        page2.Items.Select(i => i.RunId).Should().Equal("run-0");
    }

    [Fact]
    public async Task GetManifestAsync_ReturnsPersistedArtifact()
    {
        var store = new InMemoryArcGisMigrationEvidenceStore();
        var manifest = BuildManifest(targetCount: 3);
        await store.SaveManifestAsync(NewRecord("run-1", "https://example.com/x"), manifest);

        var fetched = await store.GetManifestAsync("run-1");

        fetched.Should().NotBeNull();
        fetched!.TargetResources.Should().HaveCount(3);
        fetched.SourceKind.Should().Be("arcgis-geoservices-rest");
    }

    [Fact]
    public async Task GetParityAsync_ReturnsNullWhenAbsent()
    {
        var store = new InMemoryArcGisMigrationEvidenceStore();
        await store.SaveManifestAsync(NewRecord("run-1", "https://example.com/x"), BuildManifest());

        (await store.GetParityAsync("run-1")).Should().BeNull();
        (await store.GetParityAsync("missing")).Should().BeNull();
    }

    [Fact]
    public async Task SaveManifestAsync_Twice_IsIdempotentUpsert()
    {
        var store = new InMemoryArcGisMigrationEvidenceStore();
        await store.SaveManifestAsync(NewRecord("run-1", "https://example.com/v1"), BuildManifest(targetCount: 1));
        await store.SaveManifestAsync(NewRecord("run-1", "https://example.com/v2"), BuildManifest(targetCount: 7));

        var list = await store.ListAsync(new ArcGisMigrationRunFilter());
        list.TotalCount.Should().Be(1);
        list.Items[0].SourceUrl.Should().Be("https://example.com/v2");
        list.Items[0].TargetResourceCount.Should().Be(7);
    }

    private static ArcGisMigrationRunRecord NewRecord(string runId, string sourceUrl, DateTimeOffset? createdAt = null)
        => new()
        {
            RunId = runId,
            SourceUrl = sourceUrl,
            SourceDisplayName = "ArcGIS Source",
            SourceVersion = "11.2",
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            Actor = "operator@example.com"
        };

    private static MigrationManifestArtifact BuildManifest(int targetCount = 1)
    {
        var compatibility = new MigrationCompatibilityAssessment
        {
            Level = "compatible",
            Reason = "Layer can be represented.",
            ManualSteps = []
        };

        var targets = Enumerable.Range(0, targetCount)
            .Select(i => new MigrationManifestTargetResource
            {
                SourceResourceId = $"resource:Roads:layer:{i}",
                SourceKind = "layer",
                Action = "publish",
                TargetResourceId = $"target:resource:roads:layer-{i}",
                TargetServiceName = "roads",
                TargetResourceName = $"layer-{i}",
                Compatibility = compatibility
            })
            .ToArray();

        return new MigrationManifestArtifact
        {
            SourceKind = "arcgis-geoservices-rest",
            Source = new MigrationSourceIdentity
            {
                DisplayName = "ArcGIS Source",
                BaseUrl = "https://example.com/arcgis/rest/services/Roads/FeatureServer",
                Product = "ArcGIS",
                Version = "11.2"
            },
            Summary = new MigrationManifestSummary
            {
                SourceResourceCount = targetCount,
                TargetResourceCount = targetCount
            },
            TargetResources = targets
        };
    }

    private static ArcGisMigrationParityArtifact BuildParity(string classification = "pass")
    {
        return new ArcGisMigrationParityArtifact
        {
            SourceKind = "arcgis-geoservices-rest",
            Source = new MigrationSourceIdentity
            {
                DisplayName = "ArcGIS Source",
                BaseUrl = "https://example.com/arcgis/rest/services/Roads/FeatureServer"
            },
            Classification = classification,
            Reasons = []
        };
    }
}
