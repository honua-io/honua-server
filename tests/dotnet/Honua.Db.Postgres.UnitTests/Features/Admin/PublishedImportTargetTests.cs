// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;
using Honua.Db.Postgres.Features.Admin;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Admin;

public sealed class PublishedImportTargetTests
{
    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void BindService_ExistingFacets_RequiresExactSelectedIdentity(bool selectedFacetChanged)
    {
        var request = Request();
        var target = request.ImportTarget!;
        var planned = new MetadataV2Service
        {
            Metadata = new() { Id = target.ServiceId!, Name = target.ServiceName, Title = "Operator title" },
            FolderId = target.FolderId,
            PublicName = target.PublicName,
            Route = "/operator/configured/FeatureServer"
        };
        var other = planned with { Metadata = planned.Metadata with { Id = "other-facet" }, ServiceType = MetadataV2ServiceType.EsriMapService };
        var graph = new MetadataV2Graph { Folders = [target.CreateFolderDefinition()!], Services = [other, planned] };
        var selected = selectedFacetChanged ? other : planned;
        var bind = () => PublishedImportTarget.BindService(graph, selected, request);

        if (selectedFacetChanged)
            bind.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
        else
            bind().Should().BeSameAs(planned);
    }

    [UnitTheory]
    [InlineData("previous")]
    [InlineData("persisted")]
    [InlineData("current")]
    public void BuildRebasedCompensatingMetadataV2Graph_OmittedCollections_RestoresChangesAndPreservesConcurrentValues(string omitted)
    {
        var previousPublication = PublicationCollections(omitted == "previous" ? null : ["original"]);
        var persistedPublication = PublicationCollections(omitted == "persisted" ? null : omitted == "previous" ? ["failed"] : []);
        var currentPublication = PublicationCollections(omitted == "current" ? null : omitted == "previous" ? ["failed", "concurrent"] : ["concurrent"]);
        var previous = new MetadataV2Graph
        {
            Services = [new() { Metadata = new() { Id = "service" }, PublicationIds = ["publication"] }],
            Resources = [new() { Metadata = new() { Id = "resource" } }],
            Publications = [previousPublication]
        };
        var compensated = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            previous with { Publications = [currentPublication] }, previous,
            previous with { Publications = [persistedPublication] }, DateTimeOffset.UnixEpoch);

        var restored = compensated.Publications.Should().ContainSingle().Which;
        string[] expected = omitted switch
        {
            "previous" => ["concurrent"],
            "current" => ["original"],
            _ => ["original", "concurrent"]
        };
        restored.SupportedFormats.Should().BeEquivalentTo(expected);
        restored.Capabilities.Should().BeEquivalentTo(expected);
        restored.FieldAliases.Keys.Should().BeEquivalentTo(expected);
        restored.Options.Keys.Should().BeEquivalentTo(expected);
        restored.Extensions.Keys.Should().BeEquivalentTo(expected);
    }

    private static MetadataV2Publication PublicationCollections(string[]? values) => new()
    {
        Metadata = new() { Id = "publication" },
        ResourceId = "resource",
        ServiceId = "service",
        SupportedFormats = values!,
        Capabilities = values!,
        FieldAliases = values?.ToDictionary(value => value, value => value)!,
        Options = values?.ToDictionary(value => value, value => JsonSerializer.SerializeToElement(value))!,
        Extensions = values?.ToDictionary(value => value, value => JsonSerializer.SerializeToElement(value))!
    };

    [UnitTest]
    public void RequireReplacement_LayerBelongsToAnotherService_Refuses()
    {
        var request = Request();
        var graph = new MetadataV2Graph
        {
            Folders = [request.ImportTarget!.CreateFolderDefinition()!],
            Services = [new()
            {
                Metadata = new() { Id = "planned-service", Name = "parcels" }, PublicName = "Parcels", FolderId = "planned-folder"
            }],
            Publications = [new() { Metadata = new() { Id = "publication" }, ResourceId = "layer", ServiceId = "another-service" }]
        };
        var replace = () => PublishedImportTarget.RequireReplacement(graph, "layer", request, null);
        replace.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
    }

    [UnitTest]
    public void BindService_TargetNameOccupiedByAnotherIncarnation_Refuses()
    {
        var service = new MetadataV2Service { Metadata = new() { Id = "unrelated", Name = "parcels" } };
        var graph = new MetadataV2Graph { Services = [service] };
        var bind = () => PublishedImportTarget.BindService(graph, service, Request());
        bind.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
    }

    [UnitTest]
    public void BindService_ExistingServiceRemoved_RefusesRatherThanRecreatingIt()
    {
        var request = Request();
        var existing = request.ImportTarget! with { ExistingService = true };
        var bind = () => PublishedImportTarget.BindService(new(), new(), Request(existing));
        bind.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
    }

    [UnitTheory]
    [InlineData(true, "Survey")]
    [InlineData(false, "Renamed")]
    public void PrepareFolder_PlannedFolderDeletedOrRenamed_Refuses(bool deleted, string name)
    {
        var target = Request().ImportTarget!;
        var folder = target.CreateFolderDefinition()!;
        var graph = new MetadataV2Graph
        {
            Folders = [folder with { Deleted = deleted, Metadata = folder.Metadata with { Name = name } }]
        };
        var prepare = () => PublishedImportTarget.PrepareFolder(graph, Request(target), null);
        prepare.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
    }

    [UnitTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void BuildRebasedCompensatingMetadataV2Graph_IntroducedFolder_PreservesOnlyConcurrentUseOrChanges(
        bool concurrentMember, bool editedTitle)
    {
        var folder = Request().ImportTarget!.CreateFolderDefinition()!;
        var failed = new MetadataV2Service { Metadata = new() { Id = "failed", Name = "parcels" }, FolderId = folder.Metadata.Id };
        var previous = new MetadataV2Graph { Revision = 1 };
        var persisted = previous with { Revision = 2, Folders = [folder], Services = [failed] };
        var current = persisted with
        {
            Revision = 3,
            Folders = [editedTitle ? folder with { Metadata = folder.Metadata with { Title = "Edited title" } } : folder],
            Services = concurrentMember
                ? [failed, new() { Metadata = new() { Id = "concurrent", Name = "roads" }, FolderId = folder.Metadata.Id }]
                : [failed]
        };
        var compensated = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current, previous, persisted, DateTimeOffset.UtcNow);
        compensated.Services.Should().NotContain(service => service.Metadata.Id == "failed");
        compensated.Folders.Count.Should().Be(concurrentMember || editedTitle ? 1 : 0);
        if (concurrentMember)
            compensated.Services.Should().ContainSingle().Which.Metadata.Id.Should().Be("concurrent");
    }

    private static LayerPublishRequest Request(GeoservicesImportTarget? target = null)
    {
        target ??= new()
        {
            ServiceId = "planned-service",
            ServiceName = "parcels",
            PublicName = "Parcels",
            FolderId = "planned-folder",
            FolderName = "Survey"
        };
        return new()
        {
            Schema = "public",
            Table = "parcels",
            LayerName = "Parcels",
            ServiceName = target.ServiceName,
            PublicName = target.PublicName,
            FolderId = new(target.FolderId),
            ImportTarget = target,
            Namespace = target.Namespace
        };
    }
}
