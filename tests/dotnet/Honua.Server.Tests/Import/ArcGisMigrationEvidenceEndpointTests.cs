// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for the slice 6 admin endpoints that surface ArcGIS migration evidence
/// (#1025).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class ArcGisMigrationEvidenceEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;
    private readonly InMemoryArcGisMigrationEvidenceStore _store = new();
    private HttpClient _client = null!;

    public ArcGisMigrationEvidenceEndpointTests()
    {
        _fixture = new WebAppFixture()
            .ReplaceService<IArcGisMigrationEvidenceStore>(_store);
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/arcgis/migrations/{runId}/manifest")]
    public async Task RecordManifest_WithValidArtifact_PersistsListAndManifest()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/import/arcgis/migrations/run-write/manifest",
            new
            {
                sourceUrl = "https://example.com/arcgis/rest/services/Write/FeatureServer",
                sourceDisplayName = "Write Source",
                sourceVersion = "11.2",
                createdAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                actor = "honua-migrate",
                manifest = BuildManifest(targetCount: 2)
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var recordPayload = await response.Content.ReadFromJsonAsync<JsonDocument>())
        {
            recordPayload!.RootElement.GetProperty("runId").GetString().Should().Be("run-write");
            recordPayload.RootElement.GetProperty("sourceDisplayName").GetString().Should().Be("Write Source");
        }

        var listResponse = await _client.GetAsync("/api/v1/admin/import/arcgis/migrations?sourceUrl=Write");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var listPayload = await listResponse.Content.ReadFromJsonAsync<JsonDocument>())
        {
            var item = listPayload!.RootElement.GetProperty("items")[0];
            item.GetProperty("runId").GetString().Should().Be("run-write");
            item.GetProperty("targetResourceCount").GetInt32().Should().Be(2);
        }

        var manifestResponse = await _client.GetAsync("/api/v1/admin/import/arcgis/migrations/run-write/manifest");
        manifestResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var manifestPayload = await manifestResponse.Content.ReadFromJsonAsync<JsonDocument>();
        manifestPayload!.RootElement.GetProperty("targetResources").GetArrayLength().Should().Be(2);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/arcgis/migrations/{runId}/parity")]
    public async Task RecordParity_WithoutManifest_ReturnsConflict()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/import/arcgis/migrations/missing-manifest/parity",
            new { parity = BuildParity(ArcGisMigrationParityClassifications.Pass) });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("manifest");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/arcgis/migrations/{runId}/parity")]
    public async Task RecordParity_AfterManifest_PersistsParityAndPromotesListStatus()
    {
        await SeedRun("run-parity-write", "https://example.com/arcgis/Parity");

        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/import/arcgis/migrations/run-parity-write/parity",
            new { parity = BuildParity(ArcGisMigrationParityClassifications.Fail) });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var parityPayload = await response.Content.ReadFromJsonAsync<JsonDocument>())
        {
            parityPayload!.RootElement.GetProperty("classification").GetString().Should().Be("fail");
        }

        var listResponse = await _client.GetAsync("/api/v1/admin/import/arcgis/migrations?status=fail");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var listPayload = await listResponse.Content.ReadFromJsonAsync<JsonDocument>())
        {
            var item = listPayload!.RootElement.GetProperty("items")[0];
            item.GetProperty("runId").GetString().Should().Be("run-parity-write");
            item.GetProperty("hasParity").GetBoolean().Should().BeTrue();
        }

        var parityResponse = await _client.GetAsync("/api/v1/admin/import/arcgis/migrations/run-parity-write/parity");
        parityResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var fetchedParity = await parityResponse.Content.ReadFromJsonAsync<JsonDocument>();
        fetchedParity!.RootElement.GetProperty("classification").GetString().Should().Be("fail");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/arcgis/migrations")]
    public async Task ListMigrations_EmptyStore_ReturnsEmptyPage()
    {
        var response = await _client.GetAsync("/api/v1/admin/import/arcgis/migrations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        payload.Should().NotBeNull();

        var root = payload!.RootElement;
        root.GetProperty("totalCount").GetInt32().Should().Be(0);
        root.GetProperty("items").GetArrayLength().Should().Be(0);
        root.GetProperty("page").GetInt32().Should().Be(0);
        root.GetProperty("pageSize").GetInt32().Should().Be(25);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/arcgis/migrations")]
    public async Task ListMigrations_AfterSeeding_ReturnsSummariesNewestFirst()
    {
        await SeedRun("run-a", "https://example.com/arcgis/Parcels", DateTimeOffset.UtcNow.AddMinutes(-10));
        await SeedRun("run-b", "https://example.com/arcgis/Roads", DateTimeOffset.UtcNow);

        var response = await _client.GetAsync("/api/v1/admin/import/arcgis/migrations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var items = payload!.RootElement.GetProperty("items");

        items.GetArrayLength().Should().Be(2);
        items[0].GetProperty("runId").GetString().Should().Be("run-b");
        items[0].GetProperty("status").GetString().Should().Be("manifest-only");
        items[0].GetProperty("hasParity").GetBoolean().Should().BeFalse();
        items[1].GetProperty("runId").GetString().Should().Be("run-a");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/arcgis/migrations")]
    public async Task ListMigrations_FilterBySourceUrl_FiltersCaseInsensitive()
    {
        await SeedRun("run-a", "https://example.com/arcgis/Parcels");
        await SeedRun("run-b", "https://example.com/arcgis/Roads");

        var response = await _client.GetAsync("/api/v1/admin/import/arcgis/migrations?sourceUrl=parcels");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        payload!.RootElement.GetProperty("totalCount").GetInt32().Should().Be(1);
        payload.RootElement.GetProperty("items")[0].GetProperty("runId").GetString().Should().Be("run-a");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/arcgis/migrations")]
    public async Task ListMigrations_FilterByStatus_PromotesParityClassification()
    {
        await SeedRun("run-a", "https://example.com/a");
        await SeedRun("run-b", "https://example.com/b");
        await _store.SaveParityAsync("run-b", BuildParity(ArcGisMigrationParityClassifications.Warn));

        var response = await _client.GetAsync("/api/v1/admin/import/arcgis/migrations?status=warn");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        payload!.RootElement.GetProperty("totalCount").GetInt32().Should().Be(1);
        var item = payload.RootElement.GetProperty("items")[0];
        item.GetProperty("runId").GetString().Should().Be("run-b");
        item.GetProperty("status").GetString().Should().Be("warn");
        item.GetProperty("hasParity").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/arcgis/migrations")]
    public async Task ListMigrations_UnknownStatus_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/v1/admin/import/arcgis/migrations?status=bogus");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("status");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/arcgis/migrations")]
    public async Task ListMigrations_InvalidPagination_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/v1/admin/import/arcgis/migrations?page=-1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/arcgis/migrations/{runId}/manifest")]
    public async Task GetManifest_KnownRun_ReturnsManifestArtifact()
    {
        await SeedRun("run-a", "https://example.com/arcgis/Roads", targetCount: 3);

        var response = await _client.GetAsync("/api/v1/admin/import/arcgis/migrations/run-a/manifest");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var root = payload!.RootElement;
        root.GetProperty("artifactKind").GetString().Should().Be("honua.migration.manifest");
        root.GetProperty("sourceKind").GetString().Should().Be("arcgis-geoservices-rest");
        root.GetProperty("targetResources").GetArrayLength().Should().Be(3);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/arcgis/migrations/{runId}/manifest")]
    public async Task GetManifest_UnknownRun_Returns404()
    {
        var response = await _client.GetAsync("/api/v1/admin/import/arcgis/migrations/missing/manifest");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/arcgis/migrations/{runId}/parity")]
    public async Task GetParity_KnownRun_ReturnsParityArtifact()
    {
        await SeedRun("run-a", "https://example.com/arcgis/Roads");
        await _store.SaveParityAsync("run-a", BuildParity(ArcGisMigrationParityClassifications.Pass));

        var response = await _client.GetAsync("/api/v1/admin/import/arcgis/migrations/run-a/parity");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var root = payload!.RootElement;
        root.GetProperty("artifactKind").GetString().Should().Be("honua.migration.arcgis-parity");
        root.GetProperty("classification").GetString().Should().Be("pass");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/arcgis/migrations/{runId}/parity")]
    public async Task GetParity_RunHasNoParity_Returns404()
    {
        await SeedRun("run-a", "https://example.com/arcgis/Roads");

        var response = await _client.GetAsync("/api/v1/admin/import/arcgis/migrations/run-a/parity");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("parity");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/arcgis/migrations/{runId}/parity")]
    public async Task GetParity_UnknownRun_Returns404()
    {
        var response = await _client.GetAsync("/api/v1/admin/import/arcgis/migrations/missing/parity");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task SeedRun(
        string runId,
        string sourceUrl,
        DateTimeOffset? createdAt = null,
        int targetCount = 1)
    {
        var record = new ArcGisMigrationRunRecord
        {
            RunId = runId,
            SourceUrl = sourceUrl,
            SourceDisplayName = "ArcGIS Source",
            SourceVersion = "11.2",
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            Actor = "operator@example.com"
        };

        await _store.SaveManifestAsync(record, BuildManifest(targetCount));
    }

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

    private static ArcGisMigrationParityArtifact BuildParity(string classification)
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
