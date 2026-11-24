// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.DataEnrichment;

// Managed enrichment-dataset writes refuse values the enrich handler would later reject, and a refused update
// leaves the stored dataset as it was.
[Collection("Database")]
[Protocol(ProtocolNames.DataEnrichment)]
public sealed class EnrichmentDatasetAdminHardeningTests(EnrichmentDatasetAdminHardeningTests.Host host)
    : IClassFixture<EnrichmentDatasetAdminHardeningTests.Host>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebAppFixture _fixture = host.Fixture;

    // One host for the class: each case uses its own dataset id.
    public sealed class Host : IAsyncLifetime
    {
        internal WebAppFixture Fixture { get; } = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

        public Task InitializeAsync() => Fixture.InitializeAsync();

        public Task DisposeAsync() => Fixture.DisposeAsync();
    }

    [IntegrationTheory]
    [Operation(Operations.RegisterEnrichmentDataset)]
    [Endpoint("POST /api/enrich/datasets")]
    [InlineData("reg-zero-distance", 0d)]
    [InlineData("reg-negative-distance", -5d)]
    public async Task Register_NonPositiveDistance_ReturnsBadRequest(string id, double distanceMeters)
    {
        var admin = _fixture.CreateAdminClient();

        using var response = await admin.PostAsync("/api/enrich/datasets", Body(Dataset(id) with { DistanceMeters = distanceMeters }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.GetAsync($"/api/enrich/datasets/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.RegisterEnrichmentDataset)]
    [Endpoint("POST /api/enrich/datasets")]
    public async Task Register_NumericMinimumEdition_ReturnsBadRequest()
    {
        var admin = _fixture.CreateAdminClient();

        using var response = await admin.PostAsync(
            "/api/enrich/datasets", Body(Dataset("reg-numeric-edition") with { MinimumEdition = "5" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.UpdateEnrichmentDataset)]
    [Endpoint("PUT /api/enrich/datasets/{id}")]
    public async Task Update_NonPositiveDistance_IsRefusedAndKeepsTheStoredDistance()
    {
        var admin = _fixture.CreateAdminClient();
        (await admin.PostAsync("/api/enrich/datasets", Body(Dataset("upd-distance") with { DistanceMeters = 100d })))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        using var response = await admin.PutAsync(
            "/api/enrich/datasets/upd-distance", Body(new { distanceMeters = -1d }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var stored = JsonDocument.Parse(await admin.GetStringAsync("/api/enrich/datasets/upd-distance"));
        stored.RootElement.GetProperty("distanceMeters").GetDouble().Should().Be(100d);
    }

    internal static DatasetRequest Dataset(string id) => new() { Id = id, Title = $"Dataset {id}" };

    internal static StringContent Body(object payload)
        => new(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

    internal sealed record DatasetRequest
    {
        public required string Id { get; init; }

        public required string Title { get; init; }

        public string Category { get; init; } = "boundary";

        public int LayerId { get; init; } = 1;

        public string DefaultPredicate { get; init; } = "dwithin";

        public double? DistanceMeters { get; init; }

        public string MinimumEdition { get; init; } = "Pro";
    }
}
