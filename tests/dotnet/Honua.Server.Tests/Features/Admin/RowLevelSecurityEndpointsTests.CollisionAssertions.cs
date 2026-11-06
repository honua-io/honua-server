// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class RowLevelSecurityEndpointsTests
{
    private static async Task AssertRowFilterCollisionResponsesAsync(HttpClient client)
    {
        AssertCategoryFilteredAttributes(await ReadFeatureServerCollisionAttributesAsync(client));
        AssertCategoryFilteredAttributes(await ReadODataCollisionAttributesAsync(client));
    }

    private static async Task AssertFieldMaskCollisionResponsesAsync(HttpClient client)
    {
        AssertDescriptionMaskedAttributes(await ReadFeatureServerCollisionAttributesAsync(client));
        AssertDescriptionMaskedAttributes(await ReadODataCollisionAttributesAsync(client));
    }

    private static async Task<JsonElement[]> ReadFeatureServerCollisionAttributesAsync(HttpClient client)
    {
        using var response = await client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1%3D1&outFields=*&returnGeometry=false&f=json");
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("attributes").Clone()).ToArray();
    }

    private static async Task<JsonElement[]> ReadODataCollisionAttributesAsync(HttpClient client)
    {
        using var response = await client.GetAsync($"/odata/Features({WebAppFixture.TestLayerId})?$top=10");
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("value").EnumerateArray()
            .Select(feature => ODataTestHelpers.ParseAttributes(feature).Clone()).ToArray();
    }

    private static void AssertCategoryFilteredAttributes(JsonElement[] attributes)
    {
        attributes.Should().HaveCount(CategoryTestCount);
        attributes.Should().OnlyContain(value => value.GetProperty("category").GetString() == "test");
    }

    private static void AssertDescriptionMaskedAttributes(JsonElement[] attributes)
    {
        attributes.Should().HaveCount(TestFeatureCount);
        attributes.Should().OnlyContain(value => !value.EnumerateObject().Any(property => property.Name == "description"));
    }
}
