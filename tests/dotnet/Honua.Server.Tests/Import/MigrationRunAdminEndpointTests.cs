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
    [Endpoint("POST /api/v1/admin/migration/runs")]
    public async Task RecordStarted_WithValidRequest_PersistsRunForListAndGet()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/migration/runs",
            new
            {
                runId = "run-start-write",
                sourceKind = "arcgis-geoservices-rest",
                sourceUrl = "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
                sourceDisplayName = "Parcels"
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var recordPayload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            recordPayload.RootElement.GetProperty("runId").GetString().Should().Be("run-start-write");
            recordPayload.RootElement.GetProperty("status").GetString().Should().Be("running");
        }

        var listResponse = await _client.GetAsync("/api/v1/admin/migration/runs?sourceKind=arcgis-geoservices-rest");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var listPayload = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync()))
        {
            var item = listPayload.RootElement.GetProperty("items")[0];
            item.GetProperty("runId").GetString().Should().Be("run-start-write");
        }

        var getResponse = await _client.GetAsync("/api/v1/admin/migration/runs/run-start-write");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/migration/runs/{runId}/complete")]
    public async Task Complete_WithEvidencePack_PersistsTerminalStateAndEvidenceBody()
    {
        await _client.PostAsJsonAsync(
            "/api/v1/admin/migration/runs",
            new
            {
                runId = "run-complete-write",
                sourceKind = "arcgis-geoservices-rest",
                sourceUrl = "https://example.com/arcgis/rest/services/Roads/FeatureServer"
            });

        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/migration/runs/run-complete-write/complete",
            new
            {
                status = "succeeded",
                evidencePackRef = "evidence/run-complete-write.json",
                evidencePackFingerprint = "sha256:evidence-write",
                evidencePackBody = "{\"artifactKind\":\"honua.migration.evidence-pack\"}",
                statusNote = "completed by honua-migrate"
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            payload.RootElement.GetProperty("status").GetString().Should().Be("succeeded");
            payload.RootElement.GetProperty("hasEvidencePack").GetBoolean().Should().BeTrue();
            payload.RootElement.GetProperty("statusNote").GetString().Should().Be("completed by honua-migrate");
        }

        var evidenceResponse = await _client.GetAsync("/api/v1/admin/migration/runs/run-complete-write/evidence-pack");
        evidenceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await evidenceResponse.Content.ReadAsStringAsync()).Should().Contain("honua.migration.evidence-pack");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/migration/runs/{runId}/scorecard")]
    public async Task RecordScorecard_WithSignedScorecard_PersistsFingerprintAndBody()
    {
        _catalog.Seed(new MigrationRunRecord
        {
            RunId = "run-scorecard-write",
            SourceKind = "arcgis-geoservices-rest",
            Status = MigrationRunStatus.Succeeded,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });

        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/migration/runs/run-scorecard-write/scorecard",
            BuildScorecard("run-scorecard-write"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            payload.RootElement.GetProperty("reconciliationScorecardFingerprint").GetString().Should().Be("sha256:scorecard-write");
            payload.RootElement.GetProperty("hasReconciliationScorecard").GetBoolean().Should().BeTrue();
        }

        var scorecardResponse = await _client.GetAsync("/api/v1/admin/migration/runs/run-scorecard-write/scorecard");
        scorecardResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await scorecardResponse.Content.ReadAsStringAsync();
        body.Should().Contain("honua.migration.reconciliation-scorecard");
        body.Should().Contain("sha256:scorecard-write");
    }

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
    [Endpoint("GET /api/v1/admin/migration/runs/{runId}/scorecard")]
    public async Task Scorecard_WhenAbsent_ReturnsNotFound()
    {
        _catalog.Seed(new MigrationRunRecord
        {
            RunId = "run-no-scorecard",
            SourceKind = "geoserver-rest",
            Status = MigrationRunStatus.Succeeded,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow
        });

        var response = await _client.GetAsync("/api/v1/admin/migration/runs/run-no-scorecard/scorecard");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/migration/runs/{runId}/scorecard")]
    public async Task Scorecard_WhenPresent_ReturnsSignedScorecardJson()
    {
        const string body = "{\"artifactKind\":\"honua.migration.reconciliation-scorecard\"}";
        _catalog.Seed(new MigrationRunRecord
        {
            RunId = "run-with-scorecard",
            SourceKind = "geoserver-rest",
            Status = MigrationRunStatus.Succeeded,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow
        });
        await _catalog.RecordScorecardAsync("run-with-scorecard", "sha256:scorecard-abc", body);

        var response = await _client.GetAsync("/api/v1/admin/migration/runs/run-with-scorecard/scorecard");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.Should().Contain("sha256:scorecard-abc");
        (await response.Content.ReadAsStringAsync()).Should().Contain("honua.migration.reconciliation-scorecard");
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
                var status = query.Status.Value;
                all = all.Where(r => r.Status == status);
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

    private static MigrationReconciliationScorecard BuildScorecard(string runId) => new()
    {
        RunId = runId,
        SourceKind = "arcgis-geoservices-rest",
        GeneratedAt = DateTimeOffset.UtcNow,
        Verdict = "pass",
        DataReconciliation = new MigrationScorecardDataReconciliation
        {
            Classification = "pass",
            LayerCount = 1,
            PassCount = 1,
            Layers =
            [
                new MigrationScorecardLayer
                {
                    SourceLayerId = "0",
                    SourceLayerName = "Parcels",
                    TargetHonuaLayerId = 1,
                    Classification = "pass"
                }
            ]
        },
        CapabilityParity = new MigrationScorecardCapabilityParity
        {
            ConstructCount = 1,
            AutomatedCount = 1,
            ParityRatio = 1
        },
        Fingerprint = "sha256:scorecard-write"
    };
}
