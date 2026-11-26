// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.EnrichmentCatalog.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        internal WebAppFixture Fixture { get; } = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Limits:Analytics:MaxDWithinDistanceMeters"] = "1000",
                })));

        public Task InitializeAsync() => Fixture.InitializeAsync();

        public Task DisposeAsync() => Fixture.DisposeAsync();
    }

    [IntegrationTheory]
    [Operation(Operations.RegisterEnrichmentDataset)]
    [Endpoint("POST /api/enrich/datasets")]
    [InlineData("reg-zero-distance", 0d)]
    [InlineData("reg-negative-distance", -5d)]
    [InlineData("reg-over-limit-distance", 1001d)]
    public async Task Register_InvalidDistance_ReturnsBadRequest(string id, double distanceMeters)
    {
        using var admin = _fixture.CreateAdminClient();

        using var content = Body(Dataset(id) with { DistanceMeters = distanceMeters });
        using var response = await admin.PostAsync("/api/enrich/datasets", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var stored = await admin.GetAsync($"/api/enrich/datasets/{id}");
        stored.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.RegisterEnrichmentDataset)]
    [Endpoint("POST /api/enrich/datasets")]
    public async Task Register_NumericMinimumEdition_ReturnsBadRequest()
    {
        using var admin = _fixture.CreateAdminClient();

        using var content = Body(Dataset("reg-numeric-edition") with { MinimumEdition = "5" });
        using var response = await admin.PostAsync("/api/enrich/datasets", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTheory]
    [Operation(Operations.UpdateEnrichmentDataset)]
    [Endpoint("PUT /api/enrich/datasets/{id}")]
    [InlineData(-1d)]
    [InlineData(1001d)]
    public async Task Update_InvalidDistance_IsRefusedAndKeepsTheStoredDistance(double distanceMeters)
    {
        using var admin = _fixture.CreateAdminClient();
        var id = $"upd-distance-{Guid.NewGuid():N}";
        var path = $"/api/enrich/datasets/{id}";
        using var createContent = Body(Dataset(id) with { DistanceMeters = 1000d });
        using var created = await admin.PostAsync("/api/enrich/datasets", createContent);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        using var updateContent = Body(new { distanceMeters });
        using var response = await admin.PutAsync(path, updateContent);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var stored = JsonDocument.Parse(await admin.GetStringAsync(path));
        stored.RootElement.GetProperty("distanceMeters").GetDouble().Should().Be(1000d);
    }

    [IntegrationTest]
    [Operation(Operations.UpdateEnrichmentDataset)]
    [Endpoint("PUT /api/enrich/datasets/{id}")]
    public async Task Update_ToDWithin_ValidatesTheRetainedDistanceAgainstTheConfiguredLimit()
    {
        using var admin = _fixture.CreateAdminClient();
        var id = $"upd-predicate-{Guid.NewGuid():N}";
        var path = $"/api/enrich/datasets/{id}";
        using var createContent = Body(Dataset(id) with { DefaultPredicate = "intersects", DistanceMeters = 1001d });
        using var created = await admin.PostAsync("/api/enrich/datasets", createContent);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        using var updateContent = Body(new { defaultPredicate = "dwithin" });
        using var response = await admin.PutAsync(path, updateContent);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var stored = JsonDocument.Parse(await admin.GetStringAsync(path));
        stored.RootElement.GetProperty("defaultPredicate").GetString().Should().Be("intersects");
        stored.RootElement.GetProperty("distanceMeters").GetDouble().Should().Be(1001d);
    }

    [IntegrationTest]
    [Operation(Operations.UpdateEnrichmentDataset)]
    [Endpoint("PUT /api/enrich/datasets/{id}")]
    public async Task Update_WithLegacyOverLimitDistance_RequiresCorrectingTheDefault()
    {
        using var admin = _fixture.CreateAdminClient();
        var id = $"upd-legacy-distance-{Guid.NewGuid():N}";
        var dataset = Dataset(id) with { DistanceMeters = 1000d };
        var path = $"/api/enrich/datasets/{id}";

        using var createContent = Body(dataset);
        using var created = await admin.PostAsync("/api/enrich/datasets", createContent);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IEnrichmentDatasetCatalogStore>();
        var original = await store.GetAsync(id);
        original.Should().NotBeNull();
        await store.UpdateAsync(original! with { DistanceMeters = 1001d });

        using var titleOnly = Body(new { title = "Updated title" });
        using var refused = await admin.PutAsync(path, titleOnly);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var stored = JsonDocument.Parse(await admin.GetStringAsync(path));
        stored.RootElement.GetProperty("title").GetString().Should().Be(dataset.Title);
        stored.RootElement.GetProperty("distanceMeters").GetDouble().Should().Be(1001d);

        using var corrected = Body(new { title = "Updated title", distanceMeters = 1000d });
        using var updated = await admin.PutAsync(path, corrected);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
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
