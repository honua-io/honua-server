// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// The published-COG surface: an admin publish that materialises a layer's primary raster
/// through the raster store's COG export, and the policy-aware byte-range proxy desktop clients
/// (GDAL /vsicurl, ArcGIS Pro) read it through. The raster store is substituted so the test
/// pins the HTTP contract - deterministic key, exact bytes, HEAD length, 206 ranges - rather
/// than PostGIS's GDAL build.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Export)]
public sealed class CogArtifactEndpointTests : IAsyncLifetime
{
    private const long PrimaryRasterId = 41;
    private static readonly byte[] CogBytes = BuildFakeCogBytes();

    private readonly WebAppFixture _fixture = new();
    private IRasterStore _rasterStore = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _rasterStore = Substitute.For<IRasterStore>();
        _rasterStore.GetPrimaryRasterInfoAsync(WebAppFixture.TestLayerId, Arg.Any<CancellationToken>())
            .Returns(new RasterInfo
            {
                Id = PrimaryRasterId,
                LayerId = WebAppFixture.TestLayerId,
                Name = "primary",
                Width = 64,
                Height = 64,
                BandCount = 1,
                Srid = 4326,
                PixelType = "32BF",
            });
        _rasterStore.GetPrimaryRasterInfoAsync(Arg.Is<int>(id => id != WebAppFixture.TestLayerId), Arg.Any<CancellationToken>())
            .Returns((RasterInfo?)null);
        _rasterStore.ExportImageAsync(
                WebAppFixture.TestLayerId,
                PrimaryRasterId,
                Arg.Is<RasterQuery>(q => q.OutputFormat == RasterFormat.COG),
                Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = CogBytes,
                ContentType = "image/tiff",
                Width = 64,
                Height = 64,
                Srid = 4326,
                BandCount = 1,
            });

        _fixture.ReplaceService<IRasterStore>(_rasterStore);
        _fixture.ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/raster-artifacts/cog")]
    public async Task PublishCog_ExportsThePrimaryRasterUnderADeterministicKey()
    {
        var descriptor = await PublishAsync();

        descriptor.GetProperty("artifactId").GetString().Should().Be($"cog/{WebAppFixture.TestLayerId}/{PrimaryRasterId}.tif");
        descriptor.GetProperty("url").GetString().Should().Be($"/api/v1/rasters/cog/cog/{WebAppFixture.TestLayerId}/{PrimaryRasterId}.tif");
        descriptor.GetProperty("sizeBytes").GetInt64().Should().Be(CogBytes.LongLength);
        descriptor.GetProperty("contentType").GetString().Should().Be("image/tiff; application=geotiff; profile=cloud-optimized");
        descriptor.GetProperty("width").GetInt32().Should().Be(64);
        descriptor.GetProperty("height").GetInt32().Should().Be(64);
        descriptor.GetProperty("srid").GetInt32().Should().Be(4326);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/raster-artifacts/cog")]
    public async Task PublishCog_UnknownLayer_Returns404()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/admin/raster-artifacts/cog", new { layerId = 987654 });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/raster-artifacts/cog")]
    public async Task PublishCog_ServiceIndexDiffersFromStorageLayer_ExportsOnlyBoundRaster()
    {
        const int storageLayerId = 2701;
        const long boundRasterId = 42;
        var boundBytes = CogBytes.ToArray();
        boundBytes[16] ^= 0xFF;
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var publication = ImagePublication(graph);
        var resource = graph.Resources.Single(candidate => candidate.Metadata.Id == publication.ResourceId);
        var bindingId = publication.StorageBindingId ?? resource.PrimaryStorageBindingId;
        SetGraph(graph with
        {
            StorageBindings = graph.StorageBindings.Select(binding => binding.Metadata.Id == bindingId
                ? binding with { StorageLayerId = storageLayerId } : binding).ToArray()
        });
        _rasterStore.GetPrimaryRasterInfoAsync(storageLayerId, Arg.Any<CancellationToken>())
            .Returns(new RasterInfo
            {
                Id = boundRasterId,
                LayerId = storageLayerId,
                Name = "bound primary",
                Width = 64,
                Height = 64,
                BandCount = 1,
                Srid = 4326,
                PixelType = "32BF",
            });
        _rasterStore.ExportImageAsync(storageLayerId, boundRasterId,
                Arg.Is<RasterQuery>(query => query.OutputFormat == RasterFormat.COG),
                Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = boundBytes,
                ContentType = "image/tiff",
                Width = 64,
                Height = 64,
                Srid = 4326,
                BandCount = 1,
            });

        var descriptor = await PublishAsync();

        descriptor.GetProperty("artifactId").GetString()
            .Should().Be($"cog/{WebAppFixture.TestLayerId}/{boundRasterId}.tif");
        await _rasterStore.Received(1).GetPrimaryRasterInfoAsync(storageLayerId, Arg.Any<CancellationToken>());
        await _rasterStore.Received(1).ExportImageAsync(storageLayerId, boundRasterId,
            Arg.Is<RasterQuery>(query => query.OutputFormat == RasterFormat.COG), Arg.Any<CancellationToken>());
        await _rasterStore.DidNotReceive().GetPrimaryRasterInfoAsync(WebAppFixture.TestLayerId, Arg.Any<CancellationToken>());
        await _rasterStore.DidNotReceive().ExportImageAsync(WebAppFixture.TestLayerId,
            Arg.Any<long>(), Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>());

        using var response = await _client.GetAsync(descriptor.GetProperty("url").GetString()!);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(boundBytes);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/rasters/cog/{*artifactId}")]
    public async Task CogProxy_FullGet_ServesTheExportedBytesVerbatim()
    {
        var descriptor = await PublishAsync();
        var url = descriptor.GetProperty("url").GetString()!;

        using var response = await _client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.ToString().Should().Be("image/tiff; application=geotiff; profile=cloud-optimized");
        response.Headers.AcceptRanges.Should().Contain("bytes");
        var body = await response.Content.ReadAsByteArrayAsync();
        body.Should().Equal(CogBytes);
    }

    [IntegrationTest]
    [Endpoint("HEAD /api/v1/rasters/cog/{*artifactId}")]
    public async Task CogProxy_Head_ReportsTheArtifactLengthOnEveryProbe()
    {
        var descriptor = await PublishAsync();
        var url = descriptor.GetProperty("url").GetString()!;

        // GDAL /vsicurl sizes the file from HEAD before every open; the length must be
        // stable across probes (the output cache used to replay a zero on the second one).
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            request.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");
            using var response = await _client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.OK, $"HEAD attempt {attempt}");
            response.Headers.AcceptRanges.Should().Contain("bytes");
            response.Content.Headers.ContentLength.Should().Be(CogBytes.LongLength, $"HEAD attempt {attempt}");
            (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/rasters/cog/{*artifactId}")]
    public async Task CogProxy_RangeRequest_Returns206WithExactlyThoseBytes()
    {
        var descriptor = await PublishAsync();
        var url = descriptor.GetProperty("url").GetString()!;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(16, 47);
        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange!.ToString().Should().Be($"bytes 16-47/{CogBytes.Length}");
        var body = await response.Content.ReadAsByteArrayAsync();
        body.Should().Equal(CogBytes.AsSpan(16, 32).ToArray());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/rasters/cog/{*artifactId}")]
    public async Task CogProxy_RangeAfterAWarmFullGet_IsNotServedFromTheOutputCache()
    {
        var descriptor = await PublishAsync();
        var url = descriptor.GetProperty("url").GetString()!;

        // GDAL opens a COG with a full or large first read and then ranges into it. A
        // cached whole-body 200 must never answer the ranged GET that follows.
        using (var warm = await _client.GetAsync(url))
        {
            warm.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 3);
        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Headers.Contains("Age").Should().BeFalse("a ranged GET must bypass the output cache");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(CogBytes.AsSpan(0, 4).ToArray());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/rasters/cog/{*artifactId}")]
    public async Task CogProxy_UnknownArtifact_Returns404()
    {
        using var response = await _client.GetAsync($"/api/v1/rasters/cog/cog/{WebAppFixture.TestLayerId}/{Guid.NewGuid():N}.tif");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTheory]
    [InlineData("GET", false, false)]
    [InlineData("HEAD", false, false)]
    [InlineData("GET", true, false)]
    [InlineData("GET", false, true)]
    [InlineData("HEAD", false, true)]
    [InlineData("GET", true, true)]
    [Endpoint("GET /api/v1/rasters/cog/{*artifactId}")]
    [Endpoint("HEAD /api/v1/rasters/cog/{*artifactId}")]
    public async Task CogProxy_SourceBecomesRestricted_RequiresAccessBeforeBytesOrHeaders(
        string method, bool range, bool restrictService)
    {
        var descriptor = await PublishAsync();
        var url = descriptor.GetProperty("url").GetString()!;
        var initial = _fixture.GetCurrentV2GraphSnapshot();
        var publication = ImagePublication(initial.Graph);
        var publicPolicy = new AccessPolicy { AllowAnonymous = true };
        SetGraph(initial.Graph with
        {
            Resources = initial.Graph.Resources.Select(resource => resource.Metadata.Id == publication.ResourceId
                ? resource with { AccessPolicy = publicPolicy } : resource).ToArray(),
            Services = initial.Graph.Services.Select(service => service.Metadata.Id == publication.ServiceId
                ? service with { AccessPolicy = publicPolicy } : service).ToArray()
        });

        using var anonymous = _fixture.CreateClient();
        using (var warm = await anonymous.GetAsync(url))
        {
            warm.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var policy = new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["admin"] };
        SetGraph(snapshot.Graph with
        {
            Resources = snapshot.Graph.Resources.Select(resource => !restrictService && resource.Metadata.Id == publication.ResourceId
                ? resource with { AccessPolicy = policy } : resource).ToArray(),
            Services = snapshot.Graph.Services.Select(service => restrictService && service.Metadata.Id == publication.ServiceId
                ? service with { AccessPolicy = policy } : service).ToArray()
        });

        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (range)
        {
            request.Headers.Range = new RangeHeaderValue(0, 3);
        }
        using var response = await anonymous.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.ETag.Should().BeNull();
        response.Headers.AcceptRanges.Should().NotContain("bytes",
            "a denied response must not advertise the COG artifact's byte ranges");
        response.Content.Headers.ContentRange.Should().BeNull();

        using var authorized = _fixture.CreateAdminClient();
        using var allowed = await authorized.GetAsync(url);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await allowed.Content.ReadAsByteArrayAsync()).Should().Equal(CogBytes);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/raster-artifacts/cog")]
    public async Task PublishCog_AmbiguousImagePublications_Returns404()
    {
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var publication = ImagePublication(graph);
        SetGraph(graph with
        {
            Publications = [.. graph.Publications, publication with
            {
                Metadata = publication.Metadata with { Id = "ambiguous-image-publication" }
            }]
        });

        using var response = await _client.PostAsJsonAsync("/api/v1/admin/raster-artifacts/cog",
            new { layerId = WebAppFixture.TestLayerId });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/rasters/cog/{*artifactId}")]
    public async Task CogProxy_PublicationRebound_DoesNotServePreviousResourceBytes()
    {
        var descriptor = await PublishAsync();
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var publication = ImagePublication(graph);
        var resource = graph.Resources.Single(candidate => candidate.Metadata.Id == publication.ResourceId);
        var replacement = resource with { Metadata = resource.Metadata with { Id = "replacement-cog-resource" } };
        SetGraph(graph with
        {
            Resources = [.. graph.Resources, replacement],
            Publications = graph.Publications.Select(candidate => candidate.Metadata.Id == publication.Metadata.Id
                ? candidate with { ResourceId = replacement.Metadata.Id } : candidate).ToArray()
        });

        using var response = await _client.GetAsync(descriptor.GetProperty("url").GetString()!);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/rasters/cog/{*artifactId}")]
    public async Task CogProxy_StorageBindingReboundUnderSameId_DoesNotServePreviousBytes()
    {
        var descriptor = await PublishAsync();
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var publication = ImagePublication(graph);
        var resource = graph.Resources.Single(candidate => candidate.Metadata.Id == publication.ResourceId);
        var bindingId = publication.StorageBindingId ?? resource.PrimaryStorageBindingId;
        var binding = graph.StorageBindings.Single(candidate => candidate.Metadata.Id == bindingId);
        SetGraph(graph with
        {
            StorageBindings = graph.StorageBindings.Select(candidate => candidate.Metadata.Id == bindingId
                ? candidate with { Locator = binding.Locator + "_rebound" } : candidate).ToArray()
        });

        using var response = await _client.GetAsync(descriptor.GetProperty("url").GetString()!);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static MetadataV2Publication ImagePublication(MetadataV2Graph graph)
        => graph.Publications.Single(publication => publication.LayerIndex == WebAppFixture.TestLayerId &&
            publication.PublicationType == MetadataV2PublicationType.EsriImageLayer);

    private void SetGraph(MetadataV2Graph graph)
        => _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>()
            .SetGraph(graph with { Revision = graph.Revision + 1 }, schema: _fixture.MetadataGraphSchema);

    private async Task<JsonElement> PublishAsync()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/admin/raster-artifacts/cog", new { layerId = WebAppFixture.TestLayerId });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// 4 KiB of deterministic bytes behind a little-endian TIFF magic. The endpoint publishes
    /// what the raster store returns; the COG layout itself is PostGIS/GDAL's contract and is
    /// exercised by the client lanes against the real fixture.
    /// </summary>
    private static byte[] BuildFakeCogBytes()
    {
        var bytes = new byte[4096];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        bytes[2] = 42;
        bytes[3] = 0;
        for (var i = 4; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 31 + 7) & 0xFF);
        }

        return bytes;
    }
}
