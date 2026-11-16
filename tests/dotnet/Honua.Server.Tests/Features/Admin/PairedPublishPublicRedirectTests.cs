// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Admin.PairedPublications;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class PairedPublishEndpointTests
{
    private PublicPublicationGeneration? _publicGeneration;
    private readonly Mock<IPublicPublicationJournal> _publicJournal = new(MockBehavior.Strict);

    [IntegrationTheory]
    [Endpoint("GET /api/v1/publications/{publicationId}/{version}/{fileName}")]
    [Endpoint("HEAD /api/v1/publications/{publicationId}/{version}/{fileName}")]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task PublicationRead_ReadyPublicCopy_RedirectsAfterAuthorization(string method)
    {
        var published = await PublishAsync(IdentityField);
        SetPublicationAccess(new AccessPolicy { AllowAnonymous = true });
        AdvertisePublicGeneration(published.Version);
        using var anonymous = _fixture.CreateClient(allowAutoRedirect: false);
        var files = new[] { "descriptor.json", "item.json", $"{_publicationId}.pmtiles", $"{_publicationId}.parquet" };
        foreach (var file in files)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), $"{PublicationRoute}/{published.Version}/{file}");
            using var response = await anonymous.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location.Should().Be(new Uri($"https://data.example/data/publications/{_publicationId}/{published.Version}/{file}"));
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
        _publicJournal.Invocations.Clear();
        SetPublicationAccess(new AccessPolicy { AllowAnonymous = false });
        using var deniedRequest = new HttpRequestMessage(new HttpMethod(method), $"{PublicationRoute}/{published.Version}/descriptor.json");
        using var denied = await anonymous.SendAsync(deniedRequest);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        denied.Headers.Location.Should().BeNull();
        denied.Headers.CacheControl!.NoStore.Should().BeTrue();
        _publicJournal.Verify(j => j.OpenAsync(It.IsAny<PublicPublicationKey>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private void AdvertisePublicGeneration(string version)
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var source = PMTilesProxySourceResolver.ResolveForPublish(
            snapshot, WebAppFixture.TestServiceId, WebAppFixture.TestLayerId)!;
        _publicGeneration = new()
        {
            Reservation = new()
            {
                Key = new(_publicationId, version), ServiceId = source.Service.EffectivePublicName,
                LayerIndex = source.Publication.LayerIndex!.Value, MetadataRevision = snapshot.Revision,
                DestinationBucket = "public", DestinationBaseUrl = "https://data.example", CdnDistributionId = "cdn"
            },
            State = PublicPublicationState.Ready, PurgeIds = []
        };
    }

    private void ConfigurePublicationAdvertisement(IWebHostBuilder builder)
    {
        var session = new Mock<IPublicPublicationSession>(MockBehavior.Strict);
        session.Setup(s => s.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _publicGeneration);
        session.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _publicJournal.Setup(j => j.OpenAsync(It.IsAny<PublicPublicationKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);
        var store = new Mock<IMetadataV2GraphStore>(MockBehavior.Strict);
        store.Setup(s => s.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _fixture.GetCurrentV2GraphSnapshot());
        var target = new PublicPublicationCleanupTarget
        {
            Bucket = "public", BaseUrl = "https://data.example", DistributionId = "cdn",
            Destination = Mock.Of<IPublicPublicationDestination>(), Invalidator = Mock.Of<ICdnInvalidator>(),
            StatusReader = Mock.Of<ICdnInvalidationStatusReader>()
        };
        builder.ConfigureTestServices(services => services.AddScoped(provider => new PublicPublicationAdvertisement(
            _publicJournal.Object, new(store.Object, ActivatorUtilities.CreateInstance<PublicPublicationEligibility>(provider)), target)));
    }
}
