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
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Integration tests for FeatureServer true-curve INPUT round-trip (#1877 Parts A/B): a polyline
/// supplied as Esri <c>curvePaths</c> (cubic-Bézier segment) on applyEdits is densified to linear
/// vertices, stored, and surfaces as an ordinary densified <c>paths</c> polyline on query. NTS/WKB
/// cannot represent a true curve, so the stored/queried geometry is linear (densified) — this test
/// asserts that honest behavior, not lossless curve re-emission.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerTrueCurveTests : IAsyncLifetime
{
    // 3857 (Web Mercator, metres) endpoints near San Francisco; the spatial-reference fixture's
    // line layer is SRID 3857, so the curve geometry must carry wkid 3857 to match.
    private const double StartX = -13627361.0;
    private const double StartY = 4544000.0;
    private const double EndX = -13624000.0;
    private const double EndY = 4544000.0;

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "spatial-reference.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithCurvePathsBezier_DensifiesAndRoundTripsAsLinearPolyline()
    {
        var objectId = await AddCurvePathFeatureAsync();

        var geometry = await QueryGeometryAsync(objectId);

        // The stored geometry is a densified linear polyline (paths), NOT a curvePaths echo:
        // NTS/WKB cannot represent the true curve, so curves linearize at storage (#1877 Part B).
        geometry.TryGetProperty("paths", out var paths).Should().BeTrue(
            "a densified true curve must surface as a linear paths polyline");
        geometry.TryGetProperty("curvePaths", out _).Should().BeFalse(
            "stored linear geometry cannot be re-curved, so no curvePaths echo is emitted");

        var firstPath = paths[0];
        firstPath.GetArrayLength().Should().BeGreaterThan(
            2,
            "a cubic-Bézier segment must densify into multiple straight chords");

        // Endpoints survive densification exactly (within reprojection tolerance — none here, the
        // layer SRID matches the input SRID).
        var start = firstPath[0];
        var end = firstPath[firstPath.GetArrayLength() - 1];
        start[0].GetDouble().Should().BeApproximately(StartX, 1.0);
        start[1].GetDouble().Should().BeApproximately(StartY, 1.0);
        end[0].GetDouble().Should().BeApproximately(EndX, 1.0);
        end[1].GetDouble().Should().BeApproximately(EndY, 1.0);
    }

    private async Task<long> AddCurvePathFeatureAsync()
    {
        // A single cubic-Bézier curve segment from the start vertex to (EndX, EndY).
        var json = $$"""
        {
            "adds": [
                {
                    "attributes": { "name": "true-curve probe" },
                    "geometry": {
                        "curvePaths": [
                            [
                                [{{StartX.ToString(CultureInfo.InvariantCulture)}}, {{StartY.ToString(CultureInfo.InvariantCulture)}}],
                                { "b": [
                                    [{{EndX.ToString(CultureInfo.InvariantCulture)}}, {{EndY.ToString(CultureInfo.InvariantCulture)}}],
                                    [-13626000.0, 4546000.0],
                                    [-13625000.0, 4546000.0]
                                ] }
                            ]
                        ],
                        "spatialReference": { "wkid": 3857 }
                    }
                }
            ]
        }
        """;

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{SpatialReferenceTestLayerCatalog.ServiceId}/FeatureServer/{SpatialReferenceTestLayerCatalog.LineLayerId}/applyEdits",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var addResults = document.RootElement.GetProperty("addResults");
        addResults.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
        addResults[0].GetProperty("success").GetBoolean().Should().BeTrue(
            "the densified curve must be a valid linear polyline accepted by the edit pipeline");
        return addResults[0].GetProperty("objectId").GetInt64();
    }

    private async Task<JsonElement> QueryGeometryAsync(long objectId)
    {
        var url =
            $"/rest/services/{SpatialReferenceTestLayerCatalog.ServiceId}/FeatureServer/{SpatialReferenceTestLayerCatalog.LineLayerId}/query" +
            $"?objectIds={objectId.ToString(CultureInfo.InvariantCulture)}" +
            "&returnGeometry=true&returnTrueCurves=true&outSR=3857&f=json";

        var response = await _fixture.Client.GetAsync(url);
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var document = JsonDocument.Parse(content);
        var features = document.RootElement.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
        return features[0].GetProperty("geometry").Clone();
    }
}
