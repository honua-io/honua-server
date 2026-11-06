// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Classic.Wms;
using Honua.Protocols.Ogc.Classic.Wmts;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Honua.Protocols.OgcClassic.UnitTests;

[Protocol(ProtocolNames.Wms13, ProtocolNames.Wmts10)]
public sealed class PublicationTenantVisibilityTests
{
    private static readonly string[] ResourceNames = ["private-dataset", "public-dataset"];
    [UnitTheory]
    [InterfaceOperation(ProtocolNames.Wms13, "GetCapabilities")]
    [InterfaceOperation(ProtocolNames.Wmts10, "GetCapabilities")]
    [InlineData("WMS", null)]
    [InlineData("WMS", "tenant-a")]
    [InlineData("WMTS", null)]
    [InlineData("WMTS", "tenant-a")]
    public async Task GetCapabilities_ForeignPublication_OmitsLayer(string protocol, string? tenant)
    {
        var graph = CreateGraph();
        var body = await GetCapabilitiesAsync(graph, protocol, tenant);
        body.Should().NotContain("private-dataset");
        body.Should().Contain("public-dataset");
    }

    [UnitTheory]
    [InterfaceOperation(ProtocolNames.Wms13, "GetCapabilities")]
    [InterfaceOperation(ProtocolNames.Wmts10, "GetCapabilities")]
    [InlineData("WMS")]
    [InlineData("WMTS")]
    public async Task GetCapabilities_OwningTenant_IncludesLayer(string protocol)
    {
        var body = await GetCapabilitiesAsync(CreateGraph(), protocol, "tenant-b");
        body.Should().Contain("private-dataset");
        body.Should().Contain("public-dataset");
    }

    [UnitTheory]
    [InterfaceOperation(ProtocolNames.Wms13, "GetCapabilities")]
    [InterfaceOperation(ProtocolNames.Wmts10, "GetCapabilities")]
    [InlineData("WMS")]
    [InlineData("WMTS")]
    public async Task GetCapabilities_HiddenPrimary_SelectsVisiblePublication(string protocol)
    {
        var graph = CreateGraph();
        var hidden = graph.Publications[0] with { IsPrimary = true };
        var visible = hidden with
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "visible-copy", Name = "visible-copy" },
            IsPrimary = false,
            LayerIndex = 33
        };
        graph = graph with
        {
            Publications = [hidden, visible, graph.Publications[1]],
            Resources = [graph.Resources[0] with { Metadata = graph.Resources[0].Metadata with { Name = string.Empty } }, graph.Resources[1]]
        };
        var body = await GetCapabilitiesAsync(graph, protocol, "tenant-a");
        var identifiers = XDocument.Parse(body).Descendants()
            .Where(e => e.Parent?.Name.LocalName == "Layer" && e.Name.LocalName is "Name" or "Identifier").Select(e => e.Value);
        identifiers.Should().Contain("33").And.NotContain("11");
    }

    private static async Task<string> GetCapabilitiesAsync(MetadataV2Graph graph, string protocol, string? tenant)
    {
        var validator = Substitute.For<IResourceValidator>();
        validator.ValidateServiceV2Async("maps", Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(graph.Services[0]));
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns(tenant);
        await using var services = new ServiceCollection().AddLogging().AddOptions().AddValidationServices()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton(validator).AddSingleton(tenantContext)
            .AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph))
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.RouteValues["serviceId"] = "maps";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");
        context.Request.QueryString = new QueryString($"?SERVICE={protocol}&REQUEST=GetCapabilities");
        context.Response.Body = new MemoryStream();
        var result = protocol == "WMS"
            ? await WmsRequestHandlers.HandleWms(context)
            : await WmtsRequestHandlers.HandleWmts(context);
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        context.Response.StatusCode.Should().Be(200, body);
        return body;
    }

    private static MetadataV2Graph CreateGraph()
    {
        var active = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active };
        var resources = ResourceNames.Select(name => new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = name, Name = name, Title = name },
            Type = MetadataV2ResourceType.FeatureDataset, Status = active,
            AccessPolicy = new AccessPolicy { AllowAnonymous = true },
            SchemaFields = [new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry }],
            Spatial = new MetadataV2ResourceSpatial { GeometryType = MetadataV2GeometryType.Point },
            StorageBindingIds = [name + "-binding"]
        }).ToArray();
        return new MetadataV2Graph
        {
            Services = [new MetadataV2Service
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "maps", Name = "maps" },
                Status = active, Protocols = [ServiceProtocols.Wms, ServiceProtocols.Wmts],
                AccessPolicy = new AccessPolicy { AllowAnonymous = true }
            }],
            Resources = resources,
            StorageBindings = resources.Select((r, i) => new MetadataV2StorageBinding
            {
                Metadata = new MetadataV2ObjectMetadata { Id = r.Metadata.Id + "-binding" },
                ResourceId = r.Metadata.Id, Status = active,
                StorageType = MetadataV2StorageType.RelationalTable, StorageLayerId = i + 1,
                Locator = r.Metadata.Id
            }).ToArray(),
            Publications = resources.Select((r, i) => new MetadataV2Publication
            {
                Metadata = new MetadataV2ObjectMetadata { Id = r.Metadata.Id + "-pub", Tenant = i == 0 ? "tenant-b" : null },
                ServiceId = "maps", ResourceId = r.Metadata.Id, StorageBindingId = r.Metadata.Id + "-binding",
                Status = active, LayerIndex = (i + 1) * 11,
                PublicationType = MetadataV2PublicationType.EsriMapLayer
            }).ToArray()
        };
    }
}
