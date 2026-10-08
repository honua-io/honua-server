// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Protocols.GeoServices.Catalog;
using Honua.Server.Features.Console.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Console;

public sealed class ProjectedCatalogDiscoveryRegistryStoreTests
{
    [UnitTest]
    public async Task TwoConfiguredNamespaces_DoNotShareEntriesOrItemIdentifiers()
    {
        var alpha = Graph();
        var beta = Graph("beta", "-beta");
        using var scope = new ProjectionScope(alpha with
        {
            Services = [.. alpha.Services, .. beta.Services],
            Resources = [.. alpha.Resources, .. beta.Resources],
            Publications = [.. alpha.Publications, .. beta.Publications],
        }, mappings:
        [
            Mapping(),
            new CatalogDiscoveryWorkspaceOptions { Id = "second", TenantId = "public", Namespace = "beta" },
        ]);
        var first = await scope.Store.GetEndpointAsync("default", "esri");
        var second = await scope.Store.GetEndpointAsync("second", "esri");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.All(first.Items, item => Assert.Equal("service with space", item.Title));
        Assert.All(second.Items, item => Assert.Equal("service with space", item.Title));
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));
        Assert.Null(await scope.Store.GetItemAsync("second", "esri", first.Items[0].Id));
        Assert.Null(await scope.Store.GetItemAsync("default", "esri", second.Items[0].Id));
    }

    [UnitTest]
    public void Options_RequireExplicitUniqueBoundedMappings()
    {
        var validator = new CatalogDiscoveryOptionsValidator();
        Assert.True(validator.Validate(null, new CatalogDiscoveryOptions()).Succeeded);
        foreach (var invalid in new[]
        {
            new CatalogDiscoveryWorkspaceOptions { Id = "default", Namespace = "alpha" },
            new CatalogDiscoveryWorkspaceOptions { Id = "default", TenantId = "public" },
            new CatalogDiscoveryWorkspaceOptions { Id = "default", TenantId = "public", Namespace = "*" },
            new CatalogDiscoveryWorkspaceOptions { Id = new string('a', 129), TenantId = "public", Namespace = "alpha" },
        })
        {
            Assert.True(validator.Validate(null, new CatalogDiscoveryOptions { Workspaces = [invalid] }).Failed);
        }

        Assert.True(validator.Validate(null, new CatalogDiscoveryOptions
        {
            Workspaces = [Mapping("default"), Mapping("DEFAULT")],
        }).Failed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("other")]
    [InlineData("PUBLIC")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task UnresolvedOrDifferentTenant_DoesNotExposeMappedWorkspace(string? tenant)
    {
        using var scope = new ProjectionScope(Graph(), tenant);
        Assert.Null(await scope.Store.GetRegistryAsync("default"));
        Assert.Null(await scope.Store.GetEndpointAsync("default", "esri"));
        Assert.Null(await scope.Store.GetItemAsync("default", "esri", "anything"));
        await scope.Provider.DidNotReceive().GetCurrentAsync(Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task WorkspaceNamesAreCaseInsensitive_ButUnknownWorkspaceDoesNotAliasDefault()
    {
        using var scope = new ProjectionScope(Graph());
        Assert.Null(await scope.Store.GetRegistryAsync("unmapped"));
        var registry = await scope.Store.GetRegistryAsync("DEFAULT");
        Assert.NotNull(registry);
        Assert.Equal("default", registry.WorkspaceId);
        Assert.Equal(2, Assert.Single(registry.Endpoints).Entries);
    }

    [UnitTest]
    public async Task NamespaceAndTenantApplyToEveryNodeInPublicationChain()
    {
        foreach (var node in new[] { "service", "publication", "resource" })
        {
            foreach (var change in new[] { "namespace", "tenant", "namespace-case", "unassigned" })
            {
                var graph = Graph();
                MetadataV2ObjectMetadata Change(MetadataV2ObjectMetadata metadata) => change switch
                {
                    "tenant" => metadata with { Tenant = "other" },
                    "namespace-case" => metadata with { Namespace = "ALPHA" },
                    "unassigned" => metadata with { Namespace = null },
                    _ => metadata with { Namespace = "beta" },
                };
                graph = graph with
                {
                    Services = node == "service" ? graph.Services.Select(value => value with { Metadata = Change(value.Metadata) }).ToArray() : graph.Services,
                    Resources = node == "resource" ? graph.Resources.Select(value => value with { Metadata = Change(value.Metadata) }).ToArray() : graph.Resources,
                    Publications = node == "publication" ? graph.Publications.Select(value => value with { Metadata = Change(value.Metadata) }).ToArray() : graph.Publications,
                };
                using var scope = new ProjectionScope(graph);
                var registry = await scope.Store.GetRegistryAsync("default");
                Assert.NotNull(registry);
                Assert.Empty(registry.Endpoints);
            }
        }
    }

    [UnitTest]
    public async Task AuthorizationAndLifecycleStillFilterMatchingMappings()
    {
        using (var denied = new ProjectionScope(Graph(), allowed: false))
        {
            Assert.Empty((await denied.Store.GetRegistryAsync("default"))!.Endpoints);
            denied.Evaluator.Received().Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<AccessPolicy>(), Arg.Any<AccessPolicy>(), Arg.Any<object>());
        }

        var graph = Graph();
        using var retired = new ProjectionScope(graph with
        {
            Publications = graph.Publications.Select(value => value with
            {
                Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Retired },
            }).ToArray(),
        });
        Assert.Empty((await retired.Store.GetRegistryAsync("default"))!.Endpoints);
    }

    [UnitTest]
    public async Task CanonicalProjectionParity_DetailAndItemsReflectOnlyRealEntries()
    {
        using var scope = new ProjectionScope(Graph());
        var snapshot = await scope.Provider.GetCurrentAsync();
        var original = await GeoservicesCatalogEndpoints.BuildServiceDirectoryProjectionAsync(
            scope.Context, scope.Provider,
            scope.Services.GetRequiredService<IRasterStore>(),
            scope.Services.GetRequiredService<ILicenseStatusProvider>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var projected = await GeoServicesCatalogProjection.ReadFeatureMapAsync(scope.Context, snapshot);
        Assert.Equal(original.Entries.Select(entry => (entry.Name, entry.Type, entry.Url)),
            projected.Select(entry => (entry.Name, entry.Type, entry.Url)));

        var detail = await scope.Store.GetEndpointAsync("default", "ESRI");
        Assert.NotNull(detail);
        Assert.Equal(projected.Count, detail.Items.Count);
        foreach (var row in detail.Items)
        {
            Assert.Contains(projected, entry => entry.Url == row.Resource && entry.Name == row.Title);
            var item = await scope.Store.GetItemAsync("default", "esri", row.Id);
            Assert.NotNull(item);
            Assert.Equal(row.Title, item.Title);
            Assert.True(item.Live);
        }
        Assert.Null(await scope.Store.GetEndpointAsync("default", "stac"));
        Assert.Null(await scope.Store.GetItemAsync("default", "esri", "unknown"));
    }

    internal static CatalogDiscoveryWorkspaceOptions Mapping(string id = "default") =>
        new() { Id = id, TenantId = "public", Namespace = "alpha" };

    internal static MetadataV2Graph Graph(string metadataNamespace = "alpha", string suffix = "")
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("resource" + suffix, "resource" + suffix, MetadataV2ResourceType.FeatureDataset,
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddService("service" + suffix, "service with space",
                protocols: [ServiceProtocols.FeatureServer, ServiceProtocols.MapServer],
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddPublication("publication" + suffix, "service" + suffix, "resource" + suffix, layerIndex: suffix.Length == 0 ? 42 : 43,
                publicationType: MetadataV2PublicationType.EsriFeatureLayer)
            .Build();
        MetadataV2ObjectMetadata Scoped(MetadataV2ObjectMetadata metadata) => metadata with { Tenant = "public", Namespace = metadataNamespace };
        return graph with
        {
            Services = graph.Services.Select(value => value with { Metadata = Scoped(value.Metadata) }).ToArray(),
            Resources = graph.Resources.Select(value => value with { Metadata = Scoped(value.Metadata) }).ToArray(),
            Publications = graph.Publications.Select(value => value with { Metadata = Scoped(value.Metadata) }).ToArray(),
        };
    }

    private sealed class ProjectionScope : IDisposable
    {
        public ProjectionScope(MetadataV2Graph graph, string? tenant = "public", bool allowed = true,
            List<CatalogDiscoveryWorkspaceOptions>? mappings = null)
        {
            Provider = Substitute.For<IMetadataV2GraphProvider>();
            Provider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new MetadataV2GraphSnapshot(graph, "etag", DateTimeOffset.UtcNow));
            Evaluator = Substitute.For<IAccessPolicyEvaluator>();
            Evaluator.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<AccessPolicy>(), Arg.Any<AccessPolicy>(), Arg.Any<object>())
                .Returns(allowed ? AccessDecision.Allowed() : AccessDecision.Forbidden());
            var tenantContext = new FixedTenantContext(tenant);
            var license = Substitute.For<ILicenseStatusProvider>();
            license.GetCurrentStatus().Returns(new LicenseStatus(HonuaEdition.Community, true, null, null));
            Services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
                .AddSingleton(Provider)
                .AddSingleton(Evaluator)
                .AddSingleton<ITenantContext>(tenantContext)
                .AddSingleton(Substitute.For<IRasterStore>())
                .AddSingleton(license)
                .BuildServiceProvider();
            Context = new DefaultHttpContext { RequestServices = Services };
            Context.Request.Scheme = "https";
            Context.Request.Host = new HostString("catalog.example");
            Store = new ProjectedCatalogDiscoveryRegistryStore(
                Options.Create(new CatalogDiscoveryOptions { Workspaces = mappings ?? [Mapping()] }), Provider, tenantContext,
                new HttpContextAccessor { HttpContext = Context });
        }

        public ServiceProvider Services { get; }
        public IMetadataV2GraphProvider Provider { get; }
        public IAccessPolicyEvaluator Evaluator { get; }
        public DefaultHttpContext Context { get; }
        public ProjectedCatalogDiscoveryRegistryStore Store { get; }
        public void Dispose() => Services.Dispose();
    }

    private sealed class FixedTenantContext(string? tenant) : ITenantContext
    {
        public string? TenantId => tenant;
        public TenantContextSource Source => tenant is null ? TenantContextSource.Anonymous : TenantContextSource.Claim;
        public bool RequireTenantId(out string tenantId, out string? reason)
        {
            tenantId = tenant ?? string.Empty;
            reason = tenant is null ? "No tenant" : null;
            return tenant is not null;
        }
    }
}
