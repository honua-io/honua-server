// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerMaintenanceTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Append)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/append")]
    public async Task ServiceAppend_ValidRequest_ReturnsSuccess()
    {
        var edits = JsonSerializer.Serialize(new[]
        {
            new { attributes = new { name = "Test Feature" } }
        });

        var payload = JsonSerializer.Serialize(new
        {
            edits,
            sourceFormat = "json",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/append",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.TryGetProperty("numFeaturesAppended", out _).Should().BeTrue();
        root.TryGetProperty("numFeaturesFailed", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Append)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/append")]
    public async Task ServiceAppend_MissingEdits_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new { f = "json" });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/append",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("edits");
    }

    [IntegrationTest]
    [Operation(Operations.Append)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/append")]
    public async Task ServiceAppend_InvalidService_ReturnsNotFound()
    {
        var edits = JsonSerializer.Serialize(new[]
        {
            new { attributes = new { name = "Test" } }
        });

        var payload = JsonSerializer.Serialize(new
        {
            edits,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            "/rest/services/nonexistent/FeatureServer/append",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Append)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/append")]
    public async Task LayerAppend_ValidRequest_ReturnsSuccess()
    {
        var edits = JsonSerializer.Serialize(new[]
        {
            new { attributes = new { name = "Test Feature" } }
        });

        var payload = JsonSerializer.Serialize(new
        {
            edits,
            sourceFormat = "json",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/append",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Append)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/append")]
    public async Task LayerAppend_MissingEdits_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new { f = "json" });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/append",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("edits");
    }

    [IntegrationTest]
    [Operation(Operations.Append)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/append")]
    public async Task LayerAppend_InvalidEditsJson_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new
        {
            edits = "not-valid-json{{{",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/append",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Calculate)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    public async Task Calculate_ValidRequest_ReturnsSuccess()
    {
        var calcExpression = JsonSerializer.Serialize(new[]
        {
            new { field = "name", sqlExpression = "'Updated'" }
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/calculate?calcExpression={Uri.EscapeDataString(calcExpression)}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.TryGetProperty("updatedFeatureCount", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Calculate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    public async Task Calculate_PostFormBody_ReturnsSuccess()
    {
        var calcExpression = JsonSerializer.Serialize(new[]
        {
            new { field = "name", sqlExpression = "'Updated From Post'" }
        });
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("calcExpression", calcExpression),
            new KeyValuePair<string, string>("f", "json")
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/calculate",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Calculate)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    public async Task Calculate_MissingCalcExpression_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/calculate?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("calcExpression");
    }

    [IntegrationTest]
    [Operation(Operations.Calculate)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    public async Task Calculate_InvalidCalcExpression_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/calculate?calcExpression=not-json&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.QueryDomains)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/queryDomains")]
    public async Task QueryDomains_ValidService_ReturnsDomainsArray()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/queryDomains?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("domains", out var domains).Should().BeTrue();
        domains.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.QueryDomains)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/queryDomains")]
    public async Task QueryDomains_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/queryDomains?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelationships)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/relationships")]
    public async Task QueryRelationships_ValidService_ReturnsRelationshipsArray()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/relationships?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("relationships", out var relationships).Should().BeTrue();
        relationships.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelationships)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/relationships")]
    public async Task QueryRelationships_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/relationships?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL")]
    public async Task ValidateSql_ValidExpression_ReturnsIsValid()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/validateSQL?where=1%3D1&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("isValidSQL").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL")]
    public async Task ValidateSql_InvalidExpression_ReturnsInvalid()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/validateSQL?where=INVALID%20%25%25%25&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("isValidSQL").GetBoolean().Should().BeFalse();
        root.TryGetProperty("validationError", out var validationError).Should().BeTrue();
        validationError.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL")]
    public async Task ValidateSql_MissingSqlClause_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/validateSQL?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("sql");
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL")]
    public async Task ValidateSql_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/0/validateSQL?where=1%3D1&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Regression for #1446: validateSQL must accept the Esri `sql` parameter
    // (with optional `sqlType`) and support both GET and a POST companion route.
    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL")]
    public async Task ValidateSql_SqlParameterWithSqlType_ReturnsIsValid()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/validateSQL?sql=1%3D1&sqlType=standard&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("isValidSQL").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL")]
    public async Task ValidateSql_InvalidSqlType_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/validateSQL?sql=1%3D1&sqlType=bogus&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL")]
    public async Task ValidateSql_PostSqlParameter_ReturnsIsValid()
    {
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("sql", "1=1"),
            new KeyValuePair<string, string>("sqlType", "native"),
            new KeyValuePair<string, string>("f", "json"),
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/validateSQL",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("isValidSQL").GetBoolean().Should().BeTrue();
    }
}
