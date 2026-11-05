// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Admin;

namespace Honua.Db.Postgres.Tests.Features.Admin;

public sealed class PublishedServiceOrganizationTests
{
    [Fact]
    public void Apply_NewService_UsesPublicLeafForDisplayDefaults()
    {
        var graph = Graph();
        var defaults = Service() with
        {
            Metadata = Service().Metadata with { Title = "internal-parcels", Description = "Honua service 'internal-parcels'" },
            Route = "/rest/services/internal-parcels/FeatureServer"
        };
        var service = PublishedServiceOrganization.Apply(graph, defaults, Request("folder", "Parcels"));
        service.Metadata.Name.Should().Be("internal-parcels");
        service.Metadata.Title.Should().Be("Parcels");
        service.Metadata.Description.Should().NotContain("internal-parcels");
        service.Route.Should().Be(defaults.Route);
        service.FolderId.Should().Be("folder");
        service.EffectivePublicName.Should().Be("Parcels");
    }

    [Fact]
    public void Apply_NewServiceWithAuthoredMetadata_PreservesDisplayTextAndProtocolRoute()
    {
        var authored = Service() with { Route = "/ogc/features" };
        var published = PublishedServiceOrganization.Apply(Graph(), authored, Request("folder", "Parcels"));
        published.Metadata.Title.Should().Be(authored.Metadata.Title);
        published.Metadata.Description.Should().Be(authored.Metadata.Description);
        published.Route.Should().Be(authored.Route);
    }

    [Fact]
    public void Apply_ExistingServiceWithOmittedFolder_PreservesMembershipAndMetadata()
    {
        var existing = Service() with { FolderId = "folder", PublicName = "Parcels", Route = "/ogc/features" };
        var graph = Graph() with { Services = [existing] };
        var applied = PublishedServiceOrganization.Apply(graph, existing, Request());
        applied.Should().Be(existing);
    }

    [Fact]
    public void Apply_ExistingFolderServiceWithExplicitRoot_RejectsImplicitMove()
    {
        var existing = Service() with { FolderId = "folder", PublicName = "Parcels" };
        var graph = Graph() with { Services = [existing] };
        var apply = () => PublishedServiceOrganization.Apply(graph, existing, Request(null, "Parcels"));
        apply.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
    }

    [Fact]
    public void Apply_NewServiceInOtherTenantFolder_Rejects()
    {
        var service = Service() with { Metadata = Service().Metadata with { Tenant = "other" } };
        var apply = () => PublishedServiceOrganization.Apply(Graph(), service, Request("folder", "Parcels"));
        apply.Should().Throw<LayerPublishingException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("FeatureServer")]
    [InlineData("Utilities")]
    [InlineData("a/b")]
    public void Apply_InvalidRootPublicLeaf_RejectsRatherThanUsingLogicalFallback(string leaf)
    {
        var publish = () => PublishedServiceOrganization.Apply(Graph(), Service(), Request(null, leaf));
        publish.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Validation);
    }

    private static MetadataV2Graph Graph() => new()
    {
        Folders = [new() { Metadata = new() { Id = "folder", Name = "Survey" } }]
    };

    private static MetadataV2Service Service() => new()
    {
        Metadata = new() { Id = "service", Name = "internal-parcels", Title = "Custom title", Description = "Custom description" }
    };

    private static LayerPublishRequest Request() => new() { Schema = "public", Table = "parcels", LayerName = "Parcels" };

    private static LayerPublishRequest Request(string? folder, string leaf) => new()
    {
        Schema = "public",
        Table = "parcels",
        LayerName = "Parcels",
        FolderId = new(folder),
        PublicName = leaf
    };
}
