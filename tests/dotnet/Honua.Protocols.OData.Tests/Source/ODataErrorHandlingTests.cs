// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Protocols.OData.Models;
using Honua.TestKit.Helpers;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// Comprehensive OData v4 error handling tests for invalid filters, unsupported functions,
/// malformed geometry, and other error scenarios.
/// Implements parity with OGC API Features test matrices per issue #200.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataErrorHandlingTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;

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

    [IntegrationTest]
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

    [IntegrationTest]
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

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?unsupported=1")]
    public async Task UnknownQueryParameter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?unsupported=1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response, "InvalidQueryOption");
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

        var message = await GetODataErrorMessageAsync(response);
        message.Should().Contain("Invalid field name in $orderby");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$orderby=population invalid")]
    public async Task OrderBy_InvalidDirection_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$orderby=population invalid");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);

        var message = await GetODataErrorMessageAsync(response);
        message.Should().Contain("Invalid sort direction in $orderby");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$orderby=population asc nulls last")]
    public async Task OrderBy_WithExtraTokens_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$orderby=population asc nulls last");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response, "InvalidQuery");
    }

    #endregion

    #region Supported Functions

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=substring(name, 0, 3) eq 'San'")]
    public async Task Filter_SubstringFunction_ReturnsMatches()
    {
        var filter = "substring(name, 0, 3) eq 'San'";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(3);
        foreach (var feature in features)
        {
            var attrs = ParseAttributes(feature);
            attrs.GetProperty("name").GetString().Should().StartWith("San");
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=year(datetime'2020-01-01T00:00:00Z') eq 2020")]
    public async Task Filter_YearFunction_ReturnsMatches()
    {
        var filter = "year(datetime'2020-01-01T00:00:00Z') eq 2020";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(15);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=tolower(name) eq 'san francisco'")]
    public async Task Filter_ToLowerFunction_ReturnsMatches()
    {
        var filter = "tolower(name) eq 'san francisco'";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().ContainSingle();
        var attrs = ParseAttributes(features[0]);
        attrs.GetProperty("name").GetString().Should().Be("San Francisco");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=concat(name, state) eq 'San FranciscoCalifornia'")]
    public async Task Filter_ConcatFunction_ReturnsMatches()
    {
        var filter = "concat(name, state) eq 'San FranciscoCalifornia'";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().ContainSingle();
        var attrs = ParseAttributes(features[0]);
        attrs.GetProperty("name").GetString().Should().Be("San Francisco");
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

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /odata/Layers({layerId})/Features with invalid JSON")]
    public async Task Create_InvalidJson_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PostAsync(
            $"/odata/Layers({TestLayerId})/Features",
            new StringContent("{ invalid json }", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /odata/Layers({layerId})/Features with invalid geometry WKB")]
    public async Task Create_InvalidGeometryWkb_ReturnsBadRequest()
    {
        var payload = new
        {
            Geometry = new
            {
                type = "Point",
                coordinates = "invalid"
            },
            Attributes = new { name = "Test" }
        };

        var json = JsonSerializer.Serialize(payload);
        var response = await _fixture.Client.PostAsync(
            $"/odata/Layers({TestLayerId})/Features",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /odata/Layers({layerId})/Features with invalid geometry coordinate type")]
    public async Task Create_InvalidGeometryCoordinateType_ReturnsBadRequest()
    {
        var payload = new
        {
            Geometry = new
            {
                type = "Point",
                coordinates = 123
            },
            Attributes = new { name = "Test" }
        };

        var json = JsonSerializer.Serialize(payload);
        var response = await _fixture.Client.PostAsync(
            $"/odata/Layers({TestLayerId})/Features",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /odata/Layers({layerId})/Features with invalid geometry payload details hidden")]
    public async Task Create_InvalidGeometryPayload_DoesNotLeakParserDetails()
    {
        const string sentinel = "SENTINEL_GEOMETRY_PAYLOAD";
        var payload = new
        {
            Geometry = new
            {
                type = "Point",
                coordinates = sentinel
            },
            Attributes = new { name = "Test" }
        };

        var json = JsonSerializer.Serialize(payload);
        var response = await _fixture.Client.PostAsync(
            $"/odata/Layers({TestLayerId})/Features",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response, "BadRequest");

        var message = await GetODataErrorMessageAsync(response);
        message.Should().Be("Invalid geometry payload.");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotContain(sentinel);
        content.Should().NotContain("System.Text.Json");
        content.Should().NotContain("BytePositionInLine");
        content.Should().NotContain("LineNumber");
    }

    [IntegrationTest]
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

    [IntegrationTest]
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

    [IntegrationTest]
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

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})/$apply?$apply=aggregate(nonexistent with sum as Total)")]
    public async Task Apply_NonExistentField_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$apply?$apply=aggregate(nonexistent_field with sum as Total)");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})/$apply?$apply=groupby((field,,field),aggregate(...))")]
    public async Task Apply_GroupBy_WithMalformedDelimiter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$apply?$apply=groupby((state,,state),aggregate(population with sum as TotalPop))");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})/$apply?$apply=aggregate(...,)")]
    public async Task Apply_Aggregate_WithTrailingDelimiter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$apply?$apply=aggregate(population with sum as TotalPop,)");

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
    [Endpoint("POST /odata/$batch with unsupported content type")]
    public async Task Batch_UnsupportedContentType_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PostAsync(
            "/odata/$batch",
            new StringContent("{}", Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /odata/$batch without content type")]
    public async Task Batch_MissingContentType_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PostAsync(
            "/odata/$batch",
            new ByteArrayContent("{}"u8.ToArray()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response);
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

    [IntegrationTest]
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

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /odata/$batch with malformed key predicate URL")]
    public async Task Batch_MalformedKeyPredicateUrl_ReturnsErrorInResponse()
    {
        var payload = new
        {
            requests = new[]
            {
                new
                {
                    id = "1",
                    method = "GET",
                    url = "Features(0,,1)"
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

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /odata/$batch with key predicate containing unknown key")]
    public async Task Batch_KeyPredicateWithUnknownKey_ReturnsErrorInResponse()
    {
        var payload = new
        {
            requests = new[]
            {
                new
                {
                    id = "1",
                    method = "GET",
                    url = "Features(LayerId=0,ObjectId=1,Extra=1)"
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

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /odata/$batch with duplicate key predicate property")]
    public async Task Batch_KeyPredicateWithDuplicateObjectId_ReturnsErrorInResponse()
    {
        var payload = new
        {
            requests = new[]
            {
                new
                {
                    id = "1",
                    method = "GET",
                    url = "Features(LayerId=0,ObjectId=1,ObjectId=2)"
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

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /odata/$batch with malformed $select delimiter")]
    public async Task Batch_MalformedSelectDelimiter_ReturnsErrorInResponse()
    {
        var payload = new
        {
            requests = new[]
            {
                new
                {
                    id = "1",
                    method = "GET",
                    url = "Features(0)?$select=ObjectId,,LayerId"
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

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /odata/$batch with too many requests")]
    public async Task Batch_TooManyRequests_ReturnsBadRequest()
    {
        var requests = string.Join(
            ',',
            Enumerable.Range(1, 1001)
                .Select(i => $$"""{"id":"{{i}}","method":"GET","url":"Features(0,1)"}"""));

        var payload = $$"""{"requests":[{{requests}}]}""";

        var response = await _fixture.Client.PostAsync(
            "/odata/$batch",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response, "InvalidRequest");

        var message = await GetODataErrorMessageAsync(response);
        message.Should().Contain("maximum of 1000 operations");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /odata/$batch with oversized multipart section")]
    public async Task Batch_MultipartSectionTooLarge_ReturnsBadRequest()
    {
        const string boundary = "batch_oversized";
        var oversizedBody = new string('a', 10 * 1024 * 1024 + 1);
        var payload = string.Join("\r\n",
        [
            $"--{boundary}",
            "Content-Type: application/http",
            "Content-Transfer-Encoding: binary",
            string.Empty,
            "GET /odata/Features(0,1) HTTP/1.1",
            "Accept: application/json",
            string.Empty,
            oversizedBody,
            $"--{boundary}--",
            string.Empty
        ]);

        var content = new StringContent(payload, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/mixed;boundary={boundary}");

        var response = await _fixture.Client.PostAsync("/odata/$batch", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertODataErrorAsync(response, "InvalidRequest");

        var message = await GetODataErrorMessageAsync(response);
        message.Should().Contain("maximum allowed size");
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch with mixed access")]
    public async Task Batch_MixedAccess_ReturnsPerItemResponses()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["alpha-reader"]),
                betaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["beta-reader"])));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-reader");

        var batchRequest = new ODataBatchRequest
        {
            Requests =
            [
                new ODataBatchRequestItem
                {
                    Id = "read-alpha",
                    Method = "GET",
                    Url = $"Layers({ServiceRbacTestFixture.AlphaLayerId})"
                },
                new ODataBatchRequestItem
                {
                    Id = "read-beta",
                    Method = "GET",
                    Url = $"Layers({ServiceRbacTestFixture.BetaLayerId})"
                }
            ]
        };

        var json = JsonSerializer.Serialize(batchRequest, ODataJsonContext.Default.ODataBatchRequest);
        var response = await client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var responses = document.RootElement.GetProperty("responses").EnumerateArray().ToArray();
        responses.Should().HaveCount(2);
        responses[0].GetProperty("status").GetInt32().Should().Be(200);
        responses[1].GetProperty("status").GetInt32().Should().Be(403);
    }

    #endregion

    #region OData Error Format Validation

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$top=-1")]
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

    private static async Task<string> GetODataErrorMessageAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var errorElement = document.RootElement.GetProperty("error");
        if (errorElement.TryGetProperty("details", out var detailsElement) &&
            detailsElement.ValueKind == JsonValueKind.Array &&
            detailsElement.GetArrayLength() > 0)
        {
            var firstDetail = detailsElement[0];
            if (firstDetail.TryGetProperty("message", out var messageElement))
            {
                return messageElement.GetString() ?? string.Empty;
            }
        }

        return errorElement.GetProperty("message").GetString() ?? string.Empty;
    }

    private static async Task<List<JsonElement>> ParseFeaturesAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        return document.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(e => e.Clone())
            .ToList();
    }

    private static JsonElement ParseAttributes(JsonElement feature)
    {
        return ODataTestHelpers.ParseAttributes(feature);
    }

    #endregion
}
