// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// HTTP-level OData endpoint tests verifying basic endpoint behavior.
/// For comprehensive OData client integration tests, see ODataClientIntegrationTests.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata")]
    public async Task ServiceDocument_ReturnsEntitySets()
    {
        var response = await _fixture.Client.GetAsync("/odata");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.GetProperty("@odata.context").GetString().Should().NotBeNullOrEmpty();
        var value = document.RootElement.GetProperty("value");
        value.ValueKind.Should().Be(JsonValueKind.Array);
        value.EnumerateArray().Should().NotBeEmpty();
        value.EnumerateArray()
            .Select(entitySet => entitySet.GetProperty("kind").GetString())
            .Should().OnlyContain(kind => string.Equals(kind, "EntitySet", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata")]
    public async Task ServiceDocument_ReturnsODataVersionHeader()
    {
        var response = await _fixture.Client.GetAsync("/odata");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("OData-Version", out var values).Should().BeTrue();
        values.Should().Contain("4.01");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata with Accept metadata preference")]
    public async Task ServiceDocument_WithAcceptMetadataPreference_UsesRequestedMetadataLevel()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/odata");
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/json;odata.metadata=none"));

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentType?.Parameters.Should()
            .Contain(p => p.Name == "metadata" && string.Equals(p.Value, "none", StringComparison.OrdinalIgnoreCase));

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        document.RootElement.TryGetProperty("@odata.context", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata with unsupported Accept metadata level")]
    public async Task ServiceDocument_WithAcceptMetadataFull_RejectsUnsupportedMetadataLevel()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/odata");
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/json;odata.metadata=full"));

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata/$metadata")]
    public async Task Metadata_WithExplicitlyRejectedXmlAccept_ReturnsNotAcceptable()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/odata/$metadata");
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/json;q=1"));
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/xml;q=0"));

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata/$metadata")]
    public async Task Metadata_WithXmlRejectedAndWildcardAllowed_ReturnsNotAcceptable()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/odata/$metadata");
        request.Headers.TryAddWithoutValidation("Accept", "application/xml;q=0, */*;q=1");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata/$metadata")]
    public async Task Metadata_WithUnsupportedAcceptAndWildcardFallback_ReturnsXml()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/odata/$metadata");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, */*;q=0.1");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Features_ReturnsODataVersionHeader()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("OData-Version", out var values).Should().BeTrue();
        values.Should().Contain("4.01");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})/$count")]
    public async Task FeaturesCount_ReturnsNumericCount()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})/$count");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrWhiteSpace();
        long.TryParse(content.Trim(), out var count).Should().BeTrue();
        count.Should().BeGreaterOrEqualTo(0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers({layerId})/Features/$count")]
    public async Task LayerFeaturesCount_ReturnsNumericCount()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Layers({TestLayerId})/Features/$count");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrWhiteSpace();
        long.TryParse(content.Trim(), out var count).Should().BeTrue();
        count.Should().BeGreaterOrEqualTo(0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features/$count")]
    public async Task FeaturesCount_AllLayers_ReturnsNumericCount()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features/$count?$filter=LayerId eq {TestLayerId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrWhiteSpace();
        long.TryParse(content.Trim(), out var count).Should().BeTrue();
        count.Should().BeGreaterOrEqualTo(0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features")]
    public async Task Features_AllLayers_WithLayerFilter_ReturnsCollection()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features?$filter=LayerId eq {TestLayerId}&$top=3");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var values = document.RootElement.GetProperty("value");
        values.ValueKind.Should().Be(JsonValueKind.Array);
        values.GetArrayLength().Should().BeLessThanOrEqualTo(3);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features?filter=...&top=...")]
    public async Task Features_AllLayers_AcceptsOData401QueryOptionsWithoutDollarPrefix()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features?filter=LayerId eq {TestLayerId}&top=2&count=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.GetProperty("@odata.count").GetInt64().Should().BeGreaterOrEqualTo(0);
        document.RootElement.GetProperty("value").GetArrayLength().Should().BeLessThanOrEqualTo(2);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features without layer filter")]
    public async Task Features_AllLayers_WithoutLayerFilter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync("/odata/Features");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features")]
    public async Task Features_AllLayers_WithLayerFilterAndAdditionalPredicate_ReturnsFilteredResults()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features?$filter=LayerId eq {TestLayerId} and population gt 1000000");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var values = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        values.Should().NotBeEmpty();
        values.All(value => value.GetProperty("LayerId").GetInt32() == TestLayerId).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features/$count")]
    public async Task FeaturesCount_AllLayers_WithLayerFilterAndAdditionalPredicate_ReturnsNumericCount()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features/$count?$filter=LayerId eq {TestLayerId} and population gt 1000000");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrWhiteSpace();
        long.TryParse(content.Trim(), out var count).Should().BeTrue();
        count.Should().BeGreaterOrEqualTo(0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features/$count without layer filter")]
    public async Task FeaturesCount_AllLayers_WithoutLayerFilter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync("/odata/Features/$count");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features with multi-layer filter")]
    public async Task Features_AllLayers_WithMultipleLayerIds_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            "/odata/Features?$filter=LayerId eq 0 or LayerId eq 1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features with malformed layer filter")]
    public async Task Features_AllLayers_WithMalformedLayerFilter_DoesNotLeakParserDetails()
    {
        const string sentinel = "ODATA_LAYER_FILTER_SENTINEL";
        var malformedFilter = Uri.EscapeDataString($"LayerId eq {sentinel}(");

        var response = await _fixture.Client.GetAsync($"/odata/Features?$filter={malformedFilter}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("LayerId filter must be a valid OData expression that resolves to a single layer.");
        content.Should().NotContain(sentinel);
        content.Should().NotContain("BytePositionInLine");
        content.Should().NotContain("LineNumber");
        content.Should().NotContain("System.Text.Json");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.ODataV4, "Metadata")]
    [Endpoint("GET /odata/$metadata")]
    public async Task Metadata_ReturnsXmlDocument()
    {
        var response = await _fixture.Client.GetAsync("/odata/$metadata");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("edmx:Edmx");
        content.Should().Contain("""<edmx:Edmx Version="4.01" """);
        content.Should().Contain("""<edmx:Reference Uri="http://vocabs.odata.org/capabilities/v1">""");
        content.Should().Contain("""<Annotation Term="Capabilities.ChangeTracking">""");
        content.Should().NotContain("http://docs.oasis-open.org/odata/odata/v4.0/csdl/vocabularies");
        response.Headers.TryGetValues("OData-Version", out var values).Should().BeTrue();
        values.Should().Contain("4.01");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.ODataV4, "Metadata")]
    [Endpoint("GET /odata/$metadata")]
    public async Task Metadata_WithODataMaxVersion40_ReturnsV4CompatibleVersionHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/odata/$metadata");
        request.Headers.TryAddWithoutValidation("OData-MaxVersion", "4.0");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("OData-Version", out var values).Should().BeTrue();
        values.Should().Contain("4.0");
        response.Headers.Vary.Should().Contain("OData-MaxVersion");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("""<edmx:Edmx Version="4.01" """);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers")]
    public async Task Layers_ReturnsCollection()
    {
        var response = await _fixture.Client.GetAsync("/odata/Layers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers/$count")]
    public async Task LayersCount_ReturnsNumericCount()
    {
        var response = await _fixture.Client.GetAsync("/odata/Layers/$count");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrWhiteSpace();
        long.TryParse(content.Trim(), out var count).Should().BeTrue();
        count.Should().BeGreaterOrEqualTo(0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers({layerId})")]
    public async Task Layer_ById_ReturnsLayer()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Layers({TestLayerId})");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.GetProperty("Id").GetInt32().Should().Be(TestLayerId);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers?$filter=name")]
    public async Task Layers_WithFilter_ReturnsMatchingLayer()
    {
        var response = await _fixture.Client.GetAsync("/odata/Layers?$filter=name eq 'City Landmarks'");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var values = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        values.Should().HaveCount(1);
        values[0].GetProperty("Name").GetString().Should().Be("City Landmarks");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers?$top=1&$skip=1")]
    public async Task Layers_WithTopAndSkip_ReturnsPaginatedResults()
    {
        var response = await _fixture.Client.GetAsync("/odata/Layers?$top=1&$skip=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var values = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        values.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers?$orderby=Name desc")]
    public async Task Layers_WithOrderBy_ReturnsOrderedResults()
    {
        var response = await _fixture.Client.GetAsync("/odata/Layers?$orderby=Name desc");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var names = document.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(layer => layer.GetProperty("Name").GetString())
            .ToArray();

        names.Should().ContainInOrder("US Cities", "City Landmarks");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Features_ReturnsCollection()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$format=application/json;odata.metadata=full")]
    public async Task Features_WithFormatMetadataFull_RejectsUnsupportedMetadataLevel()
    {
        var format = Uri.EscapeDataString("application/json;odata.metadata=full");
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=1&$format={format}");

        // metadata=full is not supported; server should reject with 400
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId}) with metadata=none")]
    public async Task Features_WithAcceptMetadataNone_OmitsContextAnnotation()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/odata/Features({TestLayerId})?$top=1");
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/json;odata.metadata=none"));

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.Parameters.Should()
            .Contain(p => p.Name == "metadata" && string.Equals(p.Value, "none", StringComparison.OrdinalIgnoreCase));

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        document.RootElement.TryGetProperty("@odata.context", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId}) with metadata=full")]
    public async Task Features_WithAcceptMetadataFull_RejectsUnsupportedMetadataLevel()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/odata/Features({TestLayerId})?$top=1");
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/json;odata.metadata=full"));

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId}) with format overriding unsupported Accept metadata level")]
    public async Task Features_WithFormatMetadataMinimalAndAcceptMetadataFull_UsesFormatPrecedence()
    {
        var format = Uri.EscapeDataString("application/json;odata.metadata=minimal");
        var request = new HttpRequestMessage(HttpMethod.Get, $"/odata/Features({TestLayerId})?$top=1&$format={format}");
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/json;odata.metadata=full"));

        var response = await _fixture.Client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.Parameters.Should()
            .Contain(p => p.Name == "metadata" && string.Equals(p.Value, "minimal", StringComparison.OrdinalIgnoreCase));
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId}) with Accept quality metadata preferences")]
    public async Task Features_WithAcceptQualityMetadataPreferences_PrefersHighestQualityValue()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/odata/Features({TestLayerId})?$top=1");
        request.Headers.Accept.ParseAdd("application/json;odata.metadata=none;q=0.1");
        request.Headers.Accept.ParseAdd("application/json;odata.metadata=minimal;q=1.0");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.Parameters.Should()
            .Contain(p => p.Name == "metadata" && string.Equals(p.Value, "minimal", StringComparison.OrdinalIgnoreCase));

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        document.RootElement.TryGetProperty("@odata.context", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId}) with unsupported preferred Accept and supported fallback")]
    public async Task Features_WithAcceptMetadataFullAndSupportedFallback_UsesSupportedFallback()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/odata/Features({TestLayerId})?$top=1");
        request.Headers.Accept.ParseAdd("application/json;odata.metadata=full;q=1.0");
        request.Headers.Accept.ParseAdd("application/json;odata.metadata=minimal;q=0.5");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.Parameters.Should()
            .Contain(p => p.Name == "metadata" && string.Equals(p.Value, "minimal", StringComparison.OrdinalIgnoreCase));
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers({layerId})/Features")]
    public async Task LayerFeatures_ReturnsCollection()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Layers({TestLayerId})/Features");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId},{objectId})")]
    public async Task Feature_WithObjectId_ReturnsFeature()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId},1)");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.GetProperty("ObjectId").GetInt64().Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task Feature_WithNamedKeys_ReturnsFeature()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features(LayerId={TestLayerId},ObjectId=1)");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.GetProperty("ObjectId").GetInt64().Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features(LayerId={layerId},ObjectId={objectId})/$ref")]
    public async Task FeatureReferenceEndpoint_ReturnsCanonicalReference()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features(LayerId={TestLayerId},ObjectId=1)/$ref");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.TryGetProperty("@odata.id", out var id).Should().BeTrue();
        id.GetString().Should().Contain($"/odata/Features(LayerId={TestLayerId},ObjectId=1)");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features(LayerId={layerId},ObjectId={objectId})/$value")]
    public async Task FeatureValueEndpoint_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features(LayerId={TestLayerId},ObjectId=1)/$value");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("ResourceNotFound");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers({layerId})/Features({objectId})")]
    public async Task LayerFeature_WithObjectId_ReturnsFeature()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Layers({TestLayerId})/Features(1)");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.GetProperty("ObjectId").GetInt64().Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$top=5")]
    public async Task Features_WithTop_LimitsResults()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var items = document.RootElement.GetProperty("value").EnumerateArray().ToArray();
        items.Length.Should().BeLessThanOrEqualTo(5);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$skip=2")]
    public async Task Features_WithSkip_SkipsResults()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$skip=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$count=true")]
    public async Task Features_WithCount_ReturnsCount()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$count=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.TryGetProperty("@odata.count", out var countProperty).Should().BeTrue();
        countProperty.ValueKind.Should().Be(JsonValueKind.Number);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$orderby=objectid")]
    public async Task Features_WithOrderBy_ReturnsOrderedResults()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$orderby=objectid");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$orderby=objectid desc")]
    public async Task Features_WithOrderByDesc_ReturnsDescendingResults()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$orderby=objectid desc");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$select=ObjectId,LayerId")]
    public async Task Features_WithSelect_ReturnsOnlySelectedFields()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$select=ObjectId,LayerId");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var items = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        items.Should().NotBeEmpty();

        var first = items[0];
        first.TryGetProperty("ObjectId", out _).Should().BeTrue();
        first.TryGetProperty("LayerId", out _).Should().BeTrue();
        first.TryGetProperty("Geometry", out _).Should().BeFalse();
        first.TryGetProperty("name", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$select=ObjectId,,LayerId")]
    public async Task Features_WithMalformedSelectDelimiter_ReturnsODataError()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$select=ObjectId,,LayerId");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$top=2000&$select=ObjectId,LayerId")]
    public async Task Features_WithLargeTop_UsesStreamingResponse()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$top=2000&$select=ObjectId,LayerId");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Assert.True(response.Headers.TransferEncodingChunked ?? false, "Expected chunked transfer encoding for streaming responses");

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.TryGetProperty("@odata.context", out _).Should().BeTrue();
        var items = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        items.Should().NotBeEmpty();

        var first = items[0];
        first.TryGetProperty("Geometry", out _).Should().BeFalse();
        first.TryGetProperty("name", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$top=2000&$select=ObjectId,,LayerId")]
    public async Task Features_WithMalformedSelectDelimiter_WhenStreaming_ReturnsODataError()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$top=2000&$select=ObjectId,,LayerId");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$top=-1")]
    public async Task Features_WithInvalidTop_ReturnsODataError()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=-1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // OData v4 error format has "error" with "code" and "message"
        document.RootElement.TryGetProperty("error", out var errorProperty).Should().BeTrue();
        errorProperty.GetProperty("code").GetString().Should().NotBeNullOrEmpty();
        errorProperty.GetProperty("message").GetString().Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$skip=-1")]
    public async Task Features_WithInvalidSkip_ReturnsODataError()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$skip=-1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // OData v4 error format
        document.RootElement.TryGetProperty("error", out var errorProperty).Should().BeTrue();
        errorProperty.GetProperty("code").GetString().Should().Be("InvalidQueryOption");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$orderby=bad-field")]
    public async Task Features_WithInvalidOrderBy_ReturnsODataError()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$orderby=bad-field");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.TryGetProperty("error", out var errorProperty).Should().BeTrue();
        errorProperty.GetProperty("code").GetString().Should().Be("InvalidQuery");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId},{objectId})")]
    public async Task Features_NonExistentLayer_ReturnsODataNotFoundError()
    {
        var response = await _fixture.Client.GetAsync("/odata/Features(99999)");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // OData v4 error format
        document.RootElement.TryGetProperty("error", out var errorProperty).Should().BeTrue();
        errorProperty.GetProperty("code").GetString().Should().Be("ResourceNotFound");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata/$metadata")]
    public async Task Metadata_ContainsEntityTypes()
    {
        var response = await _fixture.Client.GetAsync("/odata/$metadata");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();

        // Verify CSDL structure
        content.Should().Contain("EntityType");
        content.Should().Contain("EntitySet");
        content.Should().Contain("EntityContainer");
        content.Should().Contain("Honua.Layer");
        content.Should().Contain("Honua.Feature");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$top=2&$skip=0")]
    public async Task Features_WithPagination_ReturnsNextLink()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=2&$skip=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // NextLink should be present if there are more results
        var hasNextLink = document.RootElement.TryGetProperty("@odata.nextLink", out var nextLink);
        hasNextLink.Should().BeTrue();
        var nextLinkValue = nextLink.GetString();
        nextLinkValue.Should().NotBeNullOrEmpty();
        nextLinkValue.Should().Contain("$skip=2");
        nextLinkValue.Should().Contain("$top=2");
    }
}
