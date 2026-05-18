// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for admin layer field configuration endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class LayerFieldConfigurationEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/fields")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/fields")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task UpdateLayerFields_WithAliasDomainAndHiddenField_PersistsAndHydratesCatalog()
    {
        var adminClient = _fixture.CreateAdminClient();
        var anonymousClient = _fixture.CreateClient();
        var resetComplete = false;
        var request = new LayerFieldConfigurationUpdateRequest
        {
            Fields =
            [
                new LayerFieldConfigurationUpdateItem
                {
                    Name = "category",
                    Alias = "Lifecycle category",
                    Domain = new FieldDomainDefinition(
                        "category-domain",
                        "codedValue",
                        [
                            new DomainCodedValueDefinition("active", "Active"),
                            new DomainCodedValueDefinition("retired", "Retired")
                        ])
                },
                new LayerFieldConfigurationUpdateItem
                {
                    Name = "description",
                    Alias = "Description",
                    Hidden = true
                }
            ]
        };

        var clearRequest = new LayerFieldConfigurationUpdateRequest
        {
            Fields =
            [
                new LayerFieldConfigurationUpdateItem
                {
                    Name = "category",
                    Alias = " "
                },
                new LayerFieldConfigurationUpdateItem
                {
                    Name = "description",
                    Alias = "Description",
                    Hidden = false
                }
            ]
        };

        try
        {
            var updateResponse = await adminClient.PutAsync(
                $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/fields",
                JsonContent.Create(request, LayerFieldConfigurationJsonContext.Default.LayerFieldConfigurationUpdateRequest));

            updateResponse.Be200Ok();
            var update = await ReadFieldConfigurationResponseAsync(updateResponse);
            var category = update.Data!.Fields.Single(field => field.Name == "category");
            category.Alias.Should().Be("Lifecycle category");
            category.Domain.Should().NotBeNull();
            category.Domain!.Name.Should().Be("category-domain");
            category.Domain.CodedValues.Should().HaveCount(2);
            update.Data!.Fields.Single(field => field.Name == "description").Hidden.Should().BeTrue();

            var getResponse = await adminClient.GetAsync(
                $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/fields");

            getResponse.Be200Ok();
            var get = await ReadFieldConfigurationResponseAsync(getResponse);
            get.Data!.Fields.Single(field => field.Name == "category").Alias.Should().Be("Lifecycle category");
            get.Data!.Fields.Single(field => field.Name == "category").Domain!.CodedValues![0].Name.Should().Be("Active");
            get.Data!.Fields.Single(field => field.Name == "description").Hidden.Should().BeTrue();

            var featureLayerResponse = await anonymousClient.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}?f=json");

            featureLayerResponse.Be200Ok();
            using var document = JsonDocument.Parse(await featureLayerResponse.Content.ReadAsStringAsync());
            var fields = document.RootElement.GetProperty("fields").EnumerateArray().ToArray();
            fields.Should().NotContain(field => field.GetProperty("name").GetString() == "description");
            var categoryField = fields.Single(field => field.GetProperty("name").GetString() == "category");
            categoryField.GetProperty("alias").GetString().Should().Be("Lifecycle category");
            var domain = categoryField.GetProperty("domain");
            domain.GetProperty("name").GetString().Should().Be("category-domain");
            domain.GetProperty("type").GetString().Should().Be("codedValue");
            domain.GetProperty("codedValues").EnumerateArray()
                .Select(value => value.GetProperty("name").GetString())
                .Should()
                .BeEquivalentTo("Active", "Retired");

            var queryResponse = await anonymousClient.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1%3D1&outFields=*&returnGeometry=false&f=json");

            var queryPayload = await queryResponse.Content.ReadAsStringAsync();
            queryResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"response payload was: {queryPayload}");
            using var queryDocument = JsonDocument.Parse(queryPayload);
            queryDocument.RootElement.GetProperty("fields").EnumerateArray()
                .Should()
                .NotContain(field => field.GetProperty("name").GetString() == "description");
            var firstAttributes = queryDocument.RootElement.GetProperty("features")[0].GetProperty("attributes");
            firstAttributes.TryGetProperty("description", out _).Should().BeFalse();

            var clearResponse = await adminClient.PutAsync(
                $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/fields",
                JsonContent.Create(clearRequest, LayerFieldConfigurationJsonContext.Default.LayerFieldConfigurationUpdateRequest));

            clearResponse.Be200Ok();
            var clear = await ReadFieldConfigurationResponseAsync(clearResponse);
            var clearedCategory = clear.Data!.Fields.Single(field => field.Name == "category");
            clearedCategory.Alias.Should().BeNull();
            clearedCategory.Domain.Should().BeNull();
            clear.Data!.Fields.Single(field => field.Name == "description").Hidden.Should().BeFalse();
            resetComplete = true;
        }
        finally
        {
            if (!resetComplete)
            {
                _ = await adminClient.PutAsync(
                    $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/fields",
                    JsonContent.Create(clearRequest, LayerFieldConfigurationJsonContext.Default.LayerFieldConfigurationUpdateRequest));
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/filter")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/filter")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task UpdateLayerFilter_WithValidPermanentFilter_PersistsAndFiltersPublicQueries()
    {
        var adminClient = _fixture.CreateAdminClient();
        var anonymousClient = _fixture.CreateClient();
        var resetComplete = false;
        var request = new LayerFilterConfigurationUpdateRequest
        {
            PermanentFilter = new LayerPermanentFilterConfiguration
            {
                Expression = "category = 'test'",
                Language = "arcgis-sql"
            }
        };
        var clearRequest = new LayerFilterConfigurationUpdateRequest
        {
            PermanentFilter = null
        };

        try
        {
            var updateResponse = await adminClient.PutAsync(
                $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/filter",
                JsonContent.Create(request, LayerFieldConfigurationJsonContext.Default.LayerFilterConfigurationUpdateRequest));

            var updatePayload = await updateResponse.Content.ReadAsStringAsync();
            updateResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"response payload was: {updatePayload}");
            var update = await ReadFilterConfigurationResponseAsync(updateResponse);
            update.Data!.PermanentFilter.Should().NotBeNull();
            update.Data.PermanentFilter!.Expression.Should().Be("category = 'test'");
            update.Data.PermanentFilter.Language.Should().Be("arcgis-sql");

            var getResponse = await adminClient.GetAsync(
                $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/filter");

            getResponse.Be200Ok();
            var get = await ReadFilterConfigurationResponseAsync(getResponse);
            get.Data!.PermanentFilter!.Expression.Should().Be("category = 'test'");

            var queryResponse = await anonymousClient.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1%3D1&outFields=*&returnGeometry=false&f=json");

            var queryPayload = await queryResponse.Content.ReadAsStringAsync();
            queryResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"response payload was: {queryPayload}");
            using var queryDocument = JsonDocument.Parse(queryPayload);
            var features = queryDocument.RootElement.GetProperty("features").EnumerateArray().ToArray();
            features.Should().HaveCount(3);
            features
                .Select(feature => feature.GetProperty("attributes").GetProperty("category").GetString())
                .Should()
                .OnlyContain(category => category == "test");

            var clearResponse = await adminClient.PutAsync(
                $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/filter",
                JsonContent.Create(clearRequest, LayerFieldConfigurationJsonContext.Default.LayerFilterConfigurationUpdateRequest));

            clearResponse.Be200Ok();
            var clear = await ReadFilterConfigurationResponseAsync(clearResponse);
            clear.Data!.PermanentFilter.Should().BeNull();
            resetComplete = true;
        }
        finally
        {
            if (!resetComplete)
            {
                _ = await adminClient.PutAsync(
                    $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/filter",
                    JsonContent.Create(clearRequest, LayerFieldConfigurationJsonContext.Default.LayerFilterConfigurationUpdateRequest));
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/filter")]
    public async Task UpdateLayerFilter_WithInvalidPermanentFilter_ReturnsBadRequest()
    {
        var adminClient = _fixture.CreateAdminClient();
        var request = new LayerFilterConfigurationUpdateRequest
        {
            PermanentFilter = new LayerPermanentFilterConfiguration
            {
                Expression = "missing_field = 'test'",
                Language = "arcgis-sql"
            }
        };

        var response = await adminClient.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/filter",
            JsonContent.Create(request, LayerFieldConfigurationJsonContext.Default.LayerFilterConfigurationUpdateRequest));

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/fields")]
    public async Task UpdateLayerFields_WithUnknownField_ReturnsBadRequest()
    {
        var adminClient = _fixture.CreateAdminClient();
        var request = new LayerFieldConfigurationUpdateRequest
        {
            Fields =
            [
                new LayerFieldConfigurationUpdateItem
                {
                    Name = "missing_field",
                    Alias = "Missing"
                }
            ]
        };

        var response = await adminClient.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/fields",
            JsonContent.Create(request, LayerFieldConfigurationJsonContext.Default.LayerFieldConfigurationUpdateRequest));

        response.Be400BadRequest();
    }

    private static async Task<ApiResponse<LayerFieldConfigurationResponse>> ReadFieldConfigurationResponseAsync(
        HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            LayerFieldConfigurationJsonContext.Default.ApiResponseLayerFieldConfigurationResponse);

        apiResponse.Should().NotBeNull($"response payload was: {payload}");
        apiResponse!.Success.Should().BeTrue();
        apiResponse.Data.Should().NotBeNull();
        return apiResponse;
    }

    private static async Task<ApiResponse<LayerFilterConfigurationResponse>> ReadFilterConfigurationResponseAsync(
        HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            LayerFieldConfigurationJsonContext.Default.ApiResponseLayerFilterConfigurationResponse);

        apiResponse.Should().NotBeNull($"response payload was: {payload}");
        apiResponse!.Success.Should().BeTrue();
        apiResponse.Data.Should().NotBeNull();
        return apiResponse;
    }
}
