// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.TestKit;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

public sealed class OgcFeaturesCollectionsExceptionFilterTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;

    public OgcFeaturesCollectionsExceptionFilterTests()
    {
        var validator = Substitute.For<IResourceValidator>();
        validator.ValidateCollectionV2Async(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ResourceValidationResult<MetadataV2Resource>>(
                new ArgumentException("Invalid Parse failure")));

        _fixture = new WebAppFixture()
            .ReplaceService<IResourceValidator>(validator);
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task GetCollection_WhenCatalogThrowsArgumentException_Returns500()
    {
        var response = await _fixture.Client.GetAsync("/ogc/features/collections/0");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotContain("Invalid Parse failure");
    }

    [Fact]
    public async Task GetQueryables_WhenCatalogThrowsArgumentException_Returns500()
    {
        var response = await _fixture.Client.GetAsync("/ogc/features/collections/0/queryables");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotContain("Invalid Parse failure");
    }
}
