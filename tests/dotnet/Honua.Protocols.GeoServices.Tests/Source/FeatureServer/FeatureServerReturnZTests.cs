// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Integration tests for FeatureServer 3D (Z) query support (#1877 Part A): a Z value written through
/// applyEdits must survive storage and be emitted on the query geometry when <c>returnZ=true</c>, and
/// must be absent when <c>returnZ</c> is omitted. Validates the extended-WKB read path round-trip.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerReturnZTests : IAsyncLifetime
{
    private const double ExpectedZ = 123.5;

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_ReturnZ_RoundTripsZThroughStorage()
    {
        var objectId = await AddPointZFeatureAsync();

        // returnZ=true must surface the stored Z on the geometry.
        var withZ = await QueryFeatureAsync(objectId, returnZ: true);
        withZ.TryGetProperty("z", out var zElement).Should().BeTrue("returnZ=true must emit the stored Z ordinate");
        zElement.GetDouble().Should().BeApproximately(ExpectedZ, 0.001);

        // returnZ omitted must not emit Z (2D output).
        var withoutZ = await QueryFeatureAsync(objectId, returnZ: false);
        (!withoutZ.TryGetProperty("z", out var omitted) || omitted.ValueKind == JsonValueKind.Null)
            .Should().BeTrue("returnZ omitted must not emit a Z ordinate");
    }

    private async Task<long> AddPointZFeatureAsync()
    {
        var editsRequest = new ApplyEditsRequest
        {
            Adds =
            [
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?> { ["name"] = "Z round-trip probe" },
                    Geometry = new GeoServicesGeometry
                    {
                        X = -157.85,
                        Y = 21.30,
                        Z = ExpectedZ,
                        HasZ = true,
                    },
                },
            ],
        };

        var json = JsonSerializer.Serialize(editsRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        using var jsonContent = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/applyEdits",
            jsonContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var addResults = document.RootElement.GetProperty("addResults");
        addResults.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
        addResults[0].GetProperty("success").GetBoolean().Should().BeTrue();
        return addResults[0].GetProperty("objectId").GetInt64();
    }

    private async Task<JsonElement> QueryFeatureAsync(long objectId, bool returnZ)
    {
        var url =
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
            $"?objectIds={objectId.ToString(CultureInfo.InvariantCulture)}" +
            $"&returnGeometry=true&returnZ={(returnZ ? "true" : "false")}&f=json";

        var response = await _fixture.Client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var document = JsonDocument.Parse(content);
        var features = document.RootElement.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
        return features[0].GetProperty("geometry").Clone();
    }
}
