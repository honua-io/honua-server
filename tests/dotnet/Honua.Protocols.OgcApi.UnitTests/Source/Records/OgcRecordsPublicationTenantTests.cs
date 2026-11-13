// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Api.Records;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Records;

[Protocol(TestProtocols.OgcApiRecords)]
public sealed class OgcRecordsPublicationTenantTests
{
    [UnitTheory]
    [InlineData(null)]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public async Task Records_PublicationScopedTenant_OnlyMatchingTenantSeesRecords(string? tenantId)
    {
        using var services = CreateServices(tenantId, includeVisibleSecondary: false);
        using var response = await SearchAsync(services);
        var ids = response.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("id").GetString()).ToArray();

        ids.Should().Contain("layer:public-publication");
        if (tenantId == "tenant-b")
        {
            ids.Should().Contain("layer:private-publication").And.Contain("service:private-service");
        }
        else
        {
            ids.Should().NotContain("layer:private-publication").And.NotContain("service:private-service");
            using var item = await GetItemAsync(services, "layer:private-publication");
            item.Status.Should().Be(StatusCodes.Status404NotFound);
        }
    }

    [UnitTest]
    public async Task Records_HiddenPrimaryAndVisibleSecondary_UsesVisiblePublication()
    {
        using var services = CreateServices("tenant-a", includeVisibleSecondary: true);
        using var response = await SearchAsync(services);
        var feature = response.RootElement.GetProperty("features").EnumerateArray()
            .Single(value => value.GetProperty("id").GetString() == "layer:visible-publication");

        feature.GetRawText().Should().Contain("visible-service").And.NotContain("private-service");
        using var item = await GetItemAsync(services, "layer:visible-publication");
        item.Status.Should().Be(StatusCodes.Status200OK);
        item.Body.RootElement.GetRawText().Should().Contain("visible-service").And.NotContain("private-service");
    }

    private static ServiceProvider CreateServices(string? tenantId, bool includeVisibleSecondary)
    {
        var builder = new TestMetadataV2GraphBuilder()
            .AddResource("private-resource", "private-dataset", accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddResource("public-resource", "public-dataset", accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddStorageBinding("private-binding", "private-resource", "private", storageLayerId: 1)
            .AddStorageBinding("public-binding", "public-resource", "public", storageLayerId: 2)
            .AddService("private-service", "private-service", protocols: ["FeatureServer"], accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddService("public-service", "public-service", protocols: ["FeatureServer"], accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddPublication("private-publication", "private-service", "private-resource", layerIndex: 1, storageBindingId: "private-binding", isPrimary: true)
            .AddPublication("public-publication", "public-service", "public-resource", layerIndex: 2, storageBindingId: "public-binding");
        if (includeVisibleSecondary)
        {
            builder.AddService("visible-service", "visible-service", protocols: ["FeatureServer"], accessPolicy: new AccessPolicy { AllowAnonymous = true })
                .AddPublication("visible-publication", "visible-service", "private-resource", layerIndex: 1, storageBindingId: "private-binding");
        }
        var graph = builder.Build();
        graph = graph with
        {
            Publications = graph.Publications.Select(publication => publication.Metadata.Id == "private-publication"
                ? publication with { Metadata = publication.Metadata with { Tenant = "tenant-b" } }
                : publication).ToArray()
        };
        MetadataV2GraphValidator.Validate(graph).IsValid.Should().BeTrue();
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(tenantId);
        return new ServiceCollection().AddLogging().AddValidationServices()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<ITenantContext>(tenant)
            .AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph))
            .BuildServiceProvider();
    }

    private static async Task<JsonDocument> SearchAsync(ServiceProvider services)
    {
        var context = Context(services);
        var provider = services.GetRequiredService<IMetadataV2GraphProvider>();
        var method = typeof(OgcRecordsEndpoints).GetMethod("HandleGetItems", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = await (Task<IResult>)method.Invoke(null,
            ["honua-catalog", context, null, null, null, 100, null, null, null, null, null, provider])!;
        await result.ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(context.Response.Body);
    }

    private static async Task<ItemResponse> GetItemAsync(ServiceProvider services, string id)
    {
        var context = Context(services);
        var provider = services.GetRequiredService<IMetadataV2GraphProvider>();
        var method = typeof(OgcRecordsEndpoints).GetMethod("HandleGetItem", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = await (Task<IResult>)method.Invoke(null, ["honua-catalog", id, context, null, provider])!;
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return new ItemResponse(context.Response.StatusCode, await JsonDocument.ParseAsync(context.Response.Body));
    }

    private static DefaultHttpContext Context(IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("honua.example");
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed record ItemResponse(int Status, JsonDocument Body) : IDisposable
    {
        public void Dispose() => Body.Dispose();
    }
}
