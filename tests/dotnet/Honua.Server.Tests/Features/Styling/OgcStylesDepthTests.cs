// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Styling;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Styling;

/// <summary>
/// Depth pack for the OGC API - Styles surface (#2983): content negotiation beyond the
/// happy path (Esri drawingInfo encoding, quality ordering, wildcard fallback, 406),
/// manage-styles error contracts (415/400/404), Esri drawingInfo round trips through
/// PUT including lossy-conversion strict/non-strict handling, standalone catalog style
/// behavior (server-assigned ids, metadata, derived SLD), output-cache eviction on
/// mutation, and unknown-query-parameter rejection. Complements the happy-path suite
/// in <see cref="OgcStylesEndpointTests"/>.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.OgcApiStyles)]
public sealed class OgcStylesDepthTests : IAsyncLifetime
{
    private const string MapboxStyleMediaType = "application/vnd.mapbox.style+json";
    private const string EsriDrawingInfoMediaType = "application/vnd.esri.drawinginfo+json";
    private const string SldMediaType = "application/vnd.ogc.sld+xml";

    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    // --- Content negotiation ---

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}")]
    public async Task GetStylesheet_AcceptEsriDrawingInfo_ReturnsBackGeneratedRenderer()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        request.Headers.TryAddWithoutValidation("Accept", EsriDrawingInfoMediaType);

        var response = await client.SendAsync(request);

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(EsriDrawingInfoMediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        document.RootElement.TryGetProperty("renderer", out var renderer).Should().BeTrue(
            "the drawingInfo projection is back-generated from the canonical MapLibre style (ADR-0002)");
        renderer.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}")]
    public async Task GetStylesheet_UnacceptableMediaType_Returns406()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        request.Headers.TryAddWithoutValidation("Accept", "text/html");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}")]
    public async Task GetStylesheet_UnmatchedTypeWithWildcard_FallsBackToMapLibre()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        request.Headers.TryAddWithoutValidation("Accept", "text/html, */*;q=0.1");

        var response = await client.SendAsync(request);

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MapboxStyleMediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("version").GetInt32().Should().Be(8);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}")]
    public async Task GetStylesheet_QualityOrdering_PicksHighestQualityStylesheetType()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);
        var path = $"/ogc/styles/{Uri.EscapeDataString(styleId)}";

        // MapLibre carries the higher q-value: it must win over SLD.
        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.TryAddWithoutValidation(
                "Accept",
                "application/vnd.ogc.sld+xml;q=0.4, application/vnd.mapbox.style+json;q=0.9");
            var response = await client.SendAsync(request);
            response.Be200Ok();
            response.Content.Headers.ContentType?.MediaType.Should().Be(MapboxStyleMediaType);
        }

        // Reversed q-values: SLD must win.
        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.TryAddWithoutValidation(
                "Accept",
                "application/vnd.ogc.sld+xml;q=0.9, application/vnd.mapbox.style+json;q=0.4");
            var response = await client.SendAsync(request);
            response.Be200Ok();
            response.Content.Headers.ContentType?.MediaType.Should().Be(SldMediaType);
        }

        // A more-specific q=0 excludes Esri even though the wildcard accepts other
        // representations. MapLibre remains the server-preferred wildcard fallback.
        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.TryAddWithoutValidation(
                "Accept",
                $"{EsriDrawingInfoMediaType};q=0, */*;q=1");
            var response = await client.SendAsync(request);
            response.Be200Ok();
            response.Content.Headers.ContentType?.MediaType.Should().Be(MapboxStyleMediaType);
        }

        // application/json is an accepted request alias, not the emitted vendor media
        // type, so excluding only the alias must not override a wildcard Mapbox match.
        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.TryAddWithoutValidation("Accept", "application/json;q=0, */*;q=1");
            var response = await client.SendAsync(request);
            response.Be200Ok();
            response.Content.Headers.ContentType?.MediaType.Should().Be(MapboxStyleMediaType);
        }

        // A positive exact JSON alias outranks a lower-quality application wildcard.
        // The wildcard must not overwrite the alias before the Esri candidate is ranked.
        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.TryAddWithoutValidation(
                "Accept",
                $"application/json;q=1, application/*;q=0.1, {EsriDrawingInfoMediaType};q=0.5");
            var response = await client.SendAsync(request);
            response.Be200Ok();
            response.Content.Headers.ContentType?.MediaType.Should().Be(MapboxStyleMediaType);
        }

        // An explicit exclusion of the emitted Mapbox representation is more specific
        // than its JSON compatibility alias, so the acceptable Esri representation wins.
        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.TryAddWithoutValidation(
                "Accept",
                $"application/json;q=1, {MapboxStyleMediaType};q=0, {EsriDrawingInfoMediaType};q=0.5");
            var response = await client.SendAsync(request);
            response.Be200Ok();
            response.Content.Headers.ContentType?.MediaType.Should().Be(EsriDrawingInfoMediaType);
        }

        // A versioned SLD range is more specific than the same unversioned media type.
        // Its q=0 exclusion vetoes SLD 1.0 only; the unversioned range still accepts
        // SLD 1.1, which outranks the lower-quality Esri representation.
        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.TryAddWithoutValidation(
                "Accept",
                $"{SldMediaType};version=1.0;q=0, {SldMediaType};q=1, {EsriDrawingInfoMediaType};q=0.5");
            var response = await client.SendAsync(request);
            response.Be200Ok();
            response.Content.Headers.ContentType?.MediaType.Should().Be(SldMediaType);
            (await response.Content.ReadAsStringAsync()).Should().Contain("version=\"1.1.0\"");
        }

        // The symmetric exclusion leaves SLD 1.0 available through the generic range.
        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.TryAddWithoutValidation(
                "Accept",
                $"{SldMediaType};version=1.1;q=0, {SldMediaType};q=1");
            var response = await client.SendAsync(request);
            response.Be200Ok();
            (await response.Content.ReadAsStringAsync()).Should().Contain("version=\"1.0.0\"");
        }

        // Parameters that are not present on an emitted representation do not match it.
        // They cannot override an explicit exclusion of the actual representation.
        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.TryAddWithoutValidation(
                "Accept",
                $"{MapboxStyleMediaType};profile=unsupported;q=1, {MapboxStyleMediaType};q=0");
            (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.TryAddWithoutValidation(
                "Accept",
                $"{EsriDrawingInfoMediaType};profile=unsupported;q=1, {EsriDrawingInfoMediaType};q=0");
            (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
        }

        // Unsupported SLD versions match neither emitted SLD representation. They do
        // not exclude the supported unversioned fallback or win with a positive q-value.
        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.TryAddWithoutValidation(
                "Accept",
                $"{SldMediaType};version=2.0;q=0, {SldMediaType};q=1");
            var response = await client.SendAsync(request);
            response.Be200Ok();
            response.Content.Headers.ContentType?.MediaType.Should().Be(SldMediaType);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.TryAddWithoutValidation(
                "Accept",
                $"{SldMediaType};version=2.0;q=1, {EsriDrawingInfoMediaType};q=0.5");
            var response = await client.SendAsync(request);
            response.Be200Ok();
            response.Content.Headers.ContentType?.MediaType.Should().Be(EsriDrawingInfoMediaType);
        }

        // HTTP media type tokens are case-insensitive for both aliases and concrete
        // vendor representations.
        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.TryAddWithoutValidation("Accept", "Application/Vnd.Esri.DrawingInfo+Json");
            var response = await client.SendAsync(request);
            response.Be200Ok();
            response.Content.Headers.ContentType?.MediaType.Should().Be(EsriDrawingInfoMediaType);
        }

        // The responses are emitted as UTF-8, so a charset=utf-8 media-range parameter
        // matches the representation instead of disqualifying it the way an unknown
        // parameter does. Case is insignificant for the charset token as well.
        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.TryAddWithoutValidation("Accept", $"{EsriDrawingInfoMediaType}; charset=UTF-8");
            var response = await client.SendAsync(request);
            response.Be200Ok();
            response.Content.Headers.ContentType?.MediaType.Should().Be(EsriDrawingInfoMediaType);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.TryAddWithoutValidation("Accept", "application/json; charset=utf-8");
            var response = await client.SendAsync(request);
            response.Be200Ok();
            response.Content.Headers.ContentType?.MediaType.Should().Be(MapboxStyleMediaType);
        }

        // A charset the server never emits does not match, so it cannot outrank an
        // explicit exclusion of the same representation.
        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.TryAddWithoutValidation(
                "Accept",
                $"{EsriDrawingInfoMediaType};charset=iso-8859-1;q=1, {EsriDrawingInfoMediaType};q=0");
            (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
        }

        // Without an acceptable fallback, q=0 means the representation is rejected.
        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.TryAddWithoutValidation("Accept", $"{EsriDrawingInfoMediaType};q=0");
            (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}")]
    public async Task GetStylesheet_AcceptApplicationJson_ReturnsMapLibreAlias()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        var response = await client.SendAsync(request);

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MapboxStyleMediaType);
    }

    // --- Manage-styles error contracts ---

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_UnsupportedContentType_Returns415()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);

        using var content = new StringContent("body=1", Encoding.UTF8, "text/plain");
        var response = await client.PutAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}", content);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/styles")]
    public async Task PostStyle_EsriDrawingInfoContentType_Returns415()
    {
        // POST (create) deliberately accepts only MapLibre/Mapbox JSON; the Esri
        // drawingInfo request encoding is a PUT-only capability.
        var client = _fixture.CreateAdminClient();

        using var content = new StringContent(SimpleRendererDrawingInfoJson, Encoding.UTF8, EsriDrawingInfoMediaType);
        var response = await client.PostAsync("/ogc/styles", content);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/styles")]
    public async Task PostStyle_DrawingInfoAsApplicationJson_Returns400WithCorrectMediaType()
    {
        var client = _fixture.CreateAdminClient();

        using var content = new StringContent(SimpleRendererDrawingInfoJson, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/ogc/styles", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("detail").GetString().Should().Contain(EsriDrawingInfoMediaType);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_DrawingInfoAsApplicationJson_Returns400WithoutReplacingCanonicalStyle()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);
        var path = $"/ogc/styles/{Uri.EscapeDataString(styleId)}";
        var before = await client.GetStringAsync(path);

        using var content = new StringContent(SimpleRendererDrawingInfoJson, Encoding.UTF8, "application/json");
        var response = await client.PutAsync(path, content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("detail").GetString().Should().Contain(EsriDrawingInfoMediaType);
        (await client.GetStringAsync(path)).Should().Be(before);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_EmptyBody_Returns400()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);

        using var content = new StringContent(string.Empty, Encoding.UTF8, MapboxStyleMediaType);
        var response = await client.PutAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/styles")]
    public async Task PostStyle_EmptyBody_Returns400()
    {
        var client = _fixture.CreateAdminClient();

        using var content = new StringContent(string.Empty, Encoding.UTF8, MapboxStyleMediaType);
        var response = await client.PostAsync("/ogc/styles", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/styles")]
    public async Task PostStyle_MalformedJson_Returns400()
    {
        var client = _fixture.CreateAdminClient();

        using var content = new StringContent("{ this is not json", Encoding.UTF8, MapboxStyleMediaType);
        var response = await client.PostAsync("/ogc/styles", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/styles")]
    public async Task PostStyle_NonObjectRoot_Returns400()
    {
        var client = _fixture.CreateAdminClient();

        using var content = new StringContent("[1, 2, 3]", Encoding.UTF8, MapboxStyleMediaType);
        var response = await client.PostAsync("/ogc/styles", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/styles")]
    public async Task PostStyle_MissingVersion_LenientCreates_StrictRejects()
    {
        // Standalone catalog styles are deliberately validated weaker than layer-bound
        // styles: an empty JSON object is accepted unless strict handling is requested.
        var client = _fixture.CreateAdminClient();

        var lenientId = $"lenient-{Guid.NewGuid():N}";
        using (var content = new StringContent("{}", Encoding.UTF8, MapboxStyleMediaType))
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles") { Content = content })
        {
            request.Headers.TryAddWithoutValidation("X-Style-Id", lenientId);
            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using (var content = new StringContent("{}", Encoding.UTF8, MapboxStyleMediaType))
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles") { Content = content })
        {
            request.Headers.TryAddWithoutValidation("X-Style-Id", $"strict-{Guid.NewGuid():N}");
            request.Headers.TryAddWithoutValidation("Prefer", "handling=strict");
            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_UnknownStyleId_Returns404()
    {
        var client = _fixture.CreateAdminClient();

        using var content = new StringContent(BuildDefaultStyleJson(), Encoding.UTF8, MapboxStyleMediaType);
        var response = await client.PutAsync($"/ogc/styles/missing-{Guid.NewGuid():N}", content);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_MalformedJson_Returns400()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);

        using var content = new StringContent("{ broken", Encoding.UTF8, MapboxStyleMediaType);
        var response = await client.PutAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- Esri drawingInfo round trips through PUT ---

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_EsriDrawingInfo_SimpleRenderer_RoundTripsThroughCanonicalMapLibre()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);
        var path = $"/ogc/styles/{Uri.EscapeDataString(styleId)}";

        using (var content = new StringContent(SimpleRendererDrawingInfoJson, Encoding.UTF8, EsriDrawingInfoMediaType))
        {
            var putResponse = await client.PutAsync(path, content);
            putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
            putResponse.Headers.Contains("X-Style-Unsupported-Symbolizers").Should().BeFalse(
                "a simple marker renderer converts to MapLibre without loss");
        }

        // Canonical read-back: the stored style is MapLibre (ADR-0002).
        var mapLibreResponse = await client.GetAsync(path);
        mapLibreResponse.Be200Ok();
        mapLibreResponse.Content.Headers.ContentType?.MediaType.Should().Be(MapboxStyleMediaType);
        using (var document = JsonDocument.Parse(await mapLibreResponse.Content.ReadAsStringAsync()))
        {
            document.RootElement.GetProperty("version").GetInt32().Should().Be(8);
            var layerTypes = document.RootElement.GetProperty("layers")
                .EnumerateArray()
                .Select(l => l.GetProperty("type").GetString())
                .ToArray();
            layerTypes.Should().Contain("circle", "an esriSMS point renderer projects to a MapLibre circle layer");
        }

        // Esri read-back: drawingInfo is back-generated from the stored MapLibre.
        using var esriRequest = new HttpRequestMessage(HttpMethod.Get, path);
        esriRequest.Headers.TryAddWithoutValidation("Accept", EsriDrawingInfoMediaType);
        var esriResponse = await client.SendAsync(esriRequest);
        esriResponse.Be200Ok();
        using (var document = JsonDocument.Parse(await esriResponse.Content.ReadAsStringAsync()))
        {
            var renderer = document.RootElement.GetProperty("renderer");
            renderer.GetProperty("type").GetString().Should().Be("simple");
            var symbol = renderer.GetProperty("symbol");
            symbol.GetProperty("type").GetString().Should().Be("esriSMS");
            var color = symbol.GetProperty("color").EnumerateArray().Select(c => c.GetInt32()).ToArray();
            color.Take(3).Should().Equal(255, 0, 0);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_EsriDrawingInfo_UnboundStyle_Returns400WithoutRebindingToLayerZero()
    {
        const string unboundStyle =
            """
            {
              "version": 8,
              "sources": {
                "layer-0": {
                  "type": "vector",
                  "tiles": ["https://example.test/tiles/{z}/{x}/{y}.mvt"]
                }
              },
              "layers": [
                { "id": "external-points", "type": "circle", "source": "layer-0" }
              ]
            }
            """;
        var client = _fixture.CreateAdminClient();
        var styleId = $"unbound-{Guid.NewGuid():N}";
        using (var content = new StringContent(unboundStyle, Encoding.UTF8, MapboxStyleMediaType))
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles") { Content = content })
        {
            request.Headers.TryAddWithoutValidation("X-Style-Id", styleId);
            (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using var drawingInfo = new StringContent(
            SimpleRendererDrawingInfoJson,
            Encoding.UTF8,
            EsriDrawingInfoMediaType);
        var response = await client.PutAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}", drawingInfo);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("not bound to a Honua layer");

        var fetched = await client.GetAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        fetched.Be200Ok();
        using var document = JsonDocument.Parse(await fetched.Content.ReadAsStringAsync());
        var sources = document.RootElement.GetProperty("sources");
        sources.TryGetProperty("layer-0", out var externalSource).Should().BeTrue();
        externalSource.GetProperty("tiles")[0].GetString().Should().StartWith("https://example.test/");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_EsriDrawingInfo_MalformedJson_Returns400()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);

        using var content = new StringContent("{ nope", Encoding.UTF8, EsriDrawingInfoMediaType);
        var response = await client.PutAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_EsriDrawingInfo_LossyConversion_NonStrict_Returns204WithWarningHeader()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);

        using var content = new StringContent(NonUniformPictureMarkerDrawingInfoJson, Encoding.UTF8, EsriDrawingInfoMediaType);
        var response = await client.PutAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}", content);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.TryGetValues("X-Style-Unsupported-Symbolizers", out var warnings).Should().BeTrue(
            "a lossy conversion must surface its warnings non-blockingly");
        string.Join(" | ", warnings!).Should().Contain("PICTURE_MARKER_PARTIAL");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_EsriDrawingInfo_LossyConversion_Strict_Returns400WithDiagnostics()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);

        using var content = new StringContent(NonUniformPictureMarkerDrawingInfoJson, Encoding.UTF8, EsriDrawingInfoMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/ogc/styles/{Uri.EscapeDataString(styleId)}")
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("Prefer", "handling=strict");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("PICTURE_MARKER_PARTIAL", "strict rejection must carry the stable diagnostic code");
    }

    // --- Standalone catalog styles (ADR-0048 Phase 2) ---

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/styles")]
    public async Task PostStyle_WithoutStyleIdHeader_AssignsServerGeneratedId()
    {
        var client = _fixture.CreateAdminClient();

        using var content = new StringContent(BuildDefaultStyleJson(), Encoding.UTF8, MapboxStyleMediaType);
        var response = await client.PostAsync("/ogc/styles", content);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var assignedId = document.RootElement.GetProperty("id").GetString();
        assignedId.Should().StartWith("style-", "the server assigns a stable identifier when none is requested");
        response.Headers.Location!.ToString().Should().Contain(Uri.EscapeDataString(assignedId!));

        var fetched = await client.GetAsync($"/ogc/styles/{Uri.EscapeDataString(assignedId!)}");
        fetched.Be200Ok();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}/metadata")]
    public async Task GetStyleMetadata_StandaloneStyle_ReturnsCatalogMetadata()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await CreateStandaloneStyleAsync(client);

        var response = await client.GetAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}/metadata");

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("id").GetString().Should().Be(styleId);
        document.RootElement.GetProperty("title").GetString().Should().NotBeNullOrEmpty();
        var links = document.RootElement.GetProperty("links").EnumerateArray().ToArray();
        links.Should().Contain(l => l.GetProperty("rel").GetString() == "stylesheet");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}")]
    public async Task GetStylesheet_StandaloneStyle_AcceptSld11_ReturnsDerivedSld()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await CreateStandaloneStyleAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.ogc.sld+xml;version=1.1");

        var response = await client.SendAsync(request);

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(SldMediaType);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("StyledLayerDescriptor");
        body.Should().Contain("version=\"1.1.0\"");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}")]
    public async Task GetStylesheet_StandaloneStyle_AcceptEsriDrawingInfo_ReturnsDrawingInfo()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await CreateStandaloneStyleAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        request.Headers.TryAddWithoutValidation("Accept", EsriDrawingInfoMediaType);

        var response = await client.SendAsync(request);

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(EsriDrawingInfoMediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.TryGetProperty("renderer", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_StandaloneStyle_UpdatesCatalogStyle()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await CreateStandaloneStyleAsync(client);

        using var content = new StringContent(BuildDefaultStyleJson(), Encoding.UTF8, MapboxStyleMediaType);
        var response = await client.PutAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}", content);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("POST /ogc/styles")]
    [Endpoint("DELETE /ogc/styles/{styleId}")]
    public async Task StylesList_ReflectsCreateAndDelete_ThroughOutputCacheEviction()
    {
        // The styles list is output-cached ("OgcStylesList", tag ogc-styles); successful
        // mutations must evict it so anonymous readers never see a stale list for the TTL.
        var adminClient = _fixture.CreateAdminClient();
        var anonymousClient = _fixture.CreateClient();

        // Prime the anonymous cache entry before the create.
        (await anonymousClient.GetAsync("/ogc/styles")).Be200Ok();

        var styleId = await CreateStandaloneStyleAsync(adminClient);

        var afterCreate = await anonymousClient.GetAsync("/ogc/styles");
        afterCreate.Be200Ok();
        (await ListStyleIdsAsync(afterCreate)).Should().Contain(styleId,
            "creating a style must evict the cached styles list");

        var deleteResponse = await adminClient.DeleteAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterDelete = await anonymousClient.GetAsync("/ogc/styles");
        afterDelete.Be200Ok();
        (await ListStyleIdsAsync(afterDelete)).Should().NotContain(styleId,
            "deleting a style must evict the cached styles list");
    }

    // --- Unknown query parameter rejection (OGC common request validation) ---

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles")]
    public async Task GetStylesList_UnknownQueryParameter_Returns400()
    {
        var client = _fixture.CreateClient();

        var response = await client.GetAsync("/ogc/styles?bogus=1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/styles/{styleId}/metadata")]
    public async Task GetStyleMetadata_UnknownQueryParameter_Returns400()
    {
        var client = _fixture.CreateAdminClient();
        var styleId = await SeedAndResolveStyleIdAsync(client);

        var response = await client.GetAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}/metadata?bogus=1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- Helpers ---

    private const string SimpleRendererDrawingInfoJson = """
    {
      "renderer": {
        "type": "simple",
        "label": "All features",
        "symbol": {
          "type": "esriSMS",
          "style": "esriSMSCircle",
          "color": [255, 0, 0, 255],
          "size": 8,
          "outline": { "color": [0, 0, 0, 255], "width": 1 }
        }
      }
    }
    """;

    // Two picture markers with divergent offsets/angle: converts, but the layout hints
    // cannot be represented uniformly in MapLibre -> PICTURE_MARKER_PARTIAL warning.
    private const string NonUniformPictureMarkerDrawingInfoJson = """
    {
      "renderer": {
        "type": "uniqueValue",
        "field1": "category",
        "uniqueValueInfos": [
          {
            "value": "A",
            "symbol": {
              "type": "esriPMS",
              "url": "https://example.invalid/icon-a.png",
              "imageData": "QQ==",
              "contentType": "image/png",
              "xoffset": 0,
              "yoffset": 0,
              "angle": 0
            }
          },
          {
            "value": "B",
            "symbol": {
              "type": "esriPMS",
              "url": "https://example.invalid/icon-b.png",
              "imageData": "Qg==",
              "contentType": "image/png",
              "xoffset": 12,
              "yoffset": -4,
              "angle": 45
            }
          }
        ]
      }
    }
    """;

    private static async Task<IReadOnlyList<string>> ListStyleIdsAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("styles")
            .EnumerateArray()
            .Select(s => s.GetProperty("id").GetString()!)
            .ToArray();
    }

    private static async Task<string> CreateStandaloneStyleAsync(HttpClient adminClient)
    {
        var styleId = $"depth-{Guid.NewGuid():N}";
        using var content = new StringContent(BuildDefaultStyleJson(), Encoding.UTF8, MapboxStyleMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles") { Content = content };
        request.Headers.TryAddWithoutValidation("X-Style-Id", styleId);

        var response = await adminClient.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return styleId;
    }

    private async Task<string> SeedAndResolveStyleIdAsync(HttpClient adminClient)
    {
        await SeedTestLayerStyleAsync(adminClient);

        var response = await adminClient.GetAsync("/ogc/styles");
        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var styles = document.RootElement.GetProperty("styles");
        styles.GetArrayLength().Should().BeGreaterThan(0);
        return styles[0].GetProperty("id").GetString()!;
    }

    private static async Task SeedTestLayerStyleAsync(HttpClient adminClient)
    {
        var request = new LayerStyleUpdateRequest
        {
            MapLibreStyle = JsonSerializer.Deserialize<JsonElement>(BuildDefaultStyleJson())
        };

        using var content = JsonContent.Create(request, LayerStyleJsonContext.Default.LayerStyleUpdateRequest);
        using var response = await adminClient.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style",
            content);
        response.Be200Ok();
    }

    private static string BuildDefaultStyleJson()
    {
        var layer = new StyleLayerDescriptor(
            WebAppFixture.TestLayerId,
            "Test Layer",
            MetadataV2GeometryType.Point);
        var style = StyleDefaults.BuildDefaultMapLibreStyle(layer);
        return JsonSerializer.Serialize(style);
    }
}
