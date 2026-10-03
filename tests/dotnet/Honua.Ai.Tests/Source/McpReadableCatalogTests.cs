// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Grounding;
using Honua.Ai.Grounding.Spec;
using Honua.Ai.Protocols.Mcp.Discovery;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Grounding.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Spec.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed class McpReadableCatalogTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("resource-policy")]
    [InlineData("service-policy")]
    [InlineData("resource-tenant")]
    [InlineData("service-tenant")]
    [InlineData("publication-tenant")]
    [InlineData("missing-tenant")]
    [InlineData("unpublished")]
    [InlineData("inactive")]
    public async Task RenderMap_UnreadableLayer_DoesNotRender(string restriction)
    {
        using var services = CreateServices(restriction);
        var context = CreateContext(services);
        var renderer = services.GetRequiredService<IRasterMapRenderer>();
        var tool = new RenderMapTool(Substitute.For<IGeoprocessingJobService>(), NullLogger<RenderMapTool>.Instance);
        using var arguments = JsonDocument.Parse("""{"layers":[{"serviceId":"svc","layerId":0}],"bbox":[0,0,1,1],"maxInlineBytes":1024}""");

        var act = () => tool.InvokeAsync(context, arguments.RootElement, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>()
            .Where(exception => exception is GeoprocessingAuthorizationException or GeoprocessingNotFoundException);
        await renderer.DidNotReceiveWithAnyArgs().RenderDatasetMapAsync(default!, default!, default);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("resource-policy")]
    [InlineData("service-policy")]
    [InlineData("resource-tenant")]
    [InlineData("service-tenant")]
    [InlineData("publication-tenant")]
    [InlineData("missing-tenant")]
    [InlineData("unpublished")]
    [InlineData("inactive")]
    [InlineData("allowed")]
    [InlineData("same-tenant")]
    [InlineData("granted")]
    public async Task ResolveEntity_CatalogAccess_FiltersBeforeRanking(string restriction)
    {
        using var services = CreateServices(restriction);
        var tool = new ResolveEntityTool(Substitute.For<IGeoprocessingJobService>(), NullLogger<ResolveEntityTool>.Instance);
        using var arguments = JsonDocument.Parse("""{"text":"Parcels"}""");

        var result = await tool.InvokeAsync(CreateContext(services), arguments.RootElement, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("matchCount").GetInt32()
            .Should().Be(IsReadable(restriction) ? 2 : 0);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("resource-policy")]
    [InlineData("service-policy")]
    [InlineData("resource-tenant")]
    [InlineData("service-tenant")]
    [InlineData("publication-tenant")]
    [InlineData("missing-tenant")]
    [InlineData("unpublished")]
    [InlineData("inactive")]
    [InlineData("allowed")]
    [InlineData("same-tenant")]
    [InlineData("granted")]
    public async Task Ground_CatalogAccess_FiltersBeforeScoring(string restriction)
    {
        using var services = CreateServices(restriction);
        CreateContext(services);
        var engine = Substitute.For<IGroundingEngine>();
        engine.Classify(Arg.Any<GroundingRequest>()).Returns(new WorkflowFamilyClassification { Value = WorkflowFamily.Analyze, Confidence = 1 });
        var filter = Substitute.For<IGroundingAuthorizationFilter>();
        filter.FilterAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<GroundingCandidate>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<GroundingCandidate>>());
        var service = new GroundingService(engine, Substitute.For<IProcessCatalog>(), filter,
            Options.Create(new GroundingOptions()), NullLogger<GroundingService>.Instance,
            services.GetRequiredService<IServiceScopeFactory>());

        await service.GroundAsync(new GroundingRequest { Goal = "analyze Parcels" }, Principal);

        engine.Received().ScoreLayers(Arg.Any<GroundingRequest>(),
            Arg.Is<IReadOnlyList<LayerCandidate>>(layers => layers.Count == (IsReadable(restriction) ? 1 : 0)));
        engine.Received().ScoreServices(Arg.Any<GroundingRequest>(),
            Arg.Is<IReadOnlyList<ServiceCandidate>>(layers => layers.Count == (IsReadable(restriction) ? 1 : 0)));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("resource-policy")]
    [InlineData("service-policy")]
    [InlineData("resource-tenant")]
    [InlineData("service-tenant")]
    [InlineData("publication-tenant")]
    [InlineData("missing-tenant")]
    [InlineData("unpublished")]
    [InlineData("inactive")]
    [InlineData("allowed")]
    [InlineData("same-tenant")]
    [InlineData("granted")]
    public async Task SpecGround_CatalogAccess_OnlyResolvesReadableSources(string restriction)
    {
        using var services = CreateServices(restriction);
        CreateContext(services);
        var document = new SpecDocument(SourceSpan.Synthetic, SpecGrammarVersion.Current, SourceSpan.Synthetic,
            "analysis", null, [], [], [], null, [], ImmutableDictionary<string, string>.Empty);

        var result = await services.GetRequiredService<SpecGroundingService>().MutateAsync(document,
            "use Parcels as parcels", null, null, Principal, CancellationToken.None);

        if (IsReadable(restriction))
        {
            result.ErrorKind.Should().BeNull();
            result.Mutation.Should().NotBeNull();
        }
        else
        {
            result.Mutation.Should().BeNull();
            result.ErrorKind.Should().Be(SpecGroundingErrorKind.Unresolvable);
            result.Clarification.Should().BeNull();
        }
    }

    [UnitTest]
    public async Task SpecGround_NullPrincipal_DoesNotUseAmbientPrincipal()
    {
        using var services = CreateServices("allowed");
        CreateContext(services);
        var document = new SpecDocument(SourceSpan.Synthetic, SpecGrammarVersion.Current, SourceSpan.Synthetic,
            "analysis", null, [], [], [], null, [], ImmutableDictionary<string, string>.Empty);

        var result = await services.GetRequiredService<SpecGroundingService>().MutateAsync(document,
            "use Parcels as parcels", null, null, null, CancellationToken.None);

        result.Mutation.Should().BeNull();
        result.ErrorKind.Should().Be(SpecGroundingErrorKind.Unresolvable);
    }

    private static bool IsReadable(string restriction) => restriction is "allowed" or "same-tenant" or "granted";

    private static readonly ClaimsPrincipal Principal = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "reader"), new Claim(ClaimTypes.Role, "scoped-reader")], "Test"));

    private static ServiceProvider CreateServices(string restriction)
    {
        var policy = new AccessPolicy { AllowedRoles = ["data-reader"] };
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("res", "Parcels", fields: [new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }],
                accessPolicy: restriction is "resource-policy" or "granted" ? policy : null)
            .AddStorageBinding("binding", "res", "parcels", storageLayerId: 42)
            .AddService("svc", "Parcels", accessPolicy: restriction == "service-policy" ? policy : null)
            .AddPublication("pub", "svc", "res", layerIndex: 0, storageBindingId: "binding")
            .Build();
        if (restriction is "resource-tenant" or "missing-tenant" or "same-tenant")
        {
            graph = graph with { Resources = graph.Resources.Select(r => r with { Metadata = r.Metadata with { Tenant = restriction == "same-tenant" ? "tenant-a" : "tenant-b" } }).ToArray() };
        }
        if (restriction == "service-tenant")
        {
            graph = graph with { Services = graph.Services.Select(s => s with { Metadata = s.Metadata with { Tenant = "tenant-b" } }).ToArray() };
        }
        if (restriction == "publication-tenant")
        {
            graph = graph with { Publications = graph.Publications.Select(p => p with { Metadata = p.Metadata with { Tenant = "tenant-b" } }).ToArray() };
        }
        if (restriction == "unpublished")
        {
            graph = graph with { Publications = [] };
        }
        if (restriction == "inactive")
        {
            graph = graph with { Services = graph.Services.Select(service => service with
            {
                Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Draft }
            }).ToArray() };
        }
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSpecGrounding();
        services.AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph));
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        if (restriction.Contains("tenant", StringComparison.Ordinal) || restriction == "granted")
        {
            var resolver = Substitute.For<IPermissionResolver>();
            resolver.AuthorizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), "Parcels", "Parcels",
                    Arg.Any<AuthorizationOperation>(), true, Arg.Any<CancellationToken>())
                .Returns(PermissionDecision.Allow(new PermissionGrant { Service = "Parcels", Layer = "Parcels", Operation = "*" }));
            services.AddSingleton(resolver);
            services.AddOptions<RbacOptions>();
        }
        var renderer = Substitute.For<IRasterMapRenderer>();
        renderer.RenderDatasetMapAsync(Arg.Any<int[]>(), Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult { Data = [1], ContentType = "image/png", Width = 1, Height = 1 });
        services.AddSingleton(renderer);
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(restriction == "missing-tenant" ? null : "tenant-a");
        services.AddSingleton(tenant);
        return services.BuildServiceProvider();
    }

    private static HttpContext CreateContext(IServiceProvider services)
    {
        var context = new DefaultHttpContext { User = Principal, RequestServices = services };
        services.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        return context;
    }
}
