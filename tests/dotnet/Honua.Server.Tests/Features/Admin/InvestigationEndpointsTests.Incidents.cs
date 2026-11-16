// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class InvestigationEndpointsTests
{
    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/investigations/{investigationId}/links")]
    public async Task AddLink_IncidentReference_RoundTripsAndAudits()
    {
        var id = await CreateIncidentInvestigationAsync();
        const string reference = "incident:platform:cdfc998b-027e-4bda-aed2-4068d40f6b4b";
        var response = await _client.PostAsJsonAsync($"/api/v1/admin/investigations/{id}/links",
            new { resourceKind = "incident", resourceId = reference, note = "Suspected cause, unverified" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var persisted = JsonDocument.Parse(await _client.GetStringAsync($"/api/v1/admin/investigations/{id}"));
        var link = persisted.RootElement.GetProperty("links")[0];
        link.GetProperty("resourceKind").GetString().Should().Be("incident");
        link.GetProperty("resourceId").GetString().Should().Be(reference);
        link.GetProperty("note").GetString().Should().Be("Suspected cause, unverified");
        _audit.Recorded.Should().Contain(e => e.Action == "investigation.link.add" && e.ResourceId == id);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/investigations/{investigationId}/pins")]
    public async Task AddPin_IncidentVersion_RoundTripsWithoutRefreshingEvidenceTime()
    {
        var id = await CreateIncidentInvestigationAsync();
        const string reference = "incident:platform:cdfc998b-027e-4bda-aed2-4068d40f6b4b:7";
        var occurredAt = new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
        var response = await _client.PostAsJsonAsync($"/api/v1/admin/investigations/{id}/pins",
            new { eventKind = "incident", eventRef = reference, occurredAt, note = "Recorded observation" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var persisted = JsonDocument.Parse(await _client.GetStringAsync($"/api/v1/admin/investigations/{id}"));
        var pin = persisted.RootElement.GetProperty("pins")[0];
        pin.GetProperty("eventKind").GetString().Should().Be("incident");
        pin.GetProperty("eventRef").GetString().Should().Be(reference);
        pin.GetProperty("occurredAt").GetDateTimeOffset().Should().Be(occurredAt);
        _audit.Recorded.Should().Contain(e => e.Action == "investigation.pin.add" && e.ResourceId == id);
    }

    private async Task<string> CreateIncidentInvestigationAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/investigations", new { title = "Alarm diagnosis" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return created.RootElement.GetProperty("investigationId").GetString()!;
    }
}
