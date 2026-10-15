// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Validation;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class OgcCollectionPublicationSelectionTests
{
    [Fact]
    public void AdvertisedCollectionId_PrefersServiceLocalIdOverTitle()
    {
        var publication = Publication("pub-a", "parcels", title: "Something else", isPrimary: false);
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-a", Name = "resource-name", Title = "parcels" }
        };

        LayerValidationHelpers.AdvertisedCollectionId(publication, resource).Should().Be("parcels");
    }

    [Fact]
    public void SelectMatchingCollectionPublication_AdvertisedServiceLocalId_BeatsAnotherPublicationsTitle()
    {
        var advertised = Publication("pub-a", "parcels", title: "Parcels", isPrimary: false);
        var titleAlias = Publication("pub-b", "other", title: "parcels", isPrimary: true);

        var selected = LayerValidationHelpers.SelectMatchingCollectionPublication(
            [titleAlias, advertised],
            publication => string.Equals(
                LayerValidationHelpers.AdvertisedCollectionId(publication, null),
                "parcels",
                StringComparison.OrdinalIgnoreCase),
            static _ => true,
            static _ => true,
            static _ => false);

        selected.Should().NotBeNull();
        selected!.Metadata.Id.Should().Be("pub-a");
    }

    [Fact]
    public void SelectMatchingCollectionPublication_ExactMetadataId_DoesNotBindATitleAlias()
    {
        var advertised = Publication("pub-a", "parcels", title: "Parcels", isPrimary: true);
        var exact = Publication("pub-b", "other", title: "pub-b", isPrimary: false);

        var selected = LayerValidationHelpers.SelectMatchingCollectionPublication(
            [advertised, exact],
            publication => string.Equals(publication.Metadata.Id, "pub-b", StringComparison.OrdinalIgnoreCase),
            static _ => true,
            static _ => true,
            static _ => false);

        selected.Should().NotBeNull();
        selected!.Metadata.Id.Should().Be("pub-b");
    }

    private static MetadataV2Publication Publication(string id, string serviceLocalId, string title, bool isPrimary)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id, Title = title },
            Identifier = new MetadataV2PublicationIdentifier { Value = serviceLocalId },
            IsPrimary = isPrimary,
            ResourceId = id,
            ServiceId = "svc"
        };
}
