// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Import;

/// <summary>
/// External integration tests that exercise Esri import against real Geoportal-backed services.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Import)]
public sealed class EsriGeoportalImportIntegrationTests : IAsyncLifetime
{
    private const string ExternalServicesEnv = "HONUA_TEST_ESRI_GEOPORTAL";
    private static readonly EsriImportServiceCase[] ServiceCases =
    [
        new("https://geodata.hawaii.gov/arcgis/rest/services/Infrastructure/MapServer", 10, "dams"),
        new("https://geodata.hawaii.gov/arcgis/rest/services/Infrastructure/MapServer", 12, "marine_sewerlines"),
        new("https://geodata.hawaii.gov/arcgis/rest/services/HistoricCultural/MapServer", 3, "moku"),
        new("https://maps.kauai.gov/server/rest/services/Bridges_with_condition_where_available/FeatureServer", 2, "bridges")
    ];

    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [ExternalServiceTest(ExternalServicesEnv)]
    [Endpoint("POST /api/v1/admin/import/esri/discover")]
    public async Task Discover_GeoportalServices_ReturnsLayerSummaries()
    {
        foreach (var group in ServiceCases.GroupBy(service => service.ServiceUrl))
        {
            var request = new { ServiceUrl = group.Key, TimeoutSeconds = 30 };

            var response = await _client.PostAsJsonAsync("/api/v1/admin/import/esri/discover", request);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

            var layerIds = payload.GetProperty("layers")
                .EnumerateArray()
                .Select(layer => layer.GetProperty("id").GetInt32())
                .ToHashSet();

            foreach (var expectedLayerId in group.Select(service => service.LayerId))
            {
                layerIds.Should().Contain(expectedLayerId);
            }
        }
    }

    [ExternalServiceTest(ExternalServicesEnv)]
    [Endpoint("POST /api/v1/admin/import/esri/start")]
    public async Task Import_GeoportalLayers_Completes()
    {
        foreach (var serviceCase in ServiceCases)
        {
            var tableName = $"{serviceCase.TablePrefix}_{Guid.NewGuid().ToString("N")[..8]}";
            var startRequest = new
            {
                ServiceUrl = serviceCase.ServiceUrl,
                LayerId = serviceCase.LayerId,
                TableName = tableName,
                OverwriteExisting = true,
                BatchSize = 200,
                RequestTimeoutSeconds = 60,
                MaxRetries = 1
            };

            var response = await _client.PostAsJsonAsync("/api/v1/admin/import/esri/start", startRequest);
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var startPayload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var jobId = startPayload.GetProperty("jobId").GetString();
            jobId.Should().NotBeNullOrWhiteSpace();

            var progress = await WaitForCompletionAsync(_client, jobId!, TimeSpan.FromMinutes(3));
            ReadStatus(progress.GetProperty("status")).Should().Be(EsriImportStatus.Completed);

            progress.TryGetProperty("featuresProcessed", out var featuresProcessed).Should().BeTrue();
            featuresProcessed.GetInt64().Should().BeGreaterThan(0);
        }
    }

    private static async Task<JsonElement> WaitForCompletionAsync(
        HttpClient client,
        string jobId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/v1/admin/import/esri/jobs/{jobId}");

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                continue;
            }

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement.Clone();
            var status = ReadStatus(root.GetProperty("status"));

            if (status == EsriImportStatus.Completed)
            {
                return root;
            }

            if (status is EsriImportStatus.Failed or EsriImportStatus.Cancelled)
            {
                var error = root.TryGetProperty("errorMessage", out var errorElement)
                    ? errorElement.GetString()
                    : "Import failed.";
                throw new InvalidOperationException($"Esri import job {jobId} {status}: {error}");
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        throw new TimeoutException($"Esri import job {jobId} did not complete within {timeout.TotalSeconds} seconds.");
    }

    private static EsriImportStatus ReadStatus(JsonElement statusElement)
    {
        return statusElement.ValueKind switch
        {
            JsonValueKind.String => Enum.TryParse(statusElement.GetString(), out EsriImportStatus parsed)
                ? parsed
                : throw new InvalidOperationException("Import status is invalid."),
            JsonValueKind.Number => (EsriImportStatus)statusElement.GetInt32(),
            _ => throw new InvalidOperationException("Import status is invalid.")
        };
    }

    private sealed record EsriImportServiceCase(string ServiceUrl, int LayerId, string TablePrefix);
}
