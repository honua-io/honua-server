// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    public async Task UpdateLayerFields_WithAliasAndCodedValueDomain_PersistsAndHydratesCatalog()
    {
        var adminClient = _fixture.CreateAdminClient();
        var anonymousClient = _fixture.CreateClient();
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
                }
            ]
        };

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

        var getResponse = await adminClient.GetAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/fields");

        getResponse.Be200Ok();
        var get = await ReadFieldConfigurationResponseAsync(getResponse);
        get.Data!.Fields.Single(field => field.Name == "category").Alias.Should().Be("Lifecycle category");
        get.Data!.Fields.Single(field => field.Name == "category").Domain!.CodedValues![0].Name.Should().Be("Active");

        var featureLayerResponse = await anonymousClient.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}?f=json");

        featureLayerResponse.Be200Ok();
        using var document = JsonDocument.Parse(await featureLayerResponse.Content.ReadAsStringAsync());
        var fields = document.RootElement.GetProperty("fields").EnumerateArray();
        var categoryField = fields.Single(field => field.GetProperty("name").GetString() == "category");
        categoryField.GetProperty("alias").GetString().Should().Be("Lifecycle category");
        var domain = categoryField.GetProperty("domain");
        domain.GetProperty("name").GetString().Should().Be("category-domain");
        domain.GetProperty("type").GetString().Should().Be("codedValue");
        domain.GetProperty("codedValues").EnumerateArray()
            .Select(value => value.GetProperty("name").GetString())
            .Should()
            .BeEquivalentTo("Active", "Retired");

        var clearRequest = new LayerFieldConfigurationUpdateRequest
        {
            Fields =
            [
                new LayerFieldConfigurationUpdateItem
                {
                    Name = "category",
                    Alias = " "
                }
            ]
        };

        var clearResponse = await adminClient.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/fields",
            JsonContent.Create(clearRequest, LayerFieldConfigurationJsonContext.Default.LayerFieldConfigurationUpdateRequest));

        clearResponse.Be200Ok();
        var clear = await ReadFieldConfigurationResponseAsync(clearResponse);
        var clearedCategory = clear.Data!.Fields.Single(field => field.Name == "category");
        clearedCategory.Alias.Should().BeNull();
        clearedCategory.Domain.Should().BeNull();
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
}
