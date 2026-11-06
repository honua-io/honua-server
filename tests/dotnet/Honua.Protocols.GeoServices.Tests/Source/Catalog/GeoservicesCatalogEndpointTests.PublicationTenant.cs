// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

public sealed partial class GeoservicesCatalogEndpointTests
{
    [IntegrationTheory]
    [InlineData(null, true)]
    [InlineData("tenant-a", true)]
    [InlineData("another-tenant", false)]
    [Operation(Operations.Security)]
    [Endpoint("GET /rest/services")]
    public async Task ServicesDirectory_PublicationTenant_FiltersFeatureMapEntriesAndRetainsPublicGp(string? publicationTenant, bool visible)
    {
        await using var fixture = new WebAppFixture().WithTestLicense(Honua.Core.Features.Licensing.Domain.HonuaEdition.Pro);
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-a");
        fixture.ReplaceService(tenant);
        await fixture.InitializeAsync();
        var snapshot = fixture.GetCurrentV2GraphSnapshot();
        var provider = (TestMetadataV2GraphProvider)fixture.GetService<IMetadataV2GraphProvider>();
        var serviceIds = snapshot.Graph.Services.Where(service => service.Metadata.Name == WebAppFixture.TestServiceId)
            .Select(service => service.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        serviceIds.Should().NotBeEmpty();
        provider.SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Publications = snapshot.Graph.Publications.Select(publication => serviceIds.Contains(publication.ServiceId)
                ? publication with { Metadata = publication.Metadata with { Tenant = publicationTenant } }
                : publication).ToArray()
        }, schema: fixture.CurrentSchema);
        fixture.GetService<IMetadataV2GraphProvider>().Should().BeSameAs(provider);
        fixture.GetCurrentV2GraphSnapshot().Graph.Publications
            .Where(publication => serviceIds.Contains(publication.ServiceId))
            .Should().NotBeEmpty().And.OnlyContain(publication => publication.Metadata.Tenant == publicationTenant);
        tenant.ClearReceivedCalls();
        try
        {
            using var response = await fixture.Client.GetAsync("/rest/services?f=json");
            _ = tenant.Received().TenantId;
            response.Be200Ok();
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var entries = document.RootElement.GetProperty("services").EnumerateArray()
                .Where(entry => entry.GetProperty("name").GetString() == WebAppFixture.TestServiceId).ToArray();
            foreach (var type in new[] { "FeatureServer", "MapServer" })
            {
                entries.Any(entry => entry.GetProperty("type").GetString() == type).Should().Be(visible, body);
            }
            entries.Any(entry => entry.GetProperty("type").GetString() == "GPServer").Should().BeTrue(body);
        }
        finally
        {
            provider.SetGraph(snapshot.Graph with { Revision = snapshot.Graph.Revision + 2 }, schema: fixture.CurrentSchema);
        }
    }

}
