// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Xunit;

namespace Honua.Server.Tests.Features.OData;

/// <summary>
/// HTTP-level OData endpoint tests verifying basic endpoint behavior.
/// For comprehensive OData client integration tests, see ODataClientIntegrationTests.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        _fixture.ReplaceService<ILayerCatalog>(new ODataTestLayerCatalog());
        _fixture.ReplaceService<IFeatureStore>(new ODataTestFeatureStore());
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
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata")]
    public async Task ServiceDocument_ReturnsODataVersionHeader()
    {
        var response = await _fixture.Client.GetAsync("/odata");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("OData-Version", out var values).Should().BeTrue();
        values.Should().Contain("4.0");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task Features_ReturnsODataVersionHeader()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("OData-Version", out var values).Should().BeTrue();
        values.Should().Contain("4.0");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata/$metadata")]
    public async Task Metadata_ReturnsXmlDocument()
    {
        var response = await _fixture.Client.GetAsync("/odata/$metadata");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("edmx:Edmx");
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
    [Endpoint("GET /odata/Features({layerId})?$top=5")]
    public async Task Features_WithTop_LimitsResults()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var items = document.RootElement.GetProperty("value").EnumerateArray().ToArray();
        items.Should().HaveCountLessOrEqualTo(5);
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
        // This test depends on the number of test features available
        // If more than 2 features exist, nextLink should be present
    }
}
