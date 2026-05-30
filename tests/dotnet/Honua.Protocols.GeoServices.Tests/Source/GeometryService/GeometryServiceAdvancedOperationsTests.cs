// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.GeometryService.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

[Protocol(TestProtocols.GeometryService)]
[Collection("Database")]
public sealed class GeometryServiceAdvancedOperationsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Intersect)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/intersect")]
    public async Task Intersect_PostValidRequest_ReturnsGeometry()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {
                        "rings": [[[0,0],[2,0],[2,2],[0,2],[0,0]]]
                    }
                ]
            },
            "geometry": {
                "rings": [[[1,1],[3,1],[3,3],[1,3],[1,1]]]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/intersect",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Intersect)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/intersect")]
    public async Task Intersect_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/Utilities/Geometry/GeometryServer/intersect?sr=4326");
        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Union)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/union")]
    public async Task Union_PostValidRequest_ReturnsSingleGeometry()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[1,0],[1,1],[0,1],[0,0]]]},
                    {"rings": [[[1,0],[2,0],[2,1],[1,1],[1,0]]]}
                ]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/union",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Union)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/union")]
    public async Task Union_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/Utilities/Geometry/GeometryServer/union");
        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Clip)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/clip")]
    public async Task Clip_PostValidRequest_ReturnsGeometry()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[3,0],[3,3],[0,3],[0,0]]]}
                ]
            },
            "geometry": {
                "rings": [[[1,1],[2,1],[2,2],[1,2],[1,1]]]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/clip",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Clip)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/clip")]
    public async Task Clip_NonRectangularClipGeometry_UsesEnvelopeForClipping()
    {
        // The clip geometry is a triangle with vertices at (1,1), (3,1), (2,3).
        // Its envelope is the rectangle (1,1)-(3,3).
        // The target polygon is (0,0)-(4,4).
        // If clip correctly uses the envelope, the result should be a rectangle (1,1)-(3,3),
        // NOT the intersection with the triangle itself.
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[4,0],[4,4],[0,4],[0,0]]]}
                ]
            },
            "geometry": {
                "rings": [[[1,1],[3,1],[2,3],[1,1]]]
            },
            "sr": "4326"
        }
        """;

        var clipResponse = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/clip",
            new StringContent(body, Encoding.UTF8, "application/json"));

        clipResponse.Be200Ok();
        var clipContent = await clipResponse.Content.ReadAsStringAsync();
        var clipResult = JsonSerializer.Deserialize(clipContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        clipResult.Should().NotBeNull();
        clipResult!.Geometries.Should().HaveCount(1);

        // Also run the same inputs through intersect to verify the results differ
        var intersectResponse = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/intersect",
            new StringContent(body, Encoding.UTF8, "application/json"));

        intersectResponse.Be200Ok();
        var intersectContent = await intersectResponse.Content.ReadAsStringAsync();
        var intersectResult = JsonSerializer.Deserialize(intersectContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        intersectResult.Should().NotBeNull();
        intersectResult!.Geometries.Should().HaveCount(1);

        // Clip (envelope-based) and intersect (full geometry) should produce different geometries
        // because the clip uses the rectangular envelope of the triangle, not the triangle itself
        var clipGeom = clipResult.Geometries![0].GetRawText();
        var intersectGeom = intersectResult.Geometries![0].GetRawText();
        clipGeom.Should().NotBe(intersectGeom,
            "clip should use the envelope of the clip geometry, producing a different result than intersect");
    }

    [IntegrationTest]
    [Operation(Operations.Clip)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/clip")]
    public async Task Clip_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/Utilities/Geometry/GeometryServer/clip?sr=4326");
        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Difference)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/difference")]
    public async Task Difference_PostValidRequest_ReturnsGeometry()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[3,0],[3,3],[0,3],[0,0]]]}
                ]
            },
            "geometry": {
                "rings": [[[1,1],[2,1],[2,2],[1,2],[1,1]]]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/difference",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Difference)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/difference")]
    public async Task Difference_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/Utilities/Geometry/GeometryServer/difference?sr=4326");
        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Area)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/areasAndLengths")]
    public async Task Area_PostValidRequest_ReturnsAreaValues()
    {
        var body = """
        {
            "polygons": [
                {"rings": [[[0,0],[0,10],[10,10],[10,0],[0,0]]]}
            ],
            "sr": "3857",
            "lengthUnit": "esriMeters",
            "areaUnit": "esriSquareMeters"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/areasAndLengths",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceAreasAndLengthsResponse);
        result.Should().NotBeNull();
        result!.Areas.Should().HaveCount(1);
        result.Lengths.Should().HaveCount(1);
        result.Areas![0].Should().BeGreaterThan(0);
        result.Lengths![0].Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Area)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/areasAndLengths")]
    public async Task Area_GeographicInput_ReturnsSquareMeters()
    {
        var body = """
        {
            "polygons": [
                {"rings": [[[0,0],[1,0],[1,1],[0,1],[0,0]]]}
            ],
            "sr": "4326",
            "lengthUnit": "esriMeters",
            "areaUnit": "esriSquareMeters"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/areasAndLengths",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceAreasAndLengthsResponse);

        result.Should().NotBeNull();
        result!.Areas.Should().HaveCount(1);
        result.Lengths.Should().HaveCount(1);
        result.Areas![0].Should().BeLessThan(0);
        result.Areas[0].Should().BeLessThan(-1_000_000_000d);
        result.Lengths![0].Should().BeGreaterThan(400_000d);
    }

    [IntegrationTest]
    [Operation(Operations.Area)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/areasAndLengths")]
    public async Task Area_PreserveShapeCalculationType_ReturnsGeodeticMeasurements()
    {
        var body = """
        {
            "polygons": [
                {"rings": [[[0,0],[0,1],[1,1],[1,0],[0,0]]]}
            ],
            "sr": "4326",
            "lengthUnit": "9036",
            "areaUnit": {"areaUnit": "esriSquareKilometers"},
            "calculationType": "preserveShape"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/areasAndLengths",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceAreasAndLengthsResponse);

        result.Should().NotBeNull();
        result!.Areas.Should().HaveCount(1);
        result.Lengths.Should().HaveCount(1);
        result.Areas![0].Should().BeGreaterThan(12_000d);
        result.Lengths![0].Should().BeGreaterThan(440d);
    }

    [IntegrationTest]
    [Operation(Operations.Area)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/areasAndLengths")]
    public async Task Area_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/Utilities/Geometry/GeometryServer/areasAndLengths?sr=4326");
        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Length)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/lengths")]
    public async Task Length_PostValidRequest_ReturnsLengthValues()
    {
        var body = """
        {
            "polylines": [
                {"paths": [[[0,0],[3,4]]]}
            ],
            "sr": "3857",
            "lengthUnit": "esriMeters"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/lengths",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceLengthResponse);
        result.Should().NotBeNull();
        result!.Lengths.Should().HaveCount(1);
        result.Lengths![0].Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Length)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/lengths")]
    public async Task Length_GeographicInput_ReturnsMeters()
    {
        var body = """
        {
            "polylines": [
                {"paths": [[[0,0],[1,0]]]}
            ],
            "sr": "4326",
            "lengthUnit": "esriMeters"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/lengths",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceLengthResponse);

        result.Should().NotBeNull();
        result!.Lengths.Should().HaveCount(1);
        result.Lengths![0].Should().BeGreaterThan(100_000d);
        result.Lengths![0].Should().BeLessThan(120_000d);
    }

    [IntegrationTest]
    [Operation(Operations.Length)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/lengths")]
    public async Task Length_GeodesicCalculationType_UsesGeodeticMeasurement()
    {
        var body = """
        {
            "polylines": [
                {"paths": [[[0,0],[1,0]]]}
            ],
            "sr": "4326",
            "lengthUnit": "esriMeters",
            "calculationType": "geodesic"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/lengths",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceLengthResponse);

        result.Should().NotBeNull();
        result!.Lengths.Should().HaveCount(1);
        result.Lengths![0].Should().BeGreaterThan(111_250d);
    }

    [IntegrationTest]
    [Operation(Operations.Length)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/lengths")]
    public async Task Length_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/Utilities/Geometry/GeometryServer/lengths?sr=4326");
        response.Be400BadRequest();
    }

    // --- Intersect: additional coverage ---

    [IntegrationTest]
    [Operation(Operations.Intersect)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/intersect")]
    public async Task Intersect_GetWithValidParameters_ReturnsGeometry()
    {
        var geometries = Uri.EscapeDataString("""{"geometryType":"esriGeometryPolygon","geometries":[{"rings":[[[0,0],[2,0],[2,2],[0,2],[0,0]]]}]}""");
        var geometry = Uri.EscapeDataString("""{"rings":[[[1,1],[3,1],[3,3],[1,3],[1,1]]]}""");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/Utilities/Geometry/GeometryServer/intersect?geometries={geometries}&geometry={geometry}&sr=4326");

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Intersect)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/intersect")]
    public async Task Intersect_GeometryTypeMismatch_PointVsPolygon_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [
                    {"x": 5, "y": 5}
                ]
            },
            "geometry": {
                "rings": [[[10,10],[11,10],[11,11],[10,11],[10,10]]]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/intersect",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Intersect)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/intersect")]
    public async Task Intersect_MissingSpatialReference_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[2,0],[2,2],[0,2],[0,0]]]}
                ]
            },
            "geometry": {
                "rings": [[[1,1],[3,1],[3,3],[1,3],[1,1]]]
            }
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/intersect",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Intersect)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/intersect")]
    public async Task Intersect_MalformedGeometry_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": "not-an-array"}
                ]
            },
            "geometry": {
                "rings": [[[1,1],[3,1],[3,3],[1,3],[1,1]]]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/intersect",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be400BadRequest();
    }

    // --- Union: additional coverage ---

    [IntegrationTest]
    [Operation(Operations.Union)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/union")]
    public async Task Union_GetWithValidParameters_ReturnsGeometry()
    {
        var geometries = Uri.EscapeDataString("""{"geometryType":"esriGeometryPolygon","geometries":[{"rings":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]},{"rings":[[[1,0],[2,0],[2,1],[1,1],[1,0]]]}]}""");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/Utilities/Geometry/GeometryServer/union?geometries={geometries}&sr=4326");

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Union)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/union")]
    public async Task Union_SingleGeometry_ReturnsSameGeometry()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[1,0],[1,1],[0,1],[0,0]]]}
                ]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/union",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Union)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/union")]
    public async Task Union_MissingSpatialReference_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[1,0],[1,1],[0,1],[0,0]]]}
                ]
            }
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/union",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Union)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/union")]
    public async Task Union_MalformedGeometry_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": "invalid"}
                ]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/union",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be400BadRequest();
    }

    // --- Difference: additional coverage ---

    [IntegrationTest]
    [Operation(Operations.Difference)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/difference")]
    public async Task Difference_GetWithValidParameters_ReturnsGeometry()
    {
        var geometries = Uri.EscapeDataString("""{"geometryType":"esriGeometryPolygon","geometries":[{"rings":[[[0,0],[3,0],[3,3],[0,3],[0,0]]]}]}""");
        var geometry = Uri.EscapeDataString("""{"rings":[[[1,1],[2,1],[2,2],[1,2],[1,1]]]}""");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/Utilities/Geometry/GeometryServer/difference?geometries={geometries}&geometry={geometry}&sr=4326");

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Difference)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/difference")]
    public async Task Difference_NoOverlap_ReturnsOriginal()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[1,0],[1,1],[0,1],[0,0]]]}
                ]
            },
            "geometry": {
                "rings": [[[10,10],[11,10],[11,11],[10,11],[10,10]]]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/difference",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Difference)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/difference")]
    public async Task Difference_MissingSpatialReference_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[3,0],[3,3],[0,3],[0,0]]]}
                ]
            },
            "geometry": {
                "rings": [[[1,1],[2,1],[2,2],[1,2],[1,1]]]
            }
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/difference",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Difference)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/difference")]
    public async Task Difference_MalformedGeometry_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": 12345}
                ]
            },
            "geometry": {
                "rings": [[[1,1],[2,1],[2,2],[1,2],[1,1]]]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/difference",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be400BadRequest();
    }

    // --- Area/Length: missing SR coverage ---

    [IntegrationTest]
    [Operation(Operations.Area)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/areasAndLengths")]
    public async Task Area_MissingSpatialReference_Returns400()
    {
        var body = """
        {
            "polygons": [
                {"rings": [[[0,0],[10,0],[10,10],[0,10],[0,0]]]}
            ],
            "areaUnit": "esriSquareMeters"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/areasAndLengths",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Length)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/lengths")]
    public async Task Length_MissingSpatialReference_Returns400()
    {
        var body = """
        {
            "polylines": [
                {"paths": [[[0,0],[3,4]]]}
            ],
            "lengthUnit": "esriMeters"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/lengths",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Length)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/lengths")]
    public async Task Length_InvalidCalculationType_ReturnsStandardGeoServicesError()
    {
        var body = """
        {
            "polylines": [
                {"paths": [[[0,0],[3,4]]]}
            ],
            "sr": "4326",
            "calculationType": "unsupported"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/lengths",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be400BadRequest();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiErrorResponse>(content);
        result.Should().NotBeNull();
        result!.Error.Message.Should().Be("Bad Request");
        result.Error.Details.Should().NotBeNull();
        result.Error.Details!.Should().Contain(detail => detail.Contains("calculationType", StringComparison.Ordinal));
    }
}
