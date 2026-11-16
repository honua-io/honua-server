// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class PairedPublishEndpointTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/publications/{publicationId}/{version}/{fileName}")]
    public async Task PublicationRead_PublicGeneration_CanBeSharedButRevocationPrecedesConditions()
    {
        var published = await PublishAsync(IdentityField);
        SetPublicationAccess(new AccessPolicy { AllowAnonymous = true });
        using var anonymous = _fixture.CreateClient(allowAutoRedirect: false);
        var route = $"{PublicationRoute}/{published.Version}/{_publicationId}.parquet";
        using var response = await anonymous.GetAsync(route);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.Public.Should().BeTrue();
        response.Headers.CacheControl.SharedMaxAge.Should().BeGreaterThan(TimeSpan.Zero);
        using var protectedResponse = await _client.GetAsync(route);
        protectedResponse.Headers.CacheControl!.NoStore.Should().BeTrue();

        SetPublicationAccess(new AccessPolicy { AllowAnonymous = false });
        using var conditional = new HttpRequestMessage(HttpMethod.Get, route);
        conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        using var revoked = await anonymous.SendAsync(conditional);
        revoked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        revoked.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("HEAD /api/v1/publications/{publicationId}/{version}/{fileName}")]
    [Endpoint("GET /api/v1/publications/{publicationId}/{version}/{fileName}")]
    [Endpoint("GET /api/v1/publications/{publicationId}/current.json")]
    public async Task PublicationRead_PublicGeneration_HeadRangeAndValidatorsRetainSharedPolicy()
    {
        var published = await PublishAsync(IdentityField);
        SetPublicationAccess(new AccessPolicy { AllowAnonymous = true });
        using var anonymous = _fixture.CreateClient(allowAutoRedirect: false);
        var route = $"{PublicationRoute}/{published.Version}/{_publicationId}.parquet";
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, route);
        using var head = await anonymous.SendAsync(headRequest);
        head.StatusCode.Should().Be(HttpStatusCode.OK);
        head.Headers.CacheControl!.Public.Should().BeTrue();
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, route);
        rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 7);
        rangeRequest.Headers.IfRange = new System.Net.Http.Headers.RangeConditionHeaderValue(head.Headers.ETag!);
        using var range = await anonymous.SendAsync(rangeRequest);
        range.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        range.Headers.CacheControl!.Public.Should().BeTrue();
        (await range.Content.ReadAsByteArrayAsync()).Should().HaveCount(8);
        using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, route);
        conditionalRequest.Headers.IfNoneMatch.Add(head.Headers.ETag!);
        using var conditional = await anonymous.SendAsync(conditionalRequest);
        conditional.StatusCode.Should().Be(HttpStatusCode.NotModified);
        conditional.Headers.CacheControl!.Public.Should().BeTrue();
        using var dateRequest = new HttpRequestMessage(HttpMethod.Get, route);
        dateRequest.Headers.IfModifiedSince = DateTimeOffset.UtcNow.AddDays(1);
        using var dated = await anonymous.SendAsync(dateRequest);
        dated.StatusCode.Should().Be(HttpStatusCode.OK, "artifact dates cannot validate current configuration");
        using var pointer = await anonymous.GetAsync($"{PublicationRoute}/current.json");
        pointer.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);
        pointer.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    private void SetPublicationAccess(AccessPolicy policy)
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>().SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Revision + 1,
            Resources = snapshot.Graph.Resources.Select(resource => resource with { AccessPolicy = policy }).ToArray(),
            Services = snapshot.Graph.Services.Select(service => service with { AccessPolicy = policy }).ToArray()
        }, schema: _fixture.MetadataGraphSchema);
    }
}
