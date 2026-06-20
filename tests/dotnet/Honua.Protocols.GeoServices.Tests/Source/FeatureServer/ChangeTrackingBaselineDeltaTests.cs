// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Integration tests for FeatureServer incremental change tracking (#1876): the change-log delta path
/// must report a feature added since a generation as an add on a sync from that generation, and must
/// NOT re-report it on the next sync from the advanced generation (snapshot-once-then-delta). Also
/// verifies migration 059 seeds a baseline so pre-change-tracking data is discoverable from gen 0.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class ChangeTrackingBaselineDeltaTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task ExtractChanges_AfterEdit_ReportsAddOnceThenDelta()
    {
        var changeTracker = _fixture.GetService<IChangeTracker>();
        var baselineGen = await changeTracker.GetCurrentGenerationAsync();

        // Add a feature through the shared edit pipeline (the change-tracking trigger fires on the
        // write, recording an Insert at a generation above baselineGen).
        var addedObjectId = await AddFeatureAsync("Change-tracking delta probe");

        // A sync from the baseline generation must include the just-added feature as an add.
        var firstExtract = await ExtractFromServerGenAsync(baselineGen);
        var firstLayer = SelectLayer(firstExtract.Root, WebAppFixture.TestLayerId);
        var firstAdds = firstLayer.GetProperty("adds").GetInt32();
        firstAdds.Should().BeGreaterThanOrEqualTo(1);
        ObjectIdsInAdds(firstLayer).Should().Contain(addedObjectId);

        var advancedGen = firstExtract.Root.GetProperty("serverGen").GetInt64();
        advancedGen.Should().BeGreaterThan(baselineGen);

        // A sync from the advanced generation must NOT re-report the same add: no edits happened after
        // advancedGen, so the delta is empty (snapshot-once-then-delta).
        var secondExtract = await ExtractFromServerGenAsync(advancedGen);
        var secondLayer = SelectLayer(secondExtract.Root, WebAppFixture.TestLayerId);
        secondLayer.GetProperty("adds").GetInt32().Should().Be(0);
        ObjectIdsInAdds(secondLayer).Should().NotContain(addedObjectId);

        firstExtract.Document.Dispose();
        secondExtract.Document.Dispose();
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task ChangeLogBaseline_CoversSeededFeatures_FromGenerationZero()
    {
        // Migration 059 backfills a baseline Insert row for every feature that lacks change-log
        // coverage, so a gen-0 scan of a populated layer resolves those features through the
        // change-log delta path rather than the all-features fallback.
        var changeTracker = _fixture.GetService<IChangeTracker>();
        var reader = _fixture.GetService<IFeatureReader>();

        var seeded = await reader.QueryAsync(
            WebAppFixture.TestLayerId,
            new Honua.Core.Features.FeatureStore.Domain.FeatureQuery { Limit = 1 },
            CancellationToken.None);

        if (seeded.Items.Length == 0)
        {
            // No seeded features in the test layer — nothing to baseline; the contract is vacuously held.
            return;
        }

        var changes = await changeTracker.GetChangesSinceAsync(0, [WebAppFixture.TestLayerId]);
        changes.Should().NotBeEmpty(
            "migration 059 seeds a baseline change row for every otherwise-untracked feature");
        changes.Should().Contain(c => c.ObjectId == seeded.Items[0].ObjectId);
    }

    private async Task<long> AddFeatureAsync(string name)
    {
        var editsRequest = new ApplyEditsRequest
        {
            Adds =
            [
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?> { ["name"] = name },
                    Geometry = new GeoServicesGeometry { X = -157.85, Y = 21.30 },
                },
            ],
        };

        var json = JsonSerializer.Serialize(editsRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/applyEdits",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var addResults = document.RootElement.GetProperty("addResults");
        addResults.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
        var first = addResults[0];
        first.GetProperty("success").GetBoolean().Should().BeTrue();
        return first.GetProperty("objectId").GetInt64();
    }

    private async Task<(JsonDocument Document, JsonElement Root)> ExtractFromServerGenAsync(long serverGen)
    {
        var payload = JsonSerializer.Serialize(new
        {
            serverGen,
            layers = WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            f = "json",
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/extractChanges",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var document = JsonDocument.Parse(content);
        return (document, document.RootElement);
    }

    private static JsonElement SelectLayer(JsonElement root, int layerId)
        => root.GetProperty("layerChanges")
            .EnumerateArray()
            .First(layer => layer.GetProperty("id").GetInt32() == layerId);

    private static IEnumerable<long> ObjectIdsInAdds(JsonElement layer)
    {
        if (!layer.TryGetProperty("addFeatures", out var addFeatures) || addFeatures.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var feature in addFeatures.EnumerateArray())
        {
            if (feature.TryGetProperty("attributes", out var attributes) &&
                attributes.TryGetProperty("objectid", out var objectId) &&
                objectId.TryGetInt64(out var value))
            {
                yield return value;
            }
        }
    }
}
