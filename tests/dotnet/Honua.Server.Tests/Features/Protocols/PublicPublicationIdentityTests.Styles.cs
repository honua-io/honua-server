// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Styling;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols;

public sealed partial class PublicPublicationIdentityTests
{
    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiStyles)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /api/styles/{publicationId}.json")]
    public async Task Style_StorageRebound_KeepsPublicationUrlAndResolvesCurrentStorage()
    {
        var path = $"/api/styles/{Uri.EscapeDataString(PublicationId)}.json";
        using var before = await _fixture.Client.GetAsync(path);
        var beforeBody = await before.Content.ReadAsStringAsync();
        RebindStorage();
        using var after = await _fixture.Client.GetAsync(path);
        var afterBody = await after.Content.ReadAsStringAsync();

        before.StatusCode.Should().Be(HttpStatusCode.OK, beforeBody);
        after.StatusCode.Should().Be(HttpStatusCode.OK, afterBody);
        using var beforeStyle = JsonDocument.Parse(beforeBody);
        using var afterStyle = JsonDocument.Parse(afterBody);
        beforeStyle.RootElement.GetProperty("name").GetString().Should().Be("publication-style");
        afterStyle.RootElement.GetProperty("name").GetString().Should().Be("publication-style");
        await _styles.Received().GetStyleAsync(Arg.Any<MetadataV2Resource>(), OriginalStorageLayerId, Arg.Any<CancellationToken>());
        await _styles.Received().GetStyleAsync(Arg.Any<MetadataV2Resource>(), ReboundStorageLayerId, Arg.Any<CancellationToken>());
        using var storageAlias = await _fixture.Client.GetAsync($"/api/styles/{OriginalStorageLayerId}.json");
        storageAlias.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var caseAlias = await _fixture.Client.GetAsync($"/api/styles/{Uri.EscapeDataString(PublicationId.ToUpperInvariant())}.json");
        caseAlias.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Protocol(TestProtocols.OgcApiStyles)]
    [Operation(Operations.Security)]
    [Endpoint("GET /api/styles/{publicationId}.json")]
    public async Task Style_PrivatePublication_ConcealsOrChallengesBeforeStyleRead(bool authenticated)
    {
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        SetGraph(graph with
        {
            Revision = graph.Revision + 1,
            Publications = graph.Publications.Select(publication => publication with
            {
                Metadata = publication.Metadata with { Tenant = "private-tenant" },
            }).ToArray(),
        });
        using var client = authenticated ? _fixture.CreateAdminClient() : _fixture.CreateClient();
        using var response = await client.GetAsync($"/api/styles/{Uri.EscapeDataString(PublicationId)}.json");

        response.StatusCode.Should().Be(authenticated ? HttpStatusCode.NotFound : HttpStatusCode.Unauthorized);
        await _styles.DidNotReceive().GetStyleAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiStyles)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /api/styles/{publicationId}.json")]
    public async Task Style_DistinctPublications_KeepsCachedRepresentationsSeparate()
    {
        const string secondResourceId = "second-style-resource";
        var resourceId = ConfigureDistinctStyleResources();
        var secondStyle = JsonSerializer.Deserialize<JsonElement>(
            """{"version":8,"name":"second-publication-style","sources":{},"layers":[]}""");
        _styles.GetStyleAsync(Arg.Is<MetadataV2Resource>(candidate => candidate.Metadata.Id == secondResourceId),
            Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new LayerStyleSnapshot(secondStyle, null));

        foreach (var publicationId in new[] { PublicationId, SecondPublicationId, PublicationId })
        {
            using var response = await _fixture.Client.GetAsync($"/api/styles/{Uri.EscapeDataString(publicationId)}.json");
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            using var style = JsonDocument.Parse(body);
            style.RootElement.GetProperty("name").GetString().Should().Be(
                publicationId == PublicationId ? "publication-style" : "second-publication-style");
        }
        await _styles.Received(1).GetStyleAsync(Arg.Is<MetadataV2Resource>(candidate => candidate.Metadata.Id == resourceId),
            OriginalStorageLayerId, Arg.Any<CancellationToken>());
        await _styles.Received(1).GetStyleAsync(Arg.Is<MetadataV2Resource>(candidate => candidate.Metadata.Id == secondResourceId),
            OriginalStorageLayerId, Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Admin)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /api/v1/admin/metadata/layers/{storageLayerId}/style")]
    public async Task StorageStyle_DistinctResourcesShareStorage_RejectsAmbiguousTarget()
    {
        ConfigureDistinctStyleResources();
        using var client = _fixture.CreateAdminClient();
        using var response = await client.GetAsync($"/api/v1/admin/metadata/layers/{OriginalStorageLayerId}/style");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        await _styles.DidNotReceive().GetStyleAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private string ConfigureDistinctStyleResources()
    {
        const string secondResourceId = "second-style-resource";
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var resource = graph.Resources.Single();
        var binding = graph.StorageBindings.Single();
        SetGraph(graph with
        {
            Revision = graph.Revision + 1,
            Resources = [resource, resource with
            {
                Metadata = resource.Metadata with { Id = secondResourceId, Name = secondResourceId },
            }],
            StorageBindings = [binding, binding with
            {
                Metadata = binding.Metadata with { Id = "second-style-binding", Name = "second-style-binding" },
                ResourceId = secondResourceId,
            }],
            Publications = graph.Publications.Select(publication => publication.Metadata.Id == SecondPublicationId
                ? publication with { ResourceId = secondResourceId, StorageBindingId = "second-style-binding" }
                : publication).ToArray(),
        });
        return resource.Metadata.Id;
    }
}
