// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Publishing.Descriptors;
using Honua.Server.Features.Admin.PairedPublications;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class PairedPublishEndpointTests
{
    private string PublicationRoute => $"/api/v1/publications/{_publicationId}";

    [IntegrationTest]
    [Endpoint("GET /api/v1/publications/{publicationId}/current.json")]
    [Endpoint("HEAD /api/v1/publications/{publicationId}/current.json")]
    [Endpoint("GET /api/v1/publications/{publicationId}/{version}/{fileName}")]
    public async Task PublicationRead_Republish_PinsDescriptorAndRelativeArtifactsToOneVersion()
    {
        var first = await PublishAsync(IdentityField);
        using var client = _fixture.CreateClient(allowAutoRedirect: false);
        client.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword);
        using var current = await client.GetAsync($"{PublicationRoute}/current.json");
        current.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);
        current.Headers.Location!.OriginalString.Should().Be($"{PublicationRoute}/{first.Version}/descriptor.json");
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, $"{PublicationRoute}/current.json");
        using var head = await client.SendAsync(headRequest);
        head.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);
        head.Headers.Location.Should().Be(current.Headers.Location);
        (await head.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        var second = await PublishAsync(IdentityField);
        using var pinned = await _client.GetAsync(current.Headers.Location);
        pinned.StatusCode.Should().Be(HttpStatusCode.OK);
        (await pinned.Content.ReadAsByteArrayAsync()).Should().Equal(await ReadBytesAsync(first.DescriptorKey));
        using var changed = await client.GetAsync($"{PublicationRoute}/current.json");
        changed.Headers.Location!.OriginalString.Should().Be($"{PublicationRoute}/{second.Version}/descriptor.json");
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/publications/{publicationId}/{version}/{fileName}")]
    [Endpoint("HEAD /api/v1/publications/{publicationId}/{version}/{fileName}")]
    [InlineData("pmtiles")]
    [InlineData("parquet")]
    public async Task PublicationRead_Artifacts_SupportRangeHeadAndConditionalRequests(string extension)
    {
        var published = await PublishAsync(IdentityField);
        var route = $"{PublicationRoute}/{published.Version}/{_publicationId}.{extension}";
        var bytes = await ReadBytesAsync(ArtifactKey(published, $"{_publicationId}.{extension}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Range = new RangeHeaderValue(0, 15);
        using var ranged = await _client.SendAsync(request);
        ranged.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await ranged.Content.ReadAsByteArrayAsync()).Should().Equal(bytes[..16]);
        ranged.Content.Headers.ContentRange!.Length.Should().Be(bytes.LongLength);
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, route);
        using var head = await _client.SendAsync(headRequest);
        head.StatusCode.Should().Be(HttpStatusCode.OK);
        head.Content.Headers.ContentLength.Should().Be(bytes.LongLength);
        (await head.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        using var conditional = new HttpRequestMessage(HttpMethod.Get, route);
        conditional.Headers.IfNoneMatch.Add(head.Headers.ETag!);
        using var unchanged = await _client.SendAsync(conditional);
        unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
        using var invalid = new HttpRequestMessage(HttpMethod.Get, route);
        invalid.Headers.Range = new RangeHeaderValue(bytes.LongLength, null);
        using var unsatisfiable = await _client.SendAsync(invalid);
        unsatisfiable.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/publications/{publicationId}/current.json")]
    [Endpoint("GET /api/v1/publications/{publicationId}/{version}/{fileName}")]
    public async Task PublicationRead_Anonymous_IsRefusedBeforeExistenceOrConditionChecks()
    {
        var published = await PublishAsync(IdentityField);
        using var anonymous = _fixture.CreateClient();
        foreach (var suffix in new[] { "current.json", $"{published.Version}/descriptor.json", $"{published.Version}/{_publicationId}.parquet", "missing/descriptor.json" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{PublicationRoute}/{suffix}");
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            using var response = await anonymous.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/publications/{publicationId}/current.json")]
    [Endpoint("GET /api/v1/publications/{publicationId}/{version}/{fileName}")]
    public async Task PublicationRead_NewlyHiddenField_RefusesPreviouslyPublishedBytes()
    {
        var published = await PublishAsync(IdentityField);
        HideField(IdentityField);
        foreach (var suffix in new[] { "current.json", $"{published.Version}/descriptor.json", $"{published.Version}/item.json", $"{published.Version}/{_publicationId}.pmtiles", $"{published.Version}/{_publicationId}.parquet" })
        {
            using var response = await _client.GetAsync($"{PublicationRoute}/{suffix}");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/publications/{publicationId}/current.json")]
    [Endpoint("GET /api/v1/publications/{publicationId}/{version}/{fileName}")]
    public async Task PublicationRead_RetiredSource_RefusesPreviouslyPublishedBytes()
    {
        var published = await PublishAsync(IdentityField);
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>().SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Publications = snapshot.Graph.Publications.Select(publication => publication with
            {
                Status = publication.Status with { Lifecycle = MetadataV2LifecycleStatus.Retired }
            }).ToArray()
        }, schema: _fixture.MetadataGraphSchema);

        foreach (var suffix in new[] { "current.json", $"{published.Version}/descriptor.json", $"{published.Version}/{_publicationId}.parquet" })
        {
            using var response = await _client.GetAsync($"{PublicationRoute}/{suffix}");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/publications/{publicationId}/current.json")]
    [Endpoint("HEAD /api/v1/publications/{publicationId}/current.json")]
    [Endpoint("GET /api/v1/publications/{publicationId}/{version}/{fileName}")]
    [Endpoint("HEAD /api/v1/publications/{publicationId}/{version}/{fileName}")]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task PublicationRead_TenantChanged_RefusesPreviouslyPublishedBytes(string method)
    {
        var published = await PublishAsync(IdentityField);
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>().SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Publications = snapshot.Graph.Publications.Select(publication => publication with
            {
                Metadata = publication.Metadata with { Tenant = "another-tenant" }
            }).ToArray()
        }, schema: _fixture.MetadataGraphSchema);

        foreach (var suffix in new[] { "current.json", $"{published.Version}/descriptor.json", $"{published.Version}/{_publicationId}.pmtiles", $"{published.Version}/{_publicationId}.parquet" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), $"{PublicationRoute}/{suffix}");
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            request.Headers.Range = new RangeHeaderValue(0, 15);
            using var response = await _client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/publications/{publicationId}/current.json")]
    [Endpoint("GET /api/v1/publications/{publicationId}/{version}/{fileName}")]
    public async Task PublicationRead_MalformedPointer_RefusesCurrentAndPinnedArtifacts()
    {
        var published = await PublishAsync(IdentityField);
        var options = _fixture.Services.GetRequiredService<IOptions<CloudStorageOptions>>().Value;
        var store = PairedPublicationStore.Create(Storage, options, _publicationId);
        var pointer = await ReadJsonAsync<PublicationPointer>(published.PointerKey);
        var expectedTag = await store.ReadPointerETagAsync(CancellationToken.None);
        (await store.SwitchPointerAsync(pointer with { Descriptor = "../descriptor.json" }, expectedTag, CancellationToken.None))
            .Should().BeTrue();

        foreach (var suffix in new[] { "current.json", $"{published.Version}/descriptor.json", $"{published.Version}/{_publicationId}.pmtiles" })
        {
            using var response = await _client.GetAsync($"{PublicationRoute}/{suffix}");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }
}
