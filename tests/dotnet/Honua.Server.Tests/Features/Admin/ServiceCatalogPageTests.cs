// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Admin;

public sealed class ServiceCatalogPageTests
{
    private const int CatalogSize = 2_000;

    [UnitTest]
    public void DefaultPage_ReturnsAtMostOneHundred_AndLayerTotalFromTheSameSnapshot()
    {
        var page = ServiceCatalogPage.Create(Catalog(CatalogSize), new AdminListQuery(1, CatalogListLimits.DefaultPageSize, null));

        page.Page.Should().Be(1);
        page.PageSize.Should().Be(CatalogListLimits.DefaultPageSize);
        page.Items.Should().HaveCount(CatalogListLimits.DefaultPageSize);
        page.Total.Should().Be(CatalogSize);
        page.Items.Sum(item => item.LayerCount).Should().Be(CatalogListLimits.DefaultPageSize);
        page.LayerTotal.Should().Be(CatalogSize);
        page.Items.Select(item => item.ServiceName).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    [UnitTest]
    public void LaterPage_KeepsTheFullLayerTotal()
    {
        var page = ServiceCatalogPage.Create(Catalog(CatalogSize), new AdminListQuery(2, CatalogListLimits.DefaultPageSize, null));

        page.Items.Should().HaveCount(CatalogListLimits.DefaultPageSize);
        page.Items.Sum(item => item.LayerCount).Should().Be(CatalogListLimits.DefaultPageSize);
        page.Total.Should().Be(CatalogSize);
        page.LayerTotal.Should().Be(CatalogSize);
    }

    [UnitTest]
    public void NameQuery_ReturnsTheNamedServiceWithoutTheRest()
    {
        var page = ServiceCatalogPage.Create(Catalog(CatalogSize), new AdminListQuery(1, CatalogListLimits.DefaultPageSize, "NEEDLE-SERVICE"));

        page.Total.Should().Be(1);
        page.LayerTotal.Should().Be(1);
        page.Items.Should().ContainSingle(item => item.ServiceName == "needle-service");
    }

    [UnitTest]
    public void PagePastTheEnd_IsEmptyAndKeepsTheTotal()
    {
        var page = ServiceCatalogPage.Create(Catalog(CatalogSize), new AdminListQuery(21, CatalogListLimits.DefaultPageSize, null));

        page.Items.Should().BeEmpty();
        page.Total.Should().Be(CatalogSize);
        page.LayerTotal.Should().Be(CatalogSize);
    }

    [UnitTest]
    public void FolderId_KeepsTotalsAndFolderFieldsForThatFolder()
    {
        var page = ServiceCatalogPage.Create(FolderCatalog(), new AdminListQuery(1, CatalogListLimits.DefaultPageSize, null, "folder-1"));

        page.Total.Should().Be(1);
        page.LayerTotal.Should().Be(1);
        var service = page.Items.Should().ContainSingle().Subject;
        service.ServiceName.Should().Be("inside");
        service.FolderId.Should().Be("folder-1");
        service.FolderName.Should().Be("Survey");
    }

    [UnitTest]
    public void UnknownFolder_IsMissing_AndRootIsNot()
    {
        var snapshot = FolderCatalog();

        ServiceCatalogPage.FolderIsMissing(snapshot, "missing", null).Should().BeTrue();
        ServiceCatalogPage.FolderIsMissing(snapshot, "root", null).Should().BeFalse();
        ServiceCatalogPage.FolderIsMissing(snapshot, "folder-1", null).Should().BeFalse();
    }

    [UnitTest]
    public void SameDisplayName_UnionsProtocolsAndDistinctResources()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("res-a", "a")
            .AddResource("res-b", "b")
            .AddService("svc-fs", "shared", protocols: ["FeatureServer"])
            .AddService("svc-ms", "shared", protocols: ["MapServer"])
            .AddPublication("pub-a", "svc-fs", "res-a")
            .AddPublication("pub-b", "svc-ms", "res-b")
            .Build();

        var page = ServiceCatalogPage.Create(Snapshot(graph), new AdminListQuery(1, CatalogListLimits.DefaultPageSize, null));

        page.Total.Should().Be(1);
        page.LayerTotal.Should().Be(2);
        var service = page.Items.Should().ContainSingle().Subject;
        service.LayerCount.Should().Be(2);
        service.EnabledProtocols.Should().BeEquivalentTo(["FeatureServer", "MapServer"]);
    }

    [UnitTest]
    public void UnlistedProtocols_WithoutRasterStorage_OmitRasterProtocolsButKeepExplicitOnes()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("res-a", "a")
            .AddService("svc-default", "a-default")
            .AddService("svc-explicit", "b-explicit", protocols: ["FeatureServer", "Terrain"])
            .AddPublication("pub-a", "svc-default", "res-a")
            .AddPublication("pub-b", "svc-explicit", "res-a")
            .Build();
        var scope = new ServiceListScope { UnlistedProtocols = ServiceProtocols.WithoutRasterStorage };

        var page = ServiceCatalogPage.Create(Snapshot(graph), new AdminListQuery(1, CatalogListLimits.DefaultPageSize, null), scope);

        var unlisted = page.Items.Single(item => item.ServiceName == "a-default").EnabledProtocols;
        unlisted.Should().Contain("ImageServer").And.NotContain(ServiceProtocols.RequiringRasterStorage);
        page.Items.Single(item => item.ServiceName == "b-explicit").EnabledProtocols
            .Should().BeEquivalentTo(["FeatureServer", "Terrain"]);
        ServiceCatalogPage.Create(Snapshot(graph), new AdminListQuery(1, CatalogListLimits.DefaultPageSize, null))
            .Items.Single(item => item.ServiceName == "a-default").EnabledProtocols.Should().Contain("Terrain");
    }

    [UnitTest]
    public void LayerScope_FiltersBeforePaging_AndKeepsEveryServiceExposure()
    {
        const string connection = "11111111-1111-1111-1111-111111111111";
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("target", "target")
            .AddResource("other", "other")
            .AddStorageBinding("target-binding", "target", "public.target", connectionId: connection, storageLayerId: 0)
            .AddStorageBinding("other-binding", "other", "public.other", connectionId: "22222222-2222-2222-2222-222222222222", storageLayerId: 0)
            .AddService("unrelated", "a-unrelated")
            .AddService("first", "b-first")
            .AddService("second", "c-second")
            .AddPublication("other-pub", "unrelated", "other")
            .AddPublication("first-pub", "first", "target")
            .AddPublication("second-pub", "second", "target")
            .Build();
        var values = new Microsoft.AspNetCore.Http.QueryCollection(
            Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery($"connectionId={connection}&storageLayerId=0&pageSize=1&page=2"));
        AdminListQueryParser.TryParse(values, out var query, out var error).Should().BeTrue(error);

        var page = ServiceCatalogPage.Create(Snapshot(graph), query);

        page.Total.Should().Be(2);
        page.Items.Should().ContainSingle(item => item.ServiceName == "c-second");
    }

    [UnitTheory]
    [InlineData("service")]
    [InlineData("publication")]
    [InlineData("resource")]
    [InlineData("binding")]
    public void LayerScope_DoesNotMatchHiddenTenantEntities(string hidden)
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("target", "target")
            .AddStorageBinding("binding", "target", "public.target", connectionId: "11111111-1111-1111-1111-111111111111", storageLayerId: 0)
            .AddService("service", "target")
            .AddPublication("publication", "service", "target")
            .Build();
        graph = graph with
        {
            Services = graph.Services.Select(value => hidden == "service" ? value with { Metadata = value.Metadata with { Tenant = "private" } } : value).ToArray(),
            Publications = graph.Publications.Select(value => hidden == "publication" ? value with { Metadata = value.Metadata with { Tenant = "private" } } : value).ToArray(),
            Resources = graph.Resources.Select(value => hidden == "resource" ? value with { Metadata = value.Metadata with { Tenant = "private" } } : value).ToArray(),
            StorageBindings = graph.StorageBindings.Select(value => hidden == "binding" ? value with { Metadata = value.Metadata with { Tenant = "private" } } : value).ToArray(),
        };
        var query = new AdminListQuery(1, 100, null)
        {
            ConnectionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            StorageLayerId = 0,
        };

        ServiceCatalogPage.Create(Snapshot(graph), query).Total.Should().Be(0);
        ServiceCatalogPage.Create(Snapshot(graph), query, "private").Total.Should().Be(1);
    }

    [UnitTest]
    public void LayerScope_PreservesProtocolEntriesForTheSameLogicalService()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("target", "target")
            .AddResource("other", "other")
            .AddStorageBinding("binding", "target", "public.target", connectionId: "11111111-1111-1111-1111-111111111111", storageLayerId: 0)
            .AddService("features", "shared", protocols: ["FeatureServer"])
            .AddService("maps", "shared", protocols: ["MapServer"])
            .AddPublication("target-pub", "features", "target")
            .AddPublication("other-pub", "maps", "other")
            .Build();
        var page = ServiceCatalogPage.Create(Snapshot(graph), new AdminListQuery(1, 100, null)
        {
            ConnectionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            StorageLayerId = 0,
        });

        var service = page.Items.Should().ContainSingle().Subject;
        service.EnabledProtocols.Should().BeEquivalentTo(["FeatureServer", "MapServer"]);
        service.LayerCount.Should().Be(2);
    }

    [UnitTheory]
    [InlineData("11111111-1111-1111-1111-111111111111", 1)]
    [InlineData("22222222-2222-2222-2222-222222222222", 0)]
    [InlineData(null, 0)]
    public void LayerScope_ManagedStorageRequiresTheResolvedOwner(string? owner, int expectedTotal)
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("target", "target")
            .AddStorageBinding("binding", "target", "public.target", storageLayerId: 0)
            .AddService("service", "target")
            .AddPublication("publication", "service", "target")
            .Build();
        var query = new AdminListQuery(1, 100, null)
        {
            ConnectionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            StorageLayerId = 0,
            ManagedConnectionId = owner is null ? null : Guid.Parse(owner),
        };

        ServiceCatalogPage.Create(Snapshot(graph), query).Total.Should().Be(expectedTotal);
    }

    private static MetadataV2GraphSnapshot Catalog(int count)
    {
        var builder = new TestMetadataV2GraphBuilder().AddResource("res-1", "resource");
        for (var index = 0; index < count; index++)
        {
            var name = index == 0 ? "needle-service" : $"svc-{index:D4}";
            builder
                .AddService($"svc-{index}", name, protocols: ["FeatureServer"])
                .AddPublication($"pub-{index}", $"svc-{index}", "res-1");
        }

        return Snapshot(builder.Build());
    }

    private static MetadataV2GraphSnapshot FolderCatalog()
    {
        var built = new TestMetadataV2GraphBuilder()
            .AddResource("res-1", "resource")
            .AddService("in-folder", "inside", protocols: ["FeatureServer"])
            .AddService("at-root", "outside", protocols: ["FeatureServer"])
            .AddPublication("pub-in", "in-folder", "res-1")
            .AddPublication("pub-out", "at-root", "res-1")
            .Build();
        var graph = built with
        {
            Folders =
            [
                new MetadataV2Folder
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "folder-1", Name = "Survey" },
                },
            ],
            Services = built.Services.Select(service => service.Metadata.Id == "in-folder"
                ? service with { FolderId = "folder-1" }
                : service).ToArray(),
        };
        return Snapshot(graph);
    }

    private static MetadataV2GraphSnapshot Snapshot(MetadataV2Graph graph)
        => new(graph, "etag", DateTimeOffset.UnixEpoch);
}
