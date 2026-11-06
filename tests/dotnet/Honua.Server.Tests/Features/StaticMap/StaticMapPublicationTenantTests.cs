// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using NSubstitute;

namespace Honua.Server.Tests.Features.StaticMap;

[Collection("Database")]
[Protocol(TestProtocols.StaticMap)]
public sealed class StaticMapPublicationTenantTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private readonly IFeatureReader _reader = Substitute.For<IFeatureReader>();

    public async Task InitializeAsync()
    {
        _reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(QueryResult<Feature>.Empty()));
        _fixture.ReplaceService(_reader);
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Security)]
    [Endpoint("GET /static/{serviceId}/{center}/{dimensions}.{format}")]
    public async Task StaticMap_PublicationTenant_QueriesOnlyVisibleAliases(bool includeVisibleAlias)
    {
        var validator = _fixture.GetService<IResourceValidator>();
        var serviceResult = await validator.ValidateServiceV2Async(WebAppFixture.TestServiceId, ServiceProtocols.MapServer);
        serviceResult.IsValid.Should().BeTrue();
        var service = serviceResult.Resource!;
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var original = snapshot.PublicationsForService(service.Metadata.Id)
            .First(publication => publication.LayerIndex == WebAppFixture.TestLayerId);
        var publications = snapshot.Graph.Publications.Select(publication => publication.ServiceId == service.Metadata.Id
            ? publication with { Metadata = publication.Metadata with { Tenant = "another-tenant" } }
            : publication).ToList();
        if (includeVisibleAlias)
        {
            publications.Add(original with
            {
                Metadata = original.Metadata with { Id = original.Metadata.Id + "-visible", Tenant = null },
                LayerIndex = 17,
                IsPrimary = false
            });
        }
        var provider = (TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(snapshot.Graph with { Revision = snapshot.Graph.Revision + 1, Publications = publications }, schema: _fixture.CurrentSchema);
        _reader.ClearReceivedCalls();

        using var response = await _fixture.Client.GetAsync($"/static/{WebAppFixture.TestServiceId}/-122.4194,37.7749,12/64x64.png");

        if (includeVisibleAlias)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        }
        await _reader.Received(includeVisibleAlias ? 1 : 0)
            .QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }
}
