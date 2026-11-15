// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.HealthCheck)]
public sealed class OpsIncidentEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "incident-context-test-key";
    private static readonly Guid IncidentId = Guid.NewGuid();
    private readonly StubContextReader _reader = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;
    private static string IncidentPath => $"/api/v1/admin/observability/incidents/{IncidentId}";

    public OpsIncidentEndpointsTests()
    {
        _fixture = new WebAppFixture().UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IOpsIncidentContextReader>();
                services.AddSingleton<IOpsIncidentContextReader>(_reader);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Endpoint("GET /api/v1/admin/observability/incidents/{incidentId}")]
    [IntegrationTheory]
    [InlineData(OpsIncidentEvidenceAvailability.Truncated, 9)]
    [InlineData(OpsIncidentEvidenceAvailability.Missing, 0)]
    public async Task GetIncident_IncompleteEvidence_PreservesRecordedUnknownAndExplicitUncertainty(
        OpsIncidentEvidenceAvailability availability, long totalCount)
    {
        _reader.Context = CreateContext(availability);
        var response = await _client.GetAsync(IncidentPath + "?limit=1&authorityScope=another-authority");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        root.GetProperty("context").GetProperty("episode").GetProperty("state").GetString().Should().Be("Unknown");
        root.GetProperty("context").GetProperty("episode").GetProperty("lifecycleVersion").GetInt64().Should().Be(7);
        root.GetProperty("context").GetProperty("evidenceAvailability").GetString().Should().Be(availability.ToString());
        root.GetProperty("context").GetProperty("evidence").GetProperty("totalCount").GetInt64().Should().Be(totalCount);
        root.GetProperty("context").GetProperty("episode").GetProperty("observedAt").GetDateTimeOffset()
            .Should().Be(_reader.Context.Episode.ObservedAt);
        root.GetProperty("readAt").GetDateTimeOffset().Should().BeAfter(_reader.Context.Episode.ObservedAt);
        root.GetProperty("affectedScope").GetString().Should().Be("Unknown");
        root.GetProperty("impact").GetString().Should().Be("Unknown");
        root.GetProperty("causeConfidence").GetString().Should().Be("Unknown");
        root.GetProperty("safeNextStep").GetString().Should().NotBeNullOrWhiteSpace();
        _reader.Query.Should().Be(new OpsIncidentContextQuery(new("platform", IncidentId), 1));
    }

    [Endpoint("GET /api/v1/admin/observability/incidents/{incidentId}")]
    [IntegrationTheory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task GetIncident_InvalidLimit_RejectsBeforeReaderInvocation(int limit)
    {
        var response = await _client.GetAsync(IncidentPath + $"?limit={limit}");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _reader.Query.Should().BeNull();
    }

    [Endpoint("GET /api/v1/admin/observability/incidents/{incidentId}")]
    [IntegrationTest]
    public async Task GetIncident_AbsentEpisode_ReturnsNotFound()
    {
        var response = await _client.GetAsync(IncidentPath);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _reader.Query.Should().Be(new OpsIncidentContextQuery(new("platform", IncidentId)));
    }

    [Endpoint("GET /api/v1/admin/observability/incidents/{incidentId}")]
    [IntegrationTest]
    public async Task GetIncident_DatabaseFailure_ReturnsUnavailableWithoutExceptionDetails()
    {
        _reader.Failure = new NpgsqlException("private connection and SQL details");
        var response = await _client.GetAsync(IncidentPath);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("private connection");
    }

    [Endpoint("GET /api/v1/admin/observability/incidents/{incidentId}")]
    [IntegrationTest]
    public async Task GetIncident_Unauthenticated_DoesNotReadContext()
    {
        using var anonymous = _fixture.CreateClient();
        var response = await anonymous.GetAsync(IncidentPath);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        _reader.Query.Should().BeNull();
    }

    [Endpoint("GET /api/v1/admin/observability/incidents/{incidentId}")]
    [IntegrationTest]
    public async Task GetIncident_OpsReadCredential_CanReadPersistedContext()
    {
        _reader.Context = CreateContext(OpsIncidentEvidenceAvailability.Truncated);
        using var created = await _client.PostAsJsonAsync("/api/v1/admin/api-keys",
            new { Name = $"incident-reader-{Guid.NewGuid():N}", Permissions = new[] { "ops:read" } });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        using var reader = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add(
            "X-API-Key", body.RootElement.GetProperty("data").GetProperty("key").GetString()!));
        (await reader.GetAsync(IncidentPath)).StatusCode.Should().Be(HttpStatusCode.OK);
        _reader.Query.Should().Be(new OpsIncidentContextQuery(new("platform", IncidentId)));
    }

    private static OpsIncidentContext CreateContext(OpsIncidentEvidenceAvailability availability) => new(
        new OpsIncidentEpisode
        {
            AuthorityScope = "platform", EpisodeId = IncidentId, FindingId = "alarm-finding",
            LifecycleVersion = 7, State = OpsIncidentEpisodeState.Unknown,
            StartedAt = DateTimeOffset.UtcNow.AddDays(-2), ObservedAt = DateTimeOffset.UtcNow.AddDays(-1)
        }, new OpsIncidentObservationPage(availability == OpsIncidentEvidenceAvailability.Missing ? [] :
        [new OpsIncidentObservation
        {
            AuthorityScope = "platform", SourceId = "test", ObservationId = "unknown-1", FindingId = "alarm-finding",
            ObservedAt = DateTimeOffset.UtcNow.AddDays(-1), State = OpsIncidentObservationState.Unknown, EvidenceJson = "{}"
        }])
        {
            TotalCount = availability == OpsIncidentEvidenceAvailability.Missing ? 0 : 9,
            Truncated = availability == OpsIncidentEvidenceAvailability.Truncated
        }, availability);

    private sealed class StubContextReader : IOpsIncidentContextReader
    {
        public OpsIncidentContext? Context { get; set; }
        public OpsIncidentContextQuery? Query { get; private set; }
        public Exception? Failure { get; set; }

        public Task<OpsIncidentContext?> ReadAsync(OpsIncidentContextQuery query, CancellationToken cancellationToken)
        {
            Query = query;
            return Failure is null ? Task.FromResult(Context) : Task.FromException<OpsIncidentContext?>(Failure);
        }
    }
}
