// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.DataEnrichment;

// Enrichment under production authentication: only an administrator changes the managed dataset catalog, and
// an anonymous caller cannot read a secured layer's features through POST /api/enrich.
[Collection("Database")]
[SecurityTest]
[Protocol(ProtocolNames.DataEnrichment)]
public sealed class EnrichmentAccessTests : IAsyncLifetime
{
    private const string ConfiguredDatasetKey = "access-boundaries";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataEnrichment:Datasets:0:Key"] = ConfiguredDatasetKey,
                ["DataEnrichment:Datasets:0:DisplayName"] = "Access Boundaries",
                ["DataEnrichment:Datasets:0:Category"] = "boundary",
                ["DataEnrichment:Datasets:0:LayerId"] = "1",
                ["DataEnrichment:Datasets:0:Predicate"] = "intersects",
            }));
        });

    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _admin = _fixture.CreateAdminClient();
    }

    public async Task DisposeAsync()
    {
        _admin.Dispose();
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.UpdateEnrichmentDataset)]
    [Endpoint("PUT /api/enrich/datasets/{id}")]
    public async Task Update_Anonymous_IsUnauthorizedAndChangesNothing()
    {
        var id = await RegisterManagedDatasetAsync();
        using var anonymous = _fixture.CreateClient();

        using var content = EnrichmentDatasetAdminHardeningTests.Body(new { title = "Updated title" });
        using var response = await anonymous.PutAsync($"/api/enrich/datasets/{id}", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var stored = JsonDocument.Parse(await _admin.GetStringAsync($"/api/enrich/datasets/{id}"));
        stored.RootElement.GetProperty("title").GetString().Should().Be($"Dataset {id}");
    }

    [IntegrationTest]
    [Operation(Operations.DeregisterEnrichmentDataset)]
    [Endpoint("DELETE /api/enrich/datasets/{id}")]
    public async Task Deregister_WrongKey_IsUnauthorizedAndKeepsTheDataset()
    {
        var id = await RegisterManagedDatasetAsync();
        using var wrongKey = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", "made-up"));

        using var response = await wrongKey.DeleteAsync($"/api/enrich/datasets/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var stored = await _admin.GetAsync($"/api/enrich/datasets/{id}");
        stored.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Enrich)]
    [Endpoint("POST /api/enrich")]
    public async Task Enrich_SecuredSourceLayerAnonymous_IsDeniedWithoutFeatures()
    {
        var secured = new AccessPolicy { AllowAnonymous = false };
        _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, accessPolicy: secured);
        using var anonymous = _fixture.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            datasetKey = ConfiguredDatasetKey,
            sourceLayerId = WebAppFixture.TestLayerId,
            predicate = "intersects",
        });

        using var authorizedContent = new StringContent(payload, Encoding.UTF8, "application/json");
        using var authorized = await _admin.PostAsync("/api/enrich", authorizedContent);
        authorized.StatusCode.Should().Be(HttpStatusCode.OK);
        using var result = JsonDocument.Parse(await authorized.Content.ReadAsStringAsync());
        result.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await anonymous.PostAsync("/api/enrich", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("FeatureCollection");
    }

    private async Task<string> RegisterManagedDatasetAsync()
    {
        var id = $"access-managed-{Guid.NewGuid():N}";
        using var content = EnrichmentDatasetAdminHardeningTests.Body(EnrichmentDatasetAdminHardeningTests.Dataset(id));
        using var response = await _admin.PostAsync("/api/enrich/datasets", content);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return id;
    }
}
