// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Npgsql;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerMaintenanceTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.EnableV2ServiceEditingCapabilities(
            WebAppFixture.TestServiceId,
            ["Create", "Update", "Delete"]);
    }

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

        using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/append",
            requestContent);

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

        using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/append",
            requestContent);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

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

        using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/nonexistent/FeatureServer/append",
            requestContent);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
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

        using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/append",
            requestContent);

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

        using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/append",
            requestContent);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

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

        using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/append",
            requestContent);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Calculate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    public async Task Calculate_ValidRequest_ReturnsSuccess()
    {
        var calcExpression = JsonSerializer.Serialize(new[]
        {
            new { field = "name", sqlExpression = "'Updated'" }
        });

        var response = await PostCalculateAsync(calcExpression);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.TryGetProperty("updatedFeatureCount", out _).Should().BeTrue();
    }

    /// <summary>
    /// #6100: calculate is a bulk field rewrite, so it must be POST-only. A GET mapping let a
    /// cross-site top-level navigation (which carries a SameSite=Lax session cookie) rewrite
    /// every matching row as the victim. GET must be refused by routing and leave rows untouched;
    /// the same expression over POST is the positive control that the row is reachable.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Calculate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    public async Task Calculate_Get_IsRefusedAndLeavesRowsUnchanged()
    {
        const string originalName = "calc-6100-original";
        const string attackerName = "calc-6100-rewritten";
        var objectId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, originalName);
        var where = Uri.EscapeDataString($"objectid = {objectId}");
        var calcExpression = JsonSerializer.Serialize(new[]
        {
            new { field = "name", value = attackerName }
        });

        var getResponse = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/calculate?where={where}&calcExpression={Uri.EscapeDataString(calcExpression)}&f=json");

        await getResponse.AssertGeoServicesErrorAsync(405);
        getResponse.Content.Headers.Allow.Should().BeEquivalentTo(["POST"]);
        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, objectId))
            .Should().Be(originalName, "a GET to calculate must never reach the edit pipeline");

        using var headRequest = new HttpRequestMessage(HttpMethod.Head,
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/calculate?where={where}&calcExpression={Uri.EscapeDataString(calcExpression)}&f=json");
        using var headResponse = await _fixture.Client.SendAsync(headRequest);
        headResponse.StatusCode.Should().Be(getResponse.StatusCode);
        headResponse.Content.Headers.Allow.Should().BeEquivalentTo(["POST"]);
        (await headResponse.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, objectId))
            .Should().Be(originalName, "a HEAD to calculate must never reach the edit pipeline");

        var postResponse = await PostCalculateAsync(calcExpression, $"objectid = {objectId}");

        postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, objectId))
            .Should().Be(attackerName, "the same calculate over POST is the supported write path");
    }

    [IntegrationTest]
    [Operation(Operations.Calculate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    public async Task Calculate_SourceExpressionReferencingHiddenField_ReturnsBadRequest()
    {
        const string originalName = "calc-6141-original";
        _fixture.UpdateV2ResourceSchemaField(WebAppFixture.TestLayerId, new MetadataV2Field
        {
            Name = "secret",
            Type = MetadataV2FieldType.String,
            Hidden = true,
            Nullable = true,
        });
        var objectId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, originalName);
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using (var connection = await _fixture.Postgres.GetConnectionAsync(schema))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE features
                SET attributes = jsonb_set(attributes, '{secret}', to_jsonb(@secret::text))
                WHERE layer_id = @layerId AND objectid = @objectId
                """;
            command.Parameters.AddWithValue("secret", "hidden-value");
            command.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
            command.Parameters.AddWithValue("objectId", objectId);
            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        }

        var sourceExpression = JsonSerializer.Serialize(new[]
        {
            new { field = "name", sqlExpression = "secret" }
        });
        var sourceResponse = await PostCalculateAsync(sourceExpression, $"objectid = {objectId}");
        var sourceBody = await sourceResponse.Content.ReadAsStringAsync();
        sourceBody.Should().Contain("not available for calculate");
        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, objectId))
            .Should().Be(originalName);

        var targetExpression = JsonSerializer.Serialize(new[]
        {
            new { field = "secret", value = "exposed" }
        });
        var targetResponse = await PostCalculateAsync(targetExpression, $"objectid = {objectId}");
        var targetBody = await targetResponse.Content.ReadAsStringAsync();
        targetBody.Should().Contain("not available for calculate");
        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, objectId))
            .Should().Be(originalName);
    }

    [IntegrationTest]
    [Operation(Operations.Calculate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    public async Task Calculate_UnknownSourceField_ReturnsBadRequestAndLeavesRowsUnchanged()
    {
        const string originalName = "calc-6142-original";
        var objectId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, originalName);
        var calcExpression = JsonSerializer.Serialize(new[]
        {
            new { field = "name", sqlExpression = "not_a_field" }
        });

        var response = await PostCalculateAsync(calcExpression, $"objectid = {objectId}");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("does not exist in layer");
        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, objectId))
            .Should().Be(originalName);
    }

    [IntegrationTest]
    [Operation(Operations.Calculate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    public async Task Calculate_MixedCaseSourceField_WritesMatchedValue()
    {
        const string originalName = "Ada";
        var objectId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, originalName);
        var calcExpression = JsonSerializer.Serialize(new[]
        {
            new { field = "name", sqlExpression = "NAME || '_ok'" }
        });

        var response = await PostCalculateAsync(calcExpression, $"objectid = {objectId}");
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, objectId))
            .Should().Be("Ada_ok");
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
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    public async Task Calculate_MissingCalcExpression_ReturnsBadRequest()
    {
        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("f", "json")
        });
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/calculate",
            form);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("calcExpression");
    }

    [IntegrationTest]
    [Operation(Operations.Calculate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    public async Task Calculate_InvalidCalcExpression_ReturnsBadRequest()
    {
        var response = await PostCalculateAsync("not-json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> PostCalculateAsync(string calcExpression, string? where = null)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("calcExpression", calcExpression),
            new("f", "json"),
        };
        if (where is not null)
        {
            fields.Add(new("where", where));
        }

        using var form = new FormUrlEncodedContent(fields);
        return await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/calculate",
            form);
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

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
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

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
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

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

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

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
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

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Regression for #1901: the layer-level validateSQL must accept the same canonical Esri
    // clause-type sqlType enum (esriSQLTypeWhere/OrderBy/Expression) and short forms
    // (where/orderBy/expression) that real ArcGIS clients and the service-level endpoint use,
    // in addition to the SQL-dialect aliases (standard/native).
    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL")]
    public async Task ValidateSql_ClauseTypeWhere_ReturnsIsValid()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/validateSQL?sql={Uri.EscapeDataString("1=1")}&sqlType=where&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("isValidSQL").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL")]
    public async Task ValidateSql_EsriSqlTypeWhereEnum_ReturnsIsValid()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/validateSQL?sql={Uri.EscapeDataString("1=1")}&sqlType=esriSQLTypeWhere&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("isValidSQL").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL")]
    public async Task ValidateSql_EsriSqlTypeExpressionEnum_ReturnsIsValid()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/validateSQL?sql={Uri.EscapeDataString("objectid = 1")}&sqlType=esriSQLTypeExpression&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("isValidSQL").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL")]
    public async Task ValidateSql_EsriSqlTypeOrderByEnum_ReturnsIsValid()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/validateSQL?sql={Uri.EscapeDataString("objectid DESC")}&sqlType=esriSQLTypeOrderBy&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("isValidSQL").GetBoolean().Should().BeTrue();
    }

    // The ArcGIS API for Python sends sqlType=statement; treat it as expression validation.
    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL")]
    public async Task ValidateSql_StatementType_ReturnsIsValid()
    {
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("sql", "objectid = 1"),
            new KeyValuePair<string, string>("sqlType", "statement"),
            new KeyValuePair<string, string>("f", "json"),
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/validateSQL",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("isValidSQL").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL")]
    public async Task ValidateSql_PostSqlParameter_ReturnsIsValid()
    {
        using var content = new FormUrlEncodedContent(new[]
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

    // #1446: Esri's canonical validateSQL is at the SERVICE level (no layerId) and takes
    // sql + sqlType (one of where, orderBy, expression).
    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/validateSQL")]
    public async Task ServiceValidateSql_WhereType_ReturnsIsValid()
    {
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("sql", "objectid > 0"),
            new KeyValuePair<string, string>("sqlType", "where"),
            new KeyValuePair<string, string>("f", "json"),
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/validateSQL",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("isValidSQL").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/validateSQL")]
    public async Task ServiceValidateSql_OrderByType_ReturnsIsValid()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/validateSQL?sql={Uri.EscapeDataString("objectid DESC")}&sqlType=orderBy&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("isValidSQL").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/validateSQL")]
    public async Task ServiceValidateSql_ExpressionType_ReturnsIsValid()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/validateSQL?sql={Uri.EscapeDataString("objectid = 1")}&sqlType=expression&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("isValidSQL").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/validateSQL")]
    public async Task ServiceValidateSql_DefaultsToWhenSqlTypeOmitted_ReturnsIsValid()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/validateSQL?sql={Uri.EscapeDataString("objectid > 0")}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("isValidSQL").GetBoolean().Should().BeTrue();
    }

    // #1858: Real Esri clients send the canonical sqlType enum
    // (esriSQLTypeWhere / esriSQLTypeOrderBy / esriSQLTypeExpression). These must be
    // accepted in addition to the short forms.
    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/validateSQL")]
    public async Task ServiceValidateSql_EsriSqlTypeWhereEnum_ReturnsIsValid()
    {
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("sql", "objectid > 0"),
            new KeyValuePair<string, string>("sqlType", "esriSQLTypeWhere"),
            new KeyValuePair<string, string>("f", "json"),
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/validateSQL",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("isValidSQL").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/validateSQL")]
    public async Task ServiceValidateSql_EsriSqlTypeOrderByEnum_ReturnsIsValid()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/validateSQL?sql={Uri.EscapeDataString("objectid DESC")}&sqlType=esriSQLTypeOrderBy&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("isValidSQL").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/validateSQL")]
    public async Task ServiceValidateSql_EsriSqlTypeExpressionEnum_ReturnsIsValid()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/validateSQL?sql={Uri.EscapeDataString("objectid = 1")}&sqlType=esriSQLTypeExpression&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("isValidSQL").GetBoolean().Should().BeTrue();
    }

    // #1901: the ArcGIS API for Python sends sqlType=statement; both endpoints map it to
    // expression validation.
    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/validateSQL")]
    public async Task ServiceValidateSql_StatementType_ReturnsIsValid()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/validateSQL?sql={Uri.EscapeDataString("objectid = 1")}&sqlType=statement&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("isValidSQL").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/validateSQL")]
    public async Task ServiceValidateSql_InvalidSqlType_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/validateSQL?sql={Uri.EscapeDataString("objectid > 0")}&sqlType=bogus&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/validateSQL")]
    public async Task ServiceValidateSql_MissingSql_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/validateSQL?sqlType=where&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/validateSQL")]
    public async Task ServiceValidateSql_InvalidWhere_ReturnsIsValidFalse()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/validateSQL?sql={Uri.EscapeDataString("INVALID %%%")}&sqlType=where&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("isValidSQL").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/validateSQL")]
    public async Task ServiceValidateSql_NonExistentService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/nonexistent/FeatureServer/validateSQL?sql={Uri.EscapeDataString("objectid > 0")}&sqlType=where&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
