// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
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
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for the slice-5 admin orchestration endpoints
/// (<c>/api/v1/admin/migration/runs</c>). Uses an in-memory fake catalog so
/// the endpoints exercise routing, serialization, and status-code policy
/// without requiring the slice-4 evidence pack pipeline.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class MigrationRunAdminEndpointTests : IAsyncLifetime
{
    private readonly InMemoryMigrationRunCatalog _catalog = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public MigrationRunAdminEndpointTests()
    {
        _fixture = new WebAppFixture()
            .ReplaceService<IMigrationRunCatalog>(_catalog);
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/migration/runs")]
    public async Task List_WhenCatalogIsEmpty_ReturnsZeroTotal()
    {
        var response = await _client.GetAsync("/api/v1/admin/migration/runs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(payload);
        doc.RootElement.GetProperty("totalCount").GetInt64().Should().Be(0);
        doc.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/migration/runs")]
    public async Task List_ReturnsRecordsMostRecentFirst()
    {
        _catalog.Seed(new MigrationRunRecord
        {
            RunId = "run-001",
            SourceKind = "geoserver-rest",
            Status = MigrationRunStatus.Running,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        });
        _catalog.Seed(new MigrationRunRecord
        {
            RunId = "run-002",
            SourceKind = "geoserver-rest",
            Status = MigrationRunStatus.Succeeded,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
            EvidencePackFingerprint = "fp-001"
        });

        var response = await _client.GetAsync("/api/v1/admin/migration/runs");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("totalCount").GetInt64().Should().Be(2);
        var items = doc.RootElement.GetProperty("items");
        items[0].GetProperty("runId").GetString().Should().Be("run-002");
        items[0].GetProperty("hasEvidencePack").GetBoolean().Should().BeTrue();
        items[1].GetProperty("runId").GetString().Should().Be("run-001");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/migration/runs")]
    public async Task List_WithInvalidLimit_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/v1/admin/migration/runs?limit=abc");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("limit");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/migration/runs/{runId}")]
    public async Task Get_WithUnknownRunId_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/v1/admin/migration/runs/does-not-exist");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/migration/runs/{runId}")]
    public async Task Get_WithKnownRunId_ReturnsRecord()
    {
        _catalog.Seed(new MigrationRunRecord
        {
            RunId = "run-known",
            SourceKind = "geoserver-rest",
            Status = MigrationRunStatus.Running,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });

        var response = await _client.GetAsync("/api/v1/admin/migration/runs/run-known");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("runId").GetString().Should().Be("run-known");
        doc.RootElement.GetProperty("status").GetString().Should().Be("running");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/migration/runs/{runId}/evidence-pack")]
    public async Task EvidencePack_WhenAbsent_ReturnsNotFound()
    {
        _catalog.Seed(new MigrationRunRecord
        {
            RunId = "run-no-pack",
            SourceKind = "geoserver-rest",
            Status = MigrationRunStatus.Running,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });

        var response = await _client.GetAsync("/api/v1/admin/migration/runs/run-no-pack/evidence-pack");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/migration/runs/{runId}/evidence-pack")]
    public async Task EvidencePack_WhenPresent_ReturnsJsonAttachment()
    {
        const string body = "{\"artifactKind\":\"honua.migration.evidence-pack\"}";
        _catalog.Seed(new MigrationRunRecord
        {
            RunId = "run-with-pack",
            SourceKind = "geoserver-rest",
            Status = MigrationRunStatus.Succeeded,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            EvidencePackFingerprint = "sha256:abc"
        }, evidencePackBody: body);

        var response = await _client.GetAsync("/api/v1/admin/migration/runs/run-with-pack/evidence-pack");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Headers.ETag!.Tag.Should().Contain("sha256:abc");
        (await response.Content.ReadAsStringAsync()).Should().Contain("honua.migration.evidence-pack");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/migration/runs/{runId}/cancel")]
    public async Task Cancel_WithUnknownRunId_ReturnsNotFound()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/migration/runs/does-not-exist/cancel",
            new { reason = "operator request" });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/migration/runs/{runId}/cancel")]
    public async Task Cancel_WhenRunIsRunning_TransitionsToCancelled()
    {
        _catalog.Seed(new MigrationRunRecord
        {
            RunId = "run-to-cancel",
            SourceKind = "geoserver-rest",
            Status = MigrationRunStatus.Running,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });

        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/migration/runs/run-to-cancel/cancel",
            new { reason = "operator request" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("cancelled");
        doc.RootElement.GetProperty("statusNote").GetString().Should().Be("operator request");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/migration/runs/{runId}/cancel")]
    public async Task Cancel_WhenRunIsAlreadyTerminal_ReturnsConflict()
    {
        _catalog.Seed(new MigrationRunRecord
        {
            RunId = "run-terminal",
            SourceKind = "geoserver-rest",
            Status = MigrationRunStatus.Succeeded,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });

        var response = await _client.PostAsync(
            "/api/v1/admin/migration/runs/run-terminal/cancel",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private sealed class InMemoryMigrationRunCatalog : IMigrationRunCatalog
    {
        private readonly ConcurrentDictionary<string, (MigrationRunRecord Record, string? Body)> _rows = new();
        private readonly ConcurrentDictionary<string, string> _scorecards = new();

        public void Seed(MigrationRunRecord record, string? evidencePackBody = null)
        {
            _rows[record.RunId] = (record, evidencePackBody);
        }

        public Task<MigrationRunRecord> RecordStartedAsync(MigrationRunRecord record, CancellationToken cancellationToken = default)
        {
            var stored = _rows.GetOrAdd(record.RunId, _ => (record, null));
            return Task.FromResult(stored.Record);
        }

        public Task<MigrationRunRecord?> RecordCompletedAsync(
            string runId,
            MigrationRunStatus status,
            DateTimeOffset completedAt,
            string? evidencePackRef,
            string? evidencePackFingerprint,
            string? evidencePackBody,
            string? statusNote,
            CancellationToken cancellationToken = default)
        {
            if (!_rows.TryGetValue(runId, out var existing))
            {
                return Task.FromResult<MigrationRunRecord?>(null);
            }
            if (existing.Record.Status != MigrationRunStatus.Running)
            {
                return Task.FromResult<MigrationRunRecord?>(existing.Record);
            }
            var updated = existing.Record with
            {
                Status = status,
                CompletedAt = completedAt,
                EvidencePackRef = evidencePackRef ?? existing.Record.EvidencePackRef,
                EvidencePackFingerprint = evidencePackFingerprint ?? existing.Record.EvidencePackFingerprint,
                StatusNote = statusNote ?? existing.Record.StatusNote
            };
            _rows[runId] = (updated, evidencePackBody ?? existing.Body);
            return Task.FromResult<MigrationRunRecord?>(updated);
        }

        public Task<MigrationRunRecord?> CancelAsync(string runId, DateTimeOffset cancelledAt, string? statusNote, CancellationToken cancellationToken = default)
        {
            if (!_rows.TryGetValue(runId, out var existing))
            {
                return Task.FromResult<MigrationRunRecord?>(null);
            }
            if (existing.Record.Status != MigrationRunStatus.Running)
            {
                return Task.FromResult<MigrationRunRecord?>(existing.Record);
            }
            var updated = existing.Record with
            {
                Status = MigrationRunStatus.Cancelled,
                CompletedAt = cancelledAt,
                StatusNote = statusNote ?? existing.Record.StatusNote
            };
            _rows[runId] = (updated, existing.Body);
            return Task.FromResult<MigrationRunRecord?>(updated);
        }

        public Task<MigrationRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_rows.TryGetValue(runId, out var existing) ? existing.Record : null);
        }

        public Task<MigrationRunListPage> ListAsync(MigrationRunListQuery query, CancellationToken cancellationToken = default)
        {
            IEnumerable<MigrationRunRecord> all = _rows.Values.Select(v => v.Record);
            if (!string.IsNullOrWhiteSpace(query.SourceKind))
            {
                all = all.Where(r => string.Equals(r.SourceKind, query.SourceKind, StringComparison.OrdinalIgnoreCase));
            }
            if (query.Status.HasValue)
            {
                all = all.Where(r => r.Status == query.Status.Value);
            }
            var ordered = all.OrderByDescending(r => r.StartedAt).ToList();
            var page = ordered.Skip(query.Offset).Take(query.Limit <= 0 ? 25 : query.Limit).ToArray();
            return Task.FromResult(new MigrationRunListPage
            {
                Items = page,
                TotalCount = ordered.Count,
                Limit = query.Limit <= 0 ? 25 : query.Limit,
                Offset = query.Offset
            });
        }

        public Task<string?> GetEvidencePackAsync(string runId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_rows.TryGetValue(runId, out var existing) ? existing.Body : null);
        }

        public Task<MigrationRunRecord?> RecordScorecardAsync(
            string runId,
            string scorecardFingerprint,
            string scorecardBody,
            CancellationToken cancellationToken = default)
        {
            if (!_rows.TryGetValue(runId, out var existing))
            {
                return Task.FromResult<MigrationRunRecord?>(null);
            }
            var updated = existing.Record with { ReconciliationScorecardFingerprint = scorecardFingerprint };
            _rows[runId] = (updated, existing.Body);
            _scorecards[runId] = scorecardBody;
            return Task.FromResult<MigrationRunRecord?>(updated);
        }

        public Task<string?> GetScorecardAsync(string runId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_scorecards.TryGetValue(runId, out var body) ? body : null);
        }
    }
}
