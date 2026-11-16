// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTest]
    public async Task PairedPublication_ManagedPublish_PersistsAutomaticRecipe()
    {
        await UseServerFeatureStoreConnectionAsync();
        var recipe = new MetadataV2PairedPublication
        {
            PublicationId = "managed-roads", IdentityField = "name", MaxZoom = 3, MaxTiles = 80
        };
        var request = RevisionPinnedPublication(null, pairedPublication: recipe);

        _layerId = (await PublishRevisionPinnedAsync(request)).StorageLayerId;

        using var scope = _fixture.Services.CreateScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>().GetCurrentAsync();
        persisted.Graph.Publications.Where(layer => layer.PairedPublication is not null)
            .Should().ContainSingle().Which.PairedPublication.Should().Be(recipe);
    }

    [IntegrationTheory]
    [InlineData("valid-roads", "population", 0, 3)]
    [InlineData("../invalid", "name", 0, 3)]
    [InlineData("valid-roads", "name", 4, 3)]
    public async Task PairedPublication_InvalidRecipe_RejectsWithoutPublishing(
        string publicationId, string identityField, int minZoom, int maxZoom)
    {
        await UseServerFeatureStoreConnectionAsync();
        var recipe = new MetadataV2PairedPublication
        {
            PublicationId = publicationId, IdentityField = identityField, MinZoom = minZoom, MaxZoom = maxZoom
        };
        var publish = () => PublishRevisionPinnedAsync(RevisionPinnedPublication(null, pairedPublication: recipe));

        (await publish.Should().ThrowAsync<LayerPublishingException>()).Which.ErrorKind
            .Should().Be(LayerPublishingErrorKind.Validation);
        (await GetPublishedLayerRowCountAsync()).Should().Be(0);
    }
}
