// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class AdminDiscoveryMetadataEndpointsTests
{
    [IntegrationTheory]
    [Endpoint("GET /api/v1/admin/metadata/layers/{storageLayerId}/discovery")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{storageLayerId}/discovery")]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/discovery")]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/discovery")]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    [Endpoint("GET /rest/services/{serviceName}/FeatureServer")]
    [Endpoint("GET /rest/services/{serviceName}/FeatureServer/{layerId}")]
    [InlineData("/api/v1/admin/metadata/layers/0/discovery")]
    [InlineData("/api/v1/admin/services/test/discovery")]
    public async Task UpdateDiscovery_DescriptiveFields_RoundTripsPreservesAndClears(string path)
    {
        var client = _fixture.CreateAdminClient();
        var before = await ReadAsync(client, path);
        var fields = JsonSerializer.Deserialize<Dictionary<string, object?>>("""
            {"themes":["transport"],"language":"en-GB","license":"MIT",
             "attribution":"City survey","publisher":"City GIS",
             "contactPoint":{"name":"GIS support","email":"gis@example.com","url":"https://example.com/contact"},
             "links":[{"href":"https://example.com/metadata","rel":"describedby","type":"text/html"}]}
            """)!;
        fields["etag"] = before.Etag;
        using var content = new StringContent(JsonSerializer.Serialize(fields), Encoding.UTF8, "application/json");
        (await client.PutAsync(path, content)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var saved = JsonDocument.Parse(await client.GetStringAsync(path));
        var data = saved.RootElement.GetProperty("data");
        foreach (var field in fields.Keys.Where(key => key is not ("etag" or "contactPoint" or "links")))
            data.GetProperty(field).GetRawText().Should().Be(JsonSerializer.Serialize(fields[field]));
        data.GetProperty("contactPoint").GetProperty("email").GetString().Should().Be("gis@example.com");
        data.GetProperty("links")[0].GetProperty("href").GetString().Should().Be("https://example.com/metadata");
        AssertStoredDiscoveryFields(path);
        (await UpdateAsync(client, path, data.GetProperty("etag").GetString()!)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var preserved = JsonDocument.Parse(await client.GetStringAsync(path));
        var current = preserved.RootElement.GetProperty("data");
        foreach (var field in fields.Keys.Where(key => key != "etag"))
            current.GetProperty(field).GetRawText().Should().Be(data.GetProperty(field).GetRawText());
        await AssertDiscoveryProtocolFieldsAsync(client, path);
        fields["etag"] = current.GetProperty("etag").GetString();
        await ClearDescriptiveFieldsAsync(client, path, fields);
    }

    private void AssertStoredDiscoveryFields(string path)
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var metadata = path.Contains("/layers/", StringComparison.Ordinal)
            ? snapshot.Index.ResourcesByStorageLayerId[0].Metadata
            : snapshot.Graph.Services.First(service => service.Metadata.Name == "test").Metadata;
        metadata.License.Should().Be("MIT");
        metadata.ContactPoint!.Email.Should().Be("gis@example.com");
    }

    private static async Task ClearDescriptiveFieldsAsync(HttpClient client, string path,
        Dictionary<string, object?> fields)
    {
        var request = new DiscoveryMetadataUpdateRequest
        {
            Etag = (string)fields["etag"]!, Themes = [], Links = [], Language = null,
            License = null, Attribution = null, Publisher = null, ContactPoint = null
        };
        using var clear = new StringContent(JsonSerializer.Serialize(request,
            DiscoveryMetadataJsonContext.Default.DiscoveryMetadataUpdateRequest), Encoding.UTF8, "application/json");
        (await client.PutAsync(path, clear)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var cleared = JsonDocument.Parse(await client.GetStringAsync(path));
        var data = cleared.RootElement.GetProperty("data");
        foreach (var field in fields.Keys.Where(key => key != "etag"))
        {
            if (field is "themes" or "links")
                data.GetProperty(field).GetArrayLength().Should().Be(0);
            else
                data.GetProperty(field).ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    private static async Task AssertDiscoveryProtocolFieldsAsync(HttpClient client, string path)
    {
        var featureServerPath = path.Contains("/layers/", StringComparison.Ordinal)
            ? "/rest/services/test/FeatureServer/0?f=json"
            : "/rest/services/test/FeatureServer?f=json";
        using var response = await client.GetAsync(featureServerPath);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var featureServer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        featureServer.RootElement.GetProperty("license").GetString().Should().Be("MIT");
        featureServer.RootElement.GetProperty("publisher").GetString().Should().Be("City GIS");
        featureServer.RootElement.GetProperty("copyrightText").GetString().Should().Be("City survey");
        if (!path.Contains("/layers/", StringComparison.Ordinal))
            return;
        using var collectionResponse = await client.GetAsync("/ogc/features/collections/0?f=json");
        collectionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var collection = JsonDocument.Parse(await collectionResponse.Content.ReadAsStringAsync());
        var links = collection.RootElement.GetProperty("links").EnumerateArray().ToArray();
        links.Should().Contain(link => link.GetProperty("rel").GetString() == "describedby"
            && link.GetProperty("href").GetString() == "https://example.com/metadata");
        links.Should().Contain(link => link.GetProperty("rel").GetString() == "license"
            && link.GetProperty("title").GetString() == "MIT");
    }

    [IntegrationTheory]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/discovery")]
    [InlineData("license", "\"not a license\"")]
    [InlineData("publisher", "\"bad\\u0000text\"")]
    [InlineData("themes", "[null]")]
    [InlineData("themes", "null")]
    [InlineData("links", "null")]
    [InlineData("contactPoint", "{\"url\":\"javascript:alert(1)\"}")]
    [InlineData("contactPoint", "{\"email\":\"@example.com\"}")]
    [InlineData("links", "[null]")]
    [InlineData("links", "[{\"rel\":\"license\",\"href\":\"file:///etc/passwd\"}]")]
    public async Task UpdateDiscovery_InvalidDescriptiveFields_RejectsWithoutSaving(string field, string value)
    {
        var client = _fixture.CreateAdminClient();
        const string path = "/api/v1/admin/services/test/discovery";
        var before = await ReadAsync(client, path);
        var revision = _fixture.GetCurrentV2GraphSnapshot().Revision;
        var json = "{\"etag\":" + JsonSerializer.Serialize(before.Etag) + ",\"" + field + "\":" + value + "}";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        (await client.PutAsync(path, content)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _fixture.GetCurrentV2GraphSnapshot().Revision.Should().Be(revision);
    }
}
