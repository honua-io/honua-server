// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Honua.Sdk.Admin.Models;
using Honua.Sdk.Admin.Tests.Fixtures;

namespace Honua.Sdk.Admin.Tests;

public sealed class MetadataResourceTests
{
    private static object CreateMetadataResourcePayload() => new
    {
        apiVersion = "honua.io/v1alpha1",
        kind = "Layer",
        metadata = new
        {
            id = "res-1",
            name = "test-layer",
            @namespace = "default",
            resourceVersion = "1"
        },
        spec = new { type = "Feature" }
    };

    [Fact]
    public async Task ListMetadataResourcesAsync_ReturnsResources()
    {
        var resources = new[] { CreateMetadataResourcePayload() };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains("/admin/metadata/resources", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(resources));
        });

        var result = await client.ListMetadataResourcesAsync();

        Assert.Single(result);
        Assert.Equal("Layer", result[0].Kind);
    }

    [Fact]
    public async Task ListMetadataResourcesAsync_WithFilters_PassesQueryParams()
    {
        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Contains("kind=Layer", req.RequestUri!.Query);
            Assert.Contains("namespace=prod", req.RequestUri!.Query);
            return Task.FromResult(TestHelpers.CreateJsonResponse(Array.Empty<object>()));
        });

        var result = await client.ListMetadataResourcesAsync(kind: "Layer", ns: "prod");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMetadataResourceAsync_ReturnsResourceAndETag()
    {
        var resource = CreateMetadataResourcePayload();

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Contains("/admin/metadata/resources/Layer/default/test-layer", req.RequestUri!.PathAndQuery);
            var response = TestHelpers.CreateJsonResponse(resource);
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        });

        var (result, etag) = await client.GetMetadataResourceAsync("Layer", "default", "test-layer");

        Assert.Equal("Layer", result.Kind);
        Assert.Equal("test-layer", result.Metadata?.Name);
        Assert.Equal("\"v1\"", etag);
    }

    [Fact]
    public async Task GetMetadataResourceAsync_NoETag_ReturnsNull()
    {
        var resource = CreateMetadataResourcePayload();

        var client = TestHelpers.CreateClient(req =>
            Task.FromResult(TestHelpers.CreateJsonResponse(resource)));

        var (result, etag) = await client.GetMetadataResourceAsync("Layer", "default", "test-layer");

        Assert.NotNull(result);
        Assert.Null(etag);
    }

    [Fact]
    public async Task CreateMetadataResourceAsync_SendsPost()
    {
        var created = CreateMetadataResourcePayload();

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/admin/metadata/resources", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateJsonResponse(created, HttpStatusCode.Created));
        });

        var result = await client.CreateMetadataResourceAsync(new MetadataResource
        {
            ApiVersion = "honua.io/v1alpha1",
            Kind = "Layer",
            Metadata = new ResourceMetadata { Name = "test-layer", Namespace = "default" },
            Spec = JsonDocument.Parse("{\"type\":\"Feature\"}").RootElement
        });

        Assert.Equal("Layer", result.Kind);
    }

    [Fact]
    public async Task UpdateMetadataResourceAsync_SendsPutWithIfMatch()
    {
        var updated = CreateMetadataResourcePayload();

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Put, req.Method);
            Assert.Contains("/admin/metadata/resources/Layer/default/test-layer", req.RequestUri!.PathAndQuery);
            Assert.True(req.Headers.Contains("If-Match"));
            Assert.Equal("\"v1\"", req.Headers.GetValues("If-Match").First());
            return Task.FromResult(TestHelpers.CreateJsonResponse(updated));
        });

        var result = await client.UpdateMetadataResourceAsync(
            "Layer", "default", "test-layer",
            new MetadataResource
            {
                ApiVersion = "honua.io/v1alpha1",
                Kind = "Layer",
                Spec = JsonDocument.Parse("{\"type\":\"Feature\"}").RootElement
            },
            ifMatch: "\"v1\"");

        Assert.Equal("Layer", result.Kind);
    }

    [Fact]
    public async Task DeleteMetadataResourceAsync_SendsDeleteWithIfMatch()
    {
        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Equal(HttpMethod.Delete, req.Method);
            Assert.Contains("/admin/metadata/resources/Layer/default/test-layer", req.RequestUri!.PathAndQuery);
            Assert.True(req.Headers.Contains("If-Match"));
            return Task.FromResult(TestHelpers.CreateJsonResponse(new { }, HttpStatusCode.OK));
        });

        await client.DeleteMetadataResourceAsync("Layer", "default", "test-layer", ifMatch: "\"v1\"");
    }

    [Fact]
    public async Task DeleteMetadataResourceAsync_WithoutIfMatch_OmitsHeader()
    {
        var client = TestHelpers.CreateClient(req =>
        {
            Assert.False(req.Headers.Contains("If-Match"));
            return Task.FromResult(TestHelpers.CreateJsonResponse(new { }, HttpStatusCode.OK));
        });

        await client.DeleteMetadataResourceAsync("Layer", "default", "test-layer");
    }
}
