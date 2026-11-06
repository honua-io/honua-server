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

    private static MetadataV2GraphSnapshot Snapshot(MetadataV2Graph graph)
        => new(graph, "etag", DateTimeOffset.UnixEpoch);
}
