// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

/// <summary>
/// MapServer find drops caller-masked string fields from the default search set and
/// refuses an explicit <c>searchFields</c> entry that names one (#6148). The in-memory
/// store does not enforce <c>FeatureQuerySecurity</c>, so these assert the handler:
/// a masked column is not searched, its value is not returned, and naming it is a 400.
/// </summary>
[Protocol(TestProtocols.MapServer)]
public sealed class MapServerFindMaskedFieldTests
{
    private const string ServicePath = $"/rest/services/{MapServerDynamicJoinTests.ServiceName}/MapServer";

    [IntegrationTest]
    [Operation(Operations.Find)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task Find_WithoutSearchFields_SkipsMaskedStringField()
    {
        using var factory = MapServerDynamicJoinTests.CreateFactory(
            rightLayerAnonymous: true,
            fieldMaskSource: new FixedFieldMaskSource("category"));
        using var client = factory.CreateClient();

        // "sample" occurs only in category. A default find must not search that column.
        using var maskedOnly = await client.GetAsync($"{ServicePath}/find?searchText=sample&layers=0&f=json");
        var maskedContent = await maskedOnly.Content.ReadAsStringAsync();
        maskedOnly.StatusCode.Should().Be(HttpStatusCode.OK, maskedContent);
        using (var document = JsonDocument.Parse(maskedContent))
        {
            document.RootElement.GetProperty("results").GetArrayLength().Should().Be(0, maskedContent);
        }

        using var visible = await client.GetAsync($"{ServicePath}/find?searchText=Feature&layers=0&f=json");
        var visibleContent = await visible.Content.ReadAsStringAsync();
        visible.StatusCode.Should().Be(HttpStatusCode.OK, visibleContent);
        using var visibleDocument = JsonDocument.Parse(visibleContent);
        var results = visibleDocument.RootElement.GetProperty("results");
        results.GetArrayLength().Should().BeGreaterThan(0, visibleContent);
        foreach (var result in results.EnumerateArray())
        {
            result.GetProperty("foundFieldName").GetString().Should().Be("name");
            result.GetProperty("attributes").TryGetProperty("category", out _).Should().BeFalse(visibleContent);
        }

        visibleContent.Should().NotContain("sample");
    }

    [IntegrationTest]
    [Operation(Operations.Find)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task Find_SearchFieldsNamingMaskedField_ReturnsBadRequest()
    {
        using var factory = MapServerDynamicJoinTests.CreateFactory(
            rightLayerAnonymous: true,
            fieldMaskSource: new FixedFieldMaskSource("category"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"{ServicePath}/find?searchText=sample&layers=0&searchFields=category&f=json");
        var content = await response.Content.ReadAsStringAsync();
        await response.AssertGeoServicesErrorAsync(400);
        using var errorDocument = JsonDocument.Parse(content);
        var details = errorDocument.RootElement.GetProperty("error").GetProperty("details")
            .EnumerateArray().Select(detail => detail.GetString()).ToArray();
        details.Should().Contain("Field 'category' is not available for search.");
        details.Should().NotContain("Invalid find request parameters.");

        using var allowed = await client.GetAsync(
            $"{ServicePath}/find?searchText=Feature&layers=0&searchFields=name&f=json");
        var allowedContent = await allowed.Content.ReadAsStringAsync();
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, allowedContent);
        using var document = JsonDocument.Parse(allowedContent);
        document.RootElement.GetProperty("results").GetArrayLength().Should().BeGreaterThan(0, allowedContent);
    }

    private sealed class FixedFieldMaskSource(string fieldName) : IFieldMaskSource
    {
        public Task<ImmutableArray<string>> ResolveAsync(
            MetadataV2Resource resource,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ImmutableArray.Create(fieldName));
    }
}
