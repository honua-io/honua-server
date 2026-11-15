// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Publishing.Descriptors;
using Honua.Infrastructure.Authentication;
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

    [IntegrationTheory]
    [Endpoint("GET /api/v1/publications/{publicationId}/current.json")]
    [Endpoint("HEAD /api/v1/publications/{publicationId}/current.json")]
    [Endpoint("GET /api/v1/publications/{publicationId}/{version}/{fileName}")]
    [Endpoint("HEAD /api/v1/publications/{publicationId}/{version}/{fileName}")]
    [InlineData("GET", false)]
    [InlineData("HEAD", false)]
    [InlineData("GET", true)]
    [InlineData("HEAD", true)]
    public async Task PublicationRead_CurrentCallerPolicy_RefusesCurrentAndArchivedArtifacts(string method, bool restrictRows)
    {
        var first = await PublishAsync(IdentityField);
        var second = await PublishAsync(IdentityField);
        var route = $"{PublicationRoute}/{first.Version}/{_publicationId}.parquet";
        using var before = await _client.GetAsync(route);
        before.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var rows = scope.ServiceProvider.GetRequiredService<IRlsPolicyStore>();
        var fields = scope.ServiceProvider.GetRequiredService<IFieldMaskPolicyStore>();
        var resource = _fixture.GetCurrentV2GraphSnapshot().Index.ResourcesByStorageLayerId[WebAppFixture.TestLayerId];
        var policyId = Guid.NewGuid();
        try
        {
            if (restrictRows)
            {
                await rows.CreatePolicyAsync(new RlsPolicy
                {
                    PolicyId = policyId, Role = "admin", Service = "*", Layer = resource.Metadata.Name,
                    Attribute = IdentityField, ClaimType = "publication-test-missing-claim"
                });
            }
            else
            {
                await fields.CreatePolicyAsync(new FieldMaskPolicy
                {
                    PolicyId = policyId, Role = "admin", Service = "*", Layer = resource.Metadata.Name,
                    Attribute = IdentityField
                });
            }

            var suffixes = new[] { "current.json" }.Concat(new[] { first.Version, second.Version }
                .SelectMany(version => new[] { "descriptor.json", "item.json", $"{_publicationId}.pmtiles", $"{_publicationId}.parquet" }
                    .Select(file => $"{version}/{file}")));
            foreach (var suffix in suffixes)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), $"{PublicationRoute}/{suffix}");
                request.Headers.TryAddWithoutValidation("If-None-Match", "*");
                request.Headers.Range = new RangeHeaderValue(0, 15);
                using var response = await _client.SendAsync(request);
                response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                response.Headers.CacheControl!.NoStore.Should().BeTrue();
                response.Headers.Location.Should().BeNull();
                response.Content.Headers.ContentRange.Should().BeNull();
            }
        }
        finally
        {
            if (restrictRows)
            {
                await rows.DeletePolicyAsync(policyId);
            }
            else
            {
                await fields.DeletePolicyAsync(policyId);
            }
        }

        using var after = await _client.GetAsync(route);
        after.StatusCode.Should().Be(HttpStatusCode.OK);
    }

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
    [Endpoint("GET /api/v1/publications/{publicationId}/current.json")]
    [Endpoint("HEAD /api/v1/publications/{publicationId}/current.json")]
    [Endpoint("GET /api/v1/publications/{publicationId}/{version}/{fileName}")]
    [Endpoint("HEAD /api/v1/publications/{publicationId}/{version}/{fileName}")]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task PublicationRead_ImplicitObjectId_RemainsReadableUntilExplicitlyHidden(string method)
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var resource = snapshot.Index.ResourcesByStorageLayerId[WebAppFixture.TestLayerId];
        var objectId = resource.FindPrimaryIdField()!;
        _fixture.RestoreV2ResourceSchemaFields(WebAppFixture.TestLayerId,
            resource.SchemaFields.Where(field => field.Name != objectId.Name).ToArray());
        var published = await PublishAsync(IdentityField);
        var descriptor = await ReadJsonAsync<PublicationDescriptor>(published.DescriptorKey);
        descriptor.Artifacts.Pmtiles.Fields.Should().Contain("objectid");
        descriptor.Artifacts.Geoparquet.Fields.Should().Contain("objectid");
        using var client = _fixture.CreateClient(allowAutoRedirect: false);
        client.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword);
        foreach (var suffix in new[] { "current.json", $"{published.Version}/descriptor.json", $"{published.Version}/{_publicationId}.pmtiles", $"{published.Version}/{_publicationId}.parquet" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), $"{PublicationRoute}/{suffix}");
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(suffix == "current.json" ? HttpStatusCode.TemporaryRedirect : HttpStatusCode.OK);
        }

        _fixture.UpdateV2ResourceSchemaField(WebAppFixture.TestLayerId, objectId with { Hidden = true });
        foreach (var suffix in new[] { "current.json", $"{published.Version}/descriptor.json", $"{published.Version}/{_publicationId}.pmtiles", $"{published.Version}/{_publicationId}.parquet" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), $"{PublicationRoute}/{suffix}");
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
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

    [IntegrationTheory]
    [Endpoint("GET /api/v1/publications/{publicationId}/current.json")]
    [Endpoint("HEAD /api/v1/publications/{publicationId}/current.json")]
    [Endpoint("GET /api/v1/publications/{publicationId}/{version}/{fileName}")]
    [Endpoint("HEAD /api/v1/publications/{publicationId}/{version}/{fileName}")]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task PublicationRead_DeniedExport_IsRefusedBeforeArchivedVersionExistence(string method)
    {
        var published = await PublishAsync(IdentityField);
        var issued = await _fixture.Services.GetRequiredService<IAdminApiKeyStore>().CreateAsync(
            "publication-read-denied", ["read:unrelated-service/*"], DateTimeOffset.UtcNow.AddMinutes(5),
            "publication-read-test", CancellationToken.None);
        using var denied = _fixture.CreateClient(allowAutoRedirect: false);
        denied.DefaultRequestHeaders.Add("X-API-Key", issued.Key);
        foreach (var suffix in new[] { "current.json", $"{published.Version}/descriptor.json", $"{published.Version}/{_publicationId}.parquet", "missing/descriptor.json", $"missing/{_publicationId}.parquet" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), $"{PublicationRoute}/{suffix}");
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            request.Headers.Range = new RangeHeaderValue(0, 15);
            using var response = await denied.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
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
