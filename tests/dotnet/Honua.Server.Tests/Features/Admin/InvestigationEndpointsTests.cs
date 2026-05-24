// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// HTTP integration tests for the Console Operate investigation endpoints (#1168).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class InvestigationEndpointsTests : IAsyncLifetime
{
    private readonly InMemoryInvestigationStore _store = new();
    private readonly CapturingAuditLog _audit = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public InvestigationEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDatabaseMigrationRunner>();
                services.AddSingleton<IDatabaseMigrationRunner>(new NoopMigrationRunner());

                services.RemoveAll<IInvestigationStore>();
                services.RemoveAll<IAuditLog>();
                services.AddSingleton<IInvestigationStore>(_store);
                services.AddSingleton<IAuditLog>(_audit);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/investigations")]
    public async Task CreateInvestigation_ReturnsCreated_AndAudits()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/investigations",
            new { title = "incident-1", summary = "investigating" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("investigationId").GetString();
        id.Should().NotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("status").GetString().Should().Be("open");

        _audit.Recorded.Should().ContainSingle();
        _audit.Recorded[0].Action.Should().Be("investigation.create");
        _audit.Recorded[0].ResourceId.Should().Be(id);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/investigations/{investigationId}/pins")]
    public async Task AddPin_LinksEventOnInvestigation()
    {
        var created = await _client.PostAsJsonAsync("/api/v1/admin/investigations", new { title = "incident-2" });
        var createdJson = await created.Content.ReadAsStringAsync();
        var id = JsonDocument.Parse(createdJson).RootElement.GetProperty("investigationId").GetString()!;

        var response = await _client.PostAsJsonAsync($"/api/v1/admin/investigations/{id}/pins",
            new
            {
                eventRef = "alert:42",
                eventKind = "alert",
                occurredAt = DateTimeOffset.UtcNow,
                note = "looks related"
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var pins = doc.RootElement.GetProperty("pins");
        pins.GetArrayLength().Should().Be(1);
        pins[0].GetProperty("eventRef").GetString().Should().Be("alert:42");
        pins[0].GetProperty("eventKind").GetString().Should().Be("alert");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/investigations/{investigationId}/links")]
    public async Task AddLink_RecordsAudit()
    {
        var created = await _client.PostAsJsonAsync("/api/v1/admin/investigations", new { title = "incident-3" });
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("investigationId").GetString()!;
        _audit.Recorded.Clear();

        var response = await _client.PostAsJsonAsync($"/api/v1/admin/investigations/{id}/links",
            new { resourceKind = "job", resourceId = "abc-123" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        _audit.Recorded.Should().ContainSingle();
        _audit.Recorded[0].Action.Should().Be("investigation.link.add");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/investigations")]
    public async Task ListInvestigations_ReturnsPage()
    {
        var response = await _client.GetAsync("/api/v1/admin/investigations");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/investigations/{investigationId}")]
    public async Task GetInvestigation_ReturnsRecord()
    {
        var created = await _client.PostAsJsonAsync("/api/v1/admin/investigations", new { title = "incident-get" });
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("investigationId").GetString()!;

        var response = await _client.GetAsync($"/api/v1/admin/investigations/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("investigationId").GetString().Should().Be(id);
    }

    [IntegrationTest]
    [Endpoint("PATCH /api/v1/admin/investigations/{investigationId}")]
    public async Task UpdateInvestigation_PersistsStatusChange()
    {
        var created = await _client.PostAsJsonAsync("/api/v1/admin/investigations", new { title = "incident-update" });
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("investigationId").GetString()!;

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/admin/investigations/{id}")
        {
            Content = JsonContent.Create(new { status = "closed", summary = "done" })
        };
        var response = await _client.SendAsync(patch);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("closed");
        doc.RootElement.GetProperty("summary").GetString().Should().Be("done");
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/investigations/{investigationId}/pins/{pinId}")]
    public async Task RemovePin_DeletesPinned()
    {
        var created = await _client.PostAsJsonAsync("/api/v1/admin/investigations", new { title = "incident-rm-pin" });
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("investigationId").GetString()!;

        var pinned = await _client.PostAsJsonAsync($"/api/v1/admin/investigations/{id}/pins",
            new
            {
                eventRef = "alert:5",
                eventKind = "alert",
                occurredAt = DateTimeOffset.UtcNow
            });
        var pinId = JsonDocument.Parse(await pinned.Content.ReadAsStringAsync())
            .RootElement.GetProperty("pins")[0].GetProperty("pinId").GetInt64();

        var response = await _client.DeleteAsync($"/api/v1/admin/investigations/{id}/pins/{pinId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("pins").GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/investigations/{investigationId}/links/{linkId}")]
    public async Task RemoveLink_DeletesLinked()
    {
        var created = await _client.PostAsJsonAsync("/api/v1/admin/investigations", new { title = "incident-rm-link" });
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("investigationId").GetString()!;

        var linked = await _client.PostAsJsonAsync($"/api/v1/admin/investigations/{id}/links",
            new { resourceKind = "release", resourceId = "2026.05.21" });
        var linkId = JsonDocument.Parse(await linked.Content.ReadAsStringAsync())
            .RootElement.GetProperty("links")[0].GetProperty("linkId").GetInt64();

        var response = await _client.DeleteAsync($"/api/v1/admin/investigations/{id}/links/{linkId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("links").GetArrayLength().Should().Be(0);
    }

    private sealed class InMemoryInvestigationStore : IInvestigationStore
    {
        private readonly Dictionary<string, InvestigationRecord> _records = new();
        private long _nextId = 1;

        public Task<InvestigationRecord> CreateAsync(string title, string createdBy, string? summary,
            DateTimeOffset createdAt, CancellationToken cancellationToken = default)
        {
            var id = "inv_" + Guid.NewGuid().ToString("N")[..10];
            var record = new InvestigationRecord
            {
                InvestigationId = id,
                Title = title,
                Status = InvestigationStatus.Open,
                CreatedAt = createdAt,
                CreatedBy = createdBy,
                UpdatedAt = createdAt,
                Summary = summary,
                Pins = ImmutableArray<InvestigationPin>.Empty,
                Links = ImmutableArray<InvestigationLink>.Empty
            };
            _records[id] = record;
            return Task.FromResult(record);
        }

        public Task<InvestigationRecord?> GetAsync(string investigationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_records.GetValueOrDefault(investigationId));

        public Task<InvestigationPage> ListAsync(InvestigationFilter filter, CancellationToken cancellationToken = default)
            => Task.FromResult(new InvestigationPage { Items = Array.Empty<InvestigationSummary>() });

        public Task<InvestigationRecord?> UpdateAsync(string investigationId, string? title, string? summary,
            InvestigationStatus? status, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            if (!_records.TryGetValue(investigationId, out var record))
            {
                return Task.FromResult<InvestigationRecord?>(null);
            }

            var updated = record with
            {
                Title = title ?? record.Title,
                Summary = summary ?? record.Summary,
                Status = status ?? record.Status,
                UpdatedAt = updatedAt
            };
            _records[investigationId] = updated;
            return Task.FromResult<InvestigationRecord?>(updated);
        }

        public Task<InvestigationRecord?> AddPinAsync(string investigationId, string eventRef,
            OperateEventKind eventKind, DateTimeOffset occurredAt, string? note, string actor,
            DateTimeOffset createdAt, CancellationToken cancellationToken = default)
        {
            if (!_records.TryGetValue(investigationId, out var record))
            {
                return Task.FromResult<InvestigationRecord?>(null);
            }

            var pin = new InvestigationPin
            {
                PinId = _nextId++,
                EventRef = eventRef,
                EventKind = eventKind,
                OccurredAt = occurredAt,
                Note = note,
                CreatedAt = createdAt,
                CreatedBy = actor
            };
            var updated = record with
            {
                Pins = record.Pins.Add(pin),
                UpdatedAt = createdAt
            };
            _records[investigationId] = updated;
            return Task.FromResult<InvestigationRecord?>(updated);
        }

        public Task<InvestigationRecord?> RemovePinAsync(string investigationId, long pinId,
            DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            if (!_records.TryGetValue(investigationId, out var record))
            {
                return Task.FromResult<InvestigationRecord?>(null);
            }

            var updated = record with
            {
                Pins = record.Pins.RemoveAll(p => p.PinId == pinId),
                UpdatedAt = updatedAt
            };
            _records[investigationId] = updated;
            return Task.FromResult<InvestigationRecord?>(updated);
        }

        public Task<InvestigationRecord?> AddLinkAsync(string investigationId,
            InvestigationResourceKind resourceKind, string resourceId, string? note, string actor,
            DateTimeOffset createdAt, CancellationToken cancellationToken = default)
        {
            if (!_records.TryGetValue(investigationId, out var record))
            {
                return Task.FromResult<InvestigationRecord?>(null);
            }

            var link = new InvestigationLink
            {
                LinkId = _nextId++,
                ResourceKind = resourceKind,
                ResourceId = resourceId,
                Note = note,
                CreatedAt = createdAt,
                CreatedBy = actor
            };
            var updated = record with
            {
                Links = record.Links.Add(link),
                UpdatedAt = createdAt
            };
            _records[investigationId] = updated;
            return Task.FromResult<InvestigationRecord?>(updated);
        }

        public Task<InvestigationRecord?> RemoveLinkAsync(string investigationId, long linkId,
            DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            if (!_records.TryGetValue(investigationId, out var record))
            {
                return Task.FromResult<InvestigationRecord?>(null);
            }

            var updated = record with
            {
                Links = record.Links.RemoveAll(l => l.LinkId == linkId),
                UpdatedAt = updatedAt
            };
            _records[investigationId] = updated;
            return Task.FromResult<InvestigationRecord?>(updated);
        }
    }

    private sealed class CapturingAuditLog : IAuditLog
    {
        public List<AuditEvent> Recorded { get; } = new();

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Recorded.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopMigrationRunner : IDatabaseMigrationRunner
    {
        public Task<DatabaseMigrationPlan> PlanMigrationsAsync(string connectionString,
            System.Reflection.Assembly migrationsAssembly, CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseMigrationPlan.Succeeded());

        public Task<DatabaseMigrationResult> RunMigrationsAsync(string connectionString,
            System.Reflection.Assembly migrationsAssembly, CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseMigrationResult.Succeeded());
    }
}
