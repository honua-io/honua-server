// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OData;

/// <summary>
/// Comprehensive OData v4 error handling tests for invalid filters, unsupported functions,
/// malformed geometry, and other error scenarios.
/// Implements parity with OGC API Features test matrices per issue #200.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataErrorHandlingTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;
    private const string PendingErrorHandling = "Pending OData error handling parity (#200).";

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region Invalid $filter Syntax

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=invalid")]
    public async Task Filter_InvalidSyntax_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter=invalid_syntax");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response, "InvalidQuery");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter= eq 1")]
    public async Task Filter_MissingLeftOperand_ReturnsBadRequest()
    {
        var filter = " eq 1";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=ObjectId eq")]
    public async Task Filter_MissingRightOperand_ReturnsBadRequest()
    {
        var filter = "ObjectId eq";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=ObjectId badop 1")]
    public async Task Filter_InvalidOperator_ReturnsBadRequest()
    {
        var filter = "ObjectId badop 1";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    [IntegrationTest(Skip = PendingErrorHandling)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=(ObjectId eq 1")]
    public async Task Filter_UnbalancedParentheses_ReturnsBadRequest()
    {
        var filter = "(ObjectId eq 1";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=name eq 'unclosed")]
    public async Task Filter_UnclosedStringLiteral_ReturnsBadRequest()
    {
        var filter = "name eq 'unclosed";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    [IntegrationTest(Skip = PendingErrorHandling)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=nonexistent_field eq 'value'")]
    public async Task Filter_NonExistentField_ReturnsBadRequest()
    {
        var filter = "nonexistent_field eq 'value'";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    #endregion

    #region Invalid $top and $skip Values

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$top=-1")]
    public async Task Top_NegativeValue_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=-1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response, "InvalidQueryOption");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$skip=-1")]
    public async Task Skip_NegativeValue_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$skip=-1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response, "InvalidQueryOption");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$top=abc")]
    public async Task Top_NonNumericValue_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=abc");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$skip=abc")]
    public async Task Skip_NonNumericValue_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$skip=abc");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    #endregion

    #region Invalid $orderby

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$orderby=invalid-field")]
    public async Task OrderBy_InvalidField_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$orderby=invalid-field");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response, "InvalidQuery");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$orderby=population invalid")]
    public async Task OrderBy_InvalidDirection_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$orderby=population invalid");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    #endregion

    #region Unsupported Functions

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=substring(name, 1, 3) eq 'San'")]
    public async Task Filter_UnsupportedStringFunction_ReturnsBadRequest()
    {
        var filter = "substring(name, 1, 3) eq 'San'";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=year(created_at) eq 2020")]
    public async Task Filter_UnsupportedDateFunction_ReturnsBadRequest()
    {
        var filter = "year(created_at) eq 2020";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=tolower(name) eq 'san francisco'")]
    public async Task Filter_UnsupportedCaseFunction_ReturnsBadRequest()
    {
        var filter = "tolower(name) eq 'san francisco'";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=concat(name, state) eq 'test'")]
    public async Task Filter_UnsupportedConcatFunction_ReturnsBadRequest()
    {
        var filter = "concat(name, state) eq 'test'";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    #endregion

    #region Malformed Geometry

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.intersects(Geometry, geography'INVALID')")]
    public async Task GeoIntersects_MalformedGeometry_ReturnsBadRequest()
    {
        var filter = "geo.intersects(Geometry, geography'INVALID')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.intersects(Geometry, geography'POLYGON((0 0, 1 0))')")]
    public async Task GeoIntersects_IncompletePolygon_ReturnsBadRequest()
    {
        // Polygon with only 2 points - invalid
        var filter = "geo.intersects(Geometry, geography'POLYGON((0 0, 1 0))')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.intersects(Geometry, geography'POLYGON((0 0, 1 1, 2 0, 0 1))')")]
    public async Task GeoIntersects_UnclosedPolygon_ReturnsBadRequest()
    {
        // Polygon that doesn't close (first and last point should be same)
        var filter = "geo.intersects(Geometry, geography'POLYGON((0 0, 1 1, 2 0, 0 1))')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.distance(Geometry, geography'POINT') lt 100")]
    public async Task GeoDistance_MalformedPoint_ReturnsBadRequest()
    {
        var filter = "geo.distance(Geometry, geography'POINT') lt 100";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.distance(Geometry, geography'POINT(abc def)') lt 100")]
    public async Task GeoDistance_NonNumericCoordinates_ReturnsBadRequest()
    {
        var filter = "geo.distance(Geometry, geography'POINT(abc def)') lt 100";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    #endregion

    #region Invalid CRUD Payloads

    [IntegrationTest(Skip = PendingErrorHandling)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /odata/Features({layerId}) with invalid JSON")]
    public async Task Create_InvalidJson_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PostAsync(
            $"/odata/Features({TestLayerId})",
            new StringContent("{ invalid json }", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /odata/Features({layerId}) with invalid geometry WKB")]
    public async Task Create_InvalidGeometryWkb_ReturnsBadRequest()
    {
        var payload = new
        {
            Geometry = "not-valid-base64-wkb",
            Attributes = new { name = "Test" }
        };

        var json = JsonSerializer.Serialize(payload);
        var response = await _fixture.Client.PostAsync(
            $"/odata/Features({TestLayerId})",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest(Skip = PendingErrorHandling)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("PATCH /odata/Features({layerId},{objectId}) with invalid JSON")]
    public async Task Update_InvalidJson_ReturnsBadRequest()
    {
        var message = new HttpRequestMessage(new HttpMethod("PATCH"), $"/odata/Features({TestLayerId},1)")
        {
            Content = new StringContent("{ invalid json }", Encoding.UTF8, "application/json")
        };

        var response = await _fixture.Client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region Resource Not Found

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId}) nonexistent layer")]
    public async Task GetFeatures_NonExistentLayer_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync("/odata/Features(99999)");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AssertODataErrorAsync(response, "ResourceNotFound");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId},{objectId}) nonexistent feature")]
    public async Task GetFeature_NonExistent_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId},999999)");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AssertODataErrorAsync(response, "ResourceNotFound");
    }

    #endregion

    #region Type Mismatch Errors

    [IntegrationTest(Skip = PendingErrorHandling)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=population eq 'not_a_number'")]
    public async Task Filter_TypeMismatchNumeric_ReturnsBadRequest()
    {
        var filter = "population eq 'not_a_number'";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    [IntegrationTest(Skip = PendingErrorHandling)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=is_capital eq 'not_a_bool'")]
    public async Task Filter_TypeMismatchBoolean_ReturnsBadRequest()
    {
        var filter = "is_capital eq 'not_a_bool'";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    #endregion

    #region $apply Aggregation Errors

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})/$apply without $apply param")]
    public async Task Apply_MissingApplyParam_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})/$apply");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response, "InvalidQueryOption");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})/$apply?$apply=invalid")]
    public async Task Apply_InvalidExpression_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$apply?$apply=aggregate(population as Total)");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response, "InvalidQueryOption");
    }

    [IntegrationTest(Skip = PendingErrorHandling)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})/$apply?$apply=aggregate(nonexistent with sum as Total)")]
    public async Task Apply_NonExistentField_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$apply?$apply=aggregate(nonexistent_field with sum as Total)");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    #endregion

    #region $search Errors

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})/$search without $search param")]
    public async Task Search_MissingSearchParam_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})/$search");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response, "InvalidQueryOption");
    }

    #endregion

    #region Batch Errors

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /odata/$batch with invalid JSON")]
    public async Task Batch_InvalidJson_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PostAsync(
            "/odata/$batch",
            new StringContent("{ invalid json }", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /odata/$batch with missing requests")]
    public async Task Batch_MissingRequests_ReturnsBadRequest()
    {
        var payload = new { notRequests = "missing" };
        var json = JsonSerializer.Serialize(payload);

        var response = await _fixture.Client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest(Skip = PendingErrorHandling)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /odata/$batch with invalid URL in request")]
    public async Task Batch_InvalidRequestUrl_ReturnsErrorInResponse()
    {
        var payload = new
        {
            requests = new[]
            {
                new
                {
                    id = "1",
                    method = "GET",
                    url = "InvalidUrl"
                }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var response = await _fixture.Client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var responses = document.RootElement.GetProperty("responses");
        var firstResponse = responses[0];
        firstResponse.GetProperty("status").GetInt32().Should().Be(400);
    }

    #endregion

    #region OData Error Format Validation

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("OData error format validation")]
    public async Task Error_HasCorrectODataV4Format()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=-1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // OData v4 error format must have "error" object
        document.RootElement.TryGetProperty("error", out var errorElement).Should().BeTrue();

        // "error" must have "code" and "message"
        errorElement.TryGetProperty("code", out var codeElement).Should().BeTrue();
        codeElement.GetString().Should().NotBeNullOrEmpty();

        errorElement.TryGetProperty("message", out var messageElement).Should().BeTrue();
        messageElement.GetString().Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Helper Methods

    private static async Task AssertODataErrorAsync(HttpResponseMessage response, string? expectedCode = null)
    {
        var content = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }
        using var document = JsonDocument.Parse(content);

        document.RootElement.TryGetProperty("error", out var errorElement).Should().BeTrue(
            $"Response should contain OData error format. Response: {content}");

        if (expectedCode != null)
        {
            errorElement.TryGetProperty("code", out var codeElement).Should().BeTrue();
            codeElement.GetString().Should().Be(expectedCode);
        }
    }

    #endregion
}
