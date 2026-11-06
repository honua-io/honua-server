// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed partial class FeatureServerReplicationTests
{
    [IntegrationTheory]
    [InlineData(null, true)]
    [InlineData("tenant-a", true)]
    [InlineData("another-tenant", false)]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_PublicationTenant_RestrictsLayerSelection(string? publicationTenant, bool visible)
    {
        await using var fixture = new WebAppFixture().WithTestLicense(Honua.Core.Features.Licensing.Domain.HonuaEdition.Pro);
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-a");
        fixture.ReplaceService(tenant);
        await fixture.InitializeAsync();
        fixture.UpdateV2ServiceMetadata(WebAppFixture.TestServiceId, capabilities: ["Query", "Sync"]);
        var snapshot = fixture.GetCurrentV2GraphSnapshot();
        var service = snapshot.FindService(WebAppFixture.TestServiceId)!;
        var provider = (TestMetadataV2GraphProvider)fixture.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Publications = snapshot.Graph.Publications.Select(publication =>
                publication.ServiceId == service.Metadata.Id && publication.LayerIndex == WebAppFixture.TestLayerId
                    ? publication with { Metadata = publication.Metadata with { Tenant = publicationTenant } }
                    : publication).ToArray()
        }, schema: fixture.CurrentSchema);
        fixture.GetService<IMetadataV2GraphProvider>().Should().BeSameAs(provider);
        fixture.GetCurrentV2GraphSnapshot().Graph.Publications
            .Where(publication => publication.ServiceId == service.Metadata.Id && publication.LayerIndex == WebAppFixture.TestLayerId)
            .Should().NotBeEmpty().And.OnlyContain(publication => publication.Metadata.Tenant == publicationTenant);
        tenant.ClearReceivedCalls();
        using var payload = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["replicaName"] = "PublicationTenantReplica",
            ["layers"] = "0",
            ["syncModel"] = "perReplica",
            ["f"] = "json"
        });

        using var response = await fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/createReplica", payload);
        var body = await response.Content.ReadAsStringAsync();

        _ = tenant.Received().TenantId;
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("replicaID", out _).Should().Be(visible, body);
        if (!visible)
        {
            document.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().BeOneOf(400, 403, 404);
        }
    }

}
