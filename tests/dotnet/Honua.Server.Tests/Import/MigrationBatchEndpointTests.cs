// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for the footprint-driven batch import orchestration
/// endpoints (issue #1253): <c>POST /api/v1/admin/import/migrations</c> and
/// <c>GET /api/v1/admin/import/migrations/{batchId}</c>. Uses an in-memory
/// catalog plus a recording orchestrator so the endpoints exercise routing,
/// validation, serialization, and status-code policy without the distributed
/// job pipeline.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class MigrationBatchEndpointTests : IAsyncLifetime
{
    private readonly InMemoryMigrationBatchRunCatalog _catalog = new();
    private readonly RecordingMigrationBatchOrchestrator _orchestrator;
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public MigrationBatchEndpointTests()
    {
        _orchestrator = new RecordingMigrationBatchOrchestrator(_catalog);
        _fixture = new WebAppFixture()
            .ReplaceService<IMigrationBatchRunCatalog>(_catalog)
            .ReplaceService<IMigrationBatchOrchestrator>(_orchestrator);
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/migrations")]
    public async Task StartBatch_WithFootprint_ReturnsAcceptedWithBatchId()
    {
        var body = new
        {
            sourceKind = "arcgis-geoservices-rest",
            sourceUrl = "https://example.com/arcgis/rest/services/Inspections/FeatureServer",
            layers = new[]
            {
                new { sourceResourceId = "resource:Inspections:layer:0", serviceUrl = "https://example.com/arcgis/rest/services/Inspections/FeatureServer", layerId = 0, tableName = "inspections" },
                new { sourceResourceId = "resource:Inspections:layer:1", serviceUrl = "https://example.com/arcgis/rest/services/Inspections/FeatureServer", layerId = 1, tableName = "inspection_photos" }
            }
        };

        var response = await _client.PostAsync(
            "/api/v1/admin/import/migrations",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("batchId").GetString().Should().NotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("status").GetString().Should().Be("running");
        doc.RootElement.GetProperty("totalChildren").GetInt32().Should().Be(2);
        _orchestrator.LastRequest.Should().NotBeNull();
        _orchestrator.LastRequest!.Layers.Should().HaveCount(2);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/migrations")]
    public async Task StartBatch_WithEmptyFootprint_ReturnsBadRequest()
    {
        var body = new { sourceKind = "arcgis-geoservices-rest", layers = Array.Empty<object>() };

        var response = await _client.PostAsync(
            "/api/v1/admin/import/migrations",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/migrations")]
    public async Task StartBatch_WithInvalidTableName_ReturnsBadRequest()
    {
        var body = new
        {
            sourceKind = "arcgis-geoservices-rest",
            layers = new[]
            {
                new { sourceResourceId = "resource:x:layer:0", serviceUrl = "https://example.com/arcgis/rest/services/X/FeatureServer", layerId = 0, tableName = "bad name;drop" }
            }
        };

        var response = await _client.PostAsync(
            "/api/v1/admin/import/migrations",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/migrations/{batchId}")]
    public async Task GetBatch_WhenUnknown_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/v1/admin/import/migrations/does-not-exist");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/migrations/{batchId}")]
    public async Task GetBatch_WithChildren_ReturnsRolledUpStatus()
    {
        var now = DateTimeOffset.UtcNow;
        _catalog.Seed(
            new MigrationBatchRunRecord
            {
                BatchId = "batch-001",
                SourceKind = "arcgis-geoservices-rest",
                Status = MigrationBatchRunStatus.Running,
                StartedAt = now,
                TotalChildren = 2,
                SucceededChildren = 1
            },
            new MigrationBatchChildRecord
            {
                BatchId = "batch-001",
                Ordinal = 0,
                SourceResourceId = "resource:x:layer:0",
                ServiceUrl = "https://example.com/arcgis/rest/services/X/FeatureServer",
                SourceLayerId = 0,
                TableName = "origin",
                Status = MigrationBatchChildStatus.Succeeded,
                PublishedLayerId = 42,
                UpdatedAt = now
            },
            new MigrationBatchChildRecord
            {
                BatchId = "batch-001",
                Ordinal = 1,
                SourceResourceId = "resource:x:layer:1",
                ServiceUrl = "https://example.com/arcgis/rest/services/X/FeatureServer",
                SourceLayerId = 1,
                TableName = "related",
                DependsOn = ["resource:x:layer:0"],
                Status = MigrationBatchChildStatus.Running,
                UpdatedAt = now
            });

        var response = await _client.GetAsync("/api/v1/admin/import/migrations/batch-001");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("batchId").GetString().Should().Be("batch-001");
        doc.RootElement.GetProperty("succeededChildren").GetInt32().Should().Be(1);
        var children = doc.RootElement.GetProperty("children");
        children.GetArrayLength().Should().Be(2);
        children[0].GetProperty("status").GetString().Should().Be("succeeded");
        children[0].GetProperty("publishedLayerId").GetInt32().Should().Be(42);
        children[1].GetProperty("dependsOn")[0].GetString().Should().Be("resource:x:layer:0");
    }

    private sealed class RecordingMigrationBatchOrchestrator : IMigrationBatchOrchestrator
    {
        private readonly InMemoryMigrationBatchRunCatalog _catalog;

        public RecordingMigrationBatchOrchestrator(InMemoryMigrationBatchRunCatalog catalog) => _catalog = catalog;

        public MigrationBatchStartRequest? LastRequest { get; private set; }

        public Task<MigrationBatchRunRecord> StartAsync(MigrationBatchStartRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            var batchId = Guid.NewGuid().ToString("N")[..16];
            var record = new MigrationBatchRunRecord
            {
                BatchId = batchId,
                SourceKind = request.SourceKind,
                SourceUrl = request.SourceUrl,
                SourceDisplayName = request.SourceDisplayName,
                Status = MigrationBatchRunStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                TotalChildren = request.Layers.Count,
                ApplyRelationships = request.ApplyRelationships && !string.IsNullOrWhiteSpace(request.ManifestBody)
            };
            _catalog.Seed(record);
            return Task.FromResult(record);
        }

        public Task<MigrationBatchRunRecord?> AdvanceAsync(string batchId, CancellationToken cancellationToken = default)
            => _catalog.GetAsync(batchId, cancellationToken);
    }

    private sealed class InMemoryMigrationBatchRunCatalog : IMigrationBatchRunCatalog
    {
        private readonly ConcurrentDictionary<string, MigrationBatchRunRecord> _batches = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, List<MigrationBatchChildRecord>> _children = new(StringComparer.Ordinal);

        public void Seed(MigrationBatchRunRecord record, params MigrationBatchChildRecord[] children)
        {
            _batches[record.BatchId] = record;
            if (children.Length > 0)
            {
                _children[record.BatchId] = children.OrderBy(c => c.Ordinal).ToList();
            }
        }

        public Task<MigrationBatchRunRecord> CreateAsync(
            MigrationBatchRunRecord record,
            string? manifestBody,
            IReadOnlyList<MigrationBatchChildRecord> children,
            CancellationToken cancellationToken = default)
        {
            _batches[record.BatchId] = record;
            _children[record.BatchId] = children.OrderBy(c => c.Ordinal).ToList();
            return Task.FromResult(record);
        }

        public Task<MigrationBatchRunRecord?> GetAsync(string batchId, CancellationToken cancellationToken = default)
            => Task.FromResult(_batches.TryGetValue(batchId, out var record) ? record : null);

        public Task<IReadOnlyList<MigrationBatchChildRecord>> GetChildrenAsync(string batchId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MigrationBatchChildRecord>>(
                _children.TryGetValue(batchId, out var children) ? children : []);

        public Task<string?> GetManifestBodyAsync(string batchId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<MigrationBatchChildRecord?> UpdateChildAsync(
            string batchId,
            int ordinal,
            MigrationBatchChildStatus status,
            string? jobId,
            int? publishedLayerId,
            string? statusNote,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            if (!_children.TryGetValue(batchId, out var children))
            {
                return Task.FromResult<MigrationBatchChildRecord?>(null);
            }

            var index = children.FindIndex(c => c.Ordinal == ordinal);
            if (index < 0)
            {
                return Task.FromResult<MigrationBatchChildRecord?>(null);
            }

            var updated = children[index] with
            {
                Status = status,
                JobId = jobId ?? children[index].JobId,
                PublishedLayerId = publishedLayerId ?? children[index].PublishedLayerId,
                StatusNote = statusNote ?? children[index].StatusNote,
                UpdatedAt = updatedAt
            };
            children[index] = updated;
            return Task.FromResult<MigrationBatchChildRecord?>(updated);
        }

        public Task<MigrationBatchRunRecord?> UpdateBatchAsync(
            string batchId,
            MigrationBatchRunStatus status,
            int succeededChildren,
            int failedChildren,
            int cancelledChildren,
            DateTimeOffset? completedAt,
            bool? relationshipsApplied,
            string? statusNote,
            CancellationToken cancellationToken = default)
        {
            if (!_batches.TryGetValue(batchId, out var record))
            {
                return Task.FromResult<MigrationBatchRunRecord?>(null);
            }

            var updated = record with
            {
                Status = status,
                SucceededChildren = succeededChildren,
                FailedChildren = failedChildren,
                CancelledChildren = cancelledChildren,
                CompletedAt = completedAt ?? record.CompletedAt,
                RelationshipsApplied = relationshipsApplied ?? record.RelationshipsApplied,
                StatusNote = statusNote ?? record.StatusNote
            };
            _batches[batchId] = updated;
            return Task.FromResult<MigrationBatchRunRecord?>(updated);
        }

        public Task<IReadOnlyList<string>> GetActiveBatchIdsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(
                _batches.Values
                    .Where(b => b.Status == MigrationBatchRunStatus.Running)
                    .Select(b => b.BatchId)
                    .ToList());
    }
}
