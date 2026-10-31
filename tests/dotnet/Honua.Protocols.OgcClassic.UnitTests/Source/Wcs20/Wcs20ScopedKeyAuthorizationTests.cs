// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.Ogc.Classic.Wcs20;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wcs20;

/// <summary>Real handler authorization with in-memory stores; no database or desktop SDK.</summary>
public sealed class Wcs20ScopedKeyAuthorizationTests
{
    private static readonly byte[] CoverageBytes = [0x89, 0x50, 0x4e, 0x47];
    private static readonly string[] ResourceNames = ["elevation", "neighbor"];

    [UnitTheory]
    [InlineData("1.0.0", "read:imagery")]
    [InlineData("2.0.1", "read:imagery")]
    [InlineData("1.0.0", "read:imagery/*")]
    [InlineData("2.0.1", "read:imagery/*")]
    [InlineData("1.0.0", "read:*")]
    [InlineData("2.0.1", "read:*")]
    public async Task Issue6347_MatchingServiceReadGrant_ReachesMetadataAndCoverage(string version, string grant)
    {
        foreach (var operation in new[] { "GetCapabilities", "DescribeCoverage", "GetCoverage" })
        {
            var result = await ExecuteAsync(version, operation, Key(grant));
            result.Status.Should().Be(200, result.Text);
            if (operation == "GetCoverage")
            {
                result.Body.Should().Equal(CoverageBytes);
                result.Exports.Should().Equal(101);
            }
            else
            {
                result.Text.Should().Contain("coverage_1");
            }
        }
    }

    [UnitTheory]
    [InlineData("1.0.0")]
    [InlineData("2.0.1")]
    public async Task Issue6347_LayerGrant_FiltersCatalogAndCannotReadNeighbor(string version)
    {
        var key = Key("read:imagery/elevation");
        var capabilities = await ExecuteAsync(version, "GetCapabilities", key);
        capabilities.Status.Should().Be(200, capabilities.Text);
        capabilities.Text.Should().Contain("coverage_1").And.NotContain("coverage_2");
        var allowed = await ExecuteAsync(version, "GetCoverage", key);
        allowed.Status.Should().Be(200, allowed.Text);
        allowed.Body.Should().Equal(CoverageBytes);
        var denied = await ExecuteAsync(version, "GetCoverage", key, coverage: 2);
        AssertDenied(denied);
    }

    [UnitTheory]
    [InlineData("1.0.0", "read:imagery/elevation", 101, true)]
    [InlineData("2.0.1", "read:imagery/elevation", 101, true)]
    [InlineData("1.0.0", "read:imagery/elevation", 102, false)]
    [InlineData("2.0.1", "read:imagery/elevation", 102, false)]
    public async Task Issue6347_DirectLayerRoute_UsesResolvedGrantTarget(string version, string grant, int layer, bool allowed)
    {
        var result = await ExecuteAsync(version, "GetCoverage", Key(grant), directLayer: layer);
        if (allowed)
        {
            result.Status.Should().Be(200, result.Text);
            result.Body.Should().Equal(CoverageBytes);
        }
        else
        {
            AssertDenied(result);
        }
    }

    [UnitTheory]
    [InlineData("read:other")]
    [InlineData("write:imagery")]
    [InlineData("admin:read")]
    public async Task Issue6347_NonmatchingOrNonreadKey_RemainsForbidden(string grant)
    {
        foreach (var operation in new[] { "GetCapabilities", "DescribeCoverage", "GetCoverage" })
        {
            var result = await ExecuteAsync("2.0.1", operation, Key(grant));
            result.Status.Should().Be(403, result.Text);
            AssertDenied(result);
        }
    }

    [UnitTest]
    public async Task Issue6347_AnonymousAndWrongRoleRemainDenied_ReaderStillWorks()
    {
        var anonymous = await ExecuteAsync("2.0.1", "GetCapabilities", new ClaimsPrincipal(new ClaimsIdentity()));
        anonymous.Status.Should().Be(401, anonymous.Text);
        AssertDenied(anonymous);
        var outsider = await ExecuteAsync("2.0.1", "GetCoverage", User("outsider"));
        outsider.Status.Should().Be(403, outsider.Text);
        AssertDenied(outsider);
        var reader = await ExecuteAsync("2.0.1", "GetCoverage", User("reader"));
        reader.Status.Should().Be(200, reader.Text);
        reader.Body.Should().Equal(CoverageBytes);
    }

    [UnitTest]
    public async Task Issue6347_AnonymousPublicCatalogStillWorks_ButWrongKeyCannotDowngrade()
    {
        var graph = Graph() with
        {
            Services = Graph().Services.Select(service => service with
            {
                AccessPolicy = new AccessPolicy { AllowAnonymous = true }
            }).ToArray(),
            Resources = Graph().Resources.Select(resource => resource with
            {
                AccessPolicy = new AccessPolicy { AllowAnonymous = true }
            }).ToArray()
        };
        var anonymous = await ExecuteAsync("2.0.1", "GetCapabilities", new ClaimsPrincipal(new ClaimsIdentity()), graph: graph);
        anonymous.Status.Should().Be(200, anonymous.Text);
        var wrongKey = await ExecuteAsync("2.0.1", "GetCoverage", Key("read:other"), graph: graph);
        wrongKey.Status.Should().Be(403, wrongKey.Text);
        AssertDenied(wrongKey);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issue6347_ForeignServiceTenant_DeniesEvenWildcardKey(bool direct)
    {
        var original = Graph();
        var graph = original with
        {
            Services = original.Services.Select(service => service with
            {
                Metadata = service.Metadata with { Tenant = "foreign" }
            }).ToArray()
        };
        var result = await ExecuteAsync("2.0.1", "GetCoverage", Key("read:*"), graph: graph, directLayer: direct ? 101 : null);
        AssertDenied(result);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issue6347_ForeignPublication_DoesNotAuthorizeAnUnscopedResource(bool direct)
    {
        var original = Graph();
        var graph = original with
        {
            Publications = original.Publications.Select(publication => publication with
            {
                Metadata = publication.Metadata with { Tenant = "foreign" }
            }).ToArray()
        };
        var result = await ExecuteAsync("2.0.1", "GetCoverage", Key("read:*"), graph: graph, directLayer: direct ? 101 : null);
        AssertDenied(result);
    }

    [UnitTest]
    public async Task Issue6347_OwnerWithoutPolicyStillProvidesTheScopedTarget()
    {
        var original = Graph();
        var graph = original with { Services = original.Services.Select(service => service with { AccessPolicy = null }).ToArray() };
        var result = await ExecuteAsync("2.0.1", "GetCoverage", Key("read:imagery"), graph: graph, directLayer: 101);
        result.Status.Should().Be(200, result.Text);
        result.Body.Should().Equal(CoverageBytes);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issue6347_ServiceOnlyPolicy_StillRestrictsCoverage(bool direct)
    {
        var original = Graph();
        var graph = original with
        {
            Resources = original.Resources.Select(resource => resource with { AccessPolicy = null }).ToArray()
        };
        var denied = await ExecuteAsync("2.0.1", "GetCoverage", User("outsider"), graph: graph, directLayer: direct ? 101 : null);
        denied.Status.Should().Be(direct ? 404 : 403, denied.Text);
        AssertDenied(denied);
        var allowed = await ExecuteAsync("2.0.1", "GetCoverage", User("reader"), graph: graph, directLayer: direct ? 101 : null);
        allowed.Status.Should().Be(200, allowed.Text);
        allowed.Body.Should().Equal(CoverageBytes);
    }

    [UnitTest]
    public async Task Issue6347_DirectRoute_PrefersPolicyBearingOwner()
    {
        var original = Graph();
        var unrestricted = original.Services[0] with
        {
            Metadata = original.Services[0].Metadata with { Id = "service-unrestricted", Name = "unrestricted" },
            AccessPolicy = null
        };
        var graph = original with
        {
            Services = [unrestricted, .. original.Services],
            Resources = original.Resources.Select(resource => resource with
            {
                AccessPolicy = new AccessPolicy { AllowAnonymous = true }
            }).ToArray(),
            Publications = [original.Publications[0] with
            {
                Metadata = original.Publications[0].Metadata with { Id = "publication-unrestricted" },
                ServiceId = unrestricted.Metadata.Id
            }, .. original.Publications]
        };
        var result = await ExecuteAsync("2.0.1", "GetCoverage", User("outsider"), graph: graph, directLayer: 101);
        result.Status.Should().Be(404, result.Text);
        AssertDenied(result);
    }

    [UnitTest]
    public async Task Issue6347_DirectRoute_ScopedKeyMatchesTheGrantOwner()
    {
        var original = Graph();
        var second = original.Services[0] with
        {
            Metadata = original.Services[0].Metadata with { Id = "service-second", Name = "second" }
        };
        var graph = original with
        {
            Services = [.. original.Services, second],
            Publications =
            [
                .. original.Publications,
                original.Publications[0] with
                {
                    Metadata = original.Publications[0].Metadata with { Id = "publication-second" },
                    ServiceId = second.Metadata.Id
                }
            ]
        };

        var secondKey = await ExecuteAsync("2.0.1", "GetCoverage", Key("read:second/elevation"), graph: graph, directLayer: 101);
        secondKey.Status.Should().Be(200, secondKey.Text);
        secondKey.Body.Should().Equal(CoverageBytes);
        var firstKey = await ExecuteAsync("2.0.1", "GetCoverage", Key("read:imagery/elevation"), graph: graph, directLayer: 101);
        firstKey.Status.Should().Be(200, firstKey.Text);
        var denied = await ExecuteAsync("2.0.1", "GetCoverage", Key("read:other/elevation"), graph: graph, directLayer: 101);
        AssertDenied(denied);
    }

    [UnitTest]
    public async Task Issue6347_LayerIndexFallback_IgnoresFeaturePublication()
    {
        var original = Graph();
        var feature = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-features", Name = "features" },
            ServiceType = MetadataV2ServiceType.GeoServicesFeatureService,
            Protocols = ["FeatureServer"],
            EnabledProtocols = ["FeatureServer"],
            AccessPolicy = new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["reader"] }
        };
        var roads = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-roads", Name = "roads" },
            Type = MetadataV2ResourceType.FeatureDataset,
            AccessPolicy = new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["reader"] }
        };
        var featurePublication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "publication-roads", Name = "roads" },
            ServiceId = feature.Metadata.Id,
            ResourceId = roads.Metadata.Id,
            LayerIndex = 101
        };
        var graph = original with
        {
            Services = [feature, .. original.Services],
            Resources = [roads, .. original.Resources],
            StorageBindings = [],
            Publications =
            [
                featurePublication,
                original.Publications[0] with { LayerIndex = 101 },
                .. original.Publications.Skip(1)
            ]
        };

        var featureKey = await ExecuteAsync("2.0.1", "GetCoverage", Key("read:features/roads"), graph: graph, directLayer: 101);
        AssertDenied(featureKey);
        featureKey.Exports.Should().BeEmpty();
        var imageryKey = await ExecuteAsync("2.0.1", "GetCoverage", Key("read:imagery/elevation"), graph: graph, directLayer: 101);
        imageryKey.Status.Should().Be(200, imageryKey.Text);
        imageryKey.Body.Should().Equal(CoverageBytes);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issue6347_ForeignResourceTenant_DeniesWildcardKey(bool direct)
    {
        var original = Graph();
        var graph = original with
        {
            Resources = original.Resources.Select(resource => resource with
            {
                Metadata = resource.Metadata with { Tenant = "foreign" }
            }).ToArray()
        };
        var result = await ExecuteAsync("2.0.1", "GetCoverage", Key("read:*"), graph: graph, directLayer: direct ? 101 : null);
        AssertDenied(result);
    }

    [UnitTest]
    public async Task Issue6347_RetiredPublication_CannotBootstrapLayerGrant()
    {
        var original = Graph();
        var graph = original with
        {
            Publications = original.Publications.Select(publication => publication with
            {
                Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Retired }
            }).ToArray()
        };
        var result = await ExecuteAsync("2.0.1", "GetCapabilities", Key("read:imagery/elevation"), graph: graph);
        result.Status.Should().Be(403, result.Text);
        AssertDenied(result);
    }

    private static void AssertDenied(Response result)
    {
        result.Status.Should().BeOneOf(401, 403, 404);
        result.ContentType.Should().Contain("xml");
        result.Text.Should().Contain("Exception");
        result.Exports.Should().BeEmpty();
        result.Body.Should().NotEqual(CoverageBytes);
    }

    private static ClaimsPrincipal Key(string grant) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Role, LayerScopedWriteKey.ScopedKeyRole), new Claim("permission", grant)], "ApiKey"));

    private static ClaimsPrincipal User(string role) => new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));

    private static MetadataV2Graph Graph()
    {
        var policy = new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["reader"] };
        var resources = ResourceNames.Select((name, index) => new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = $"resource-{index + 1}", Name = name },
            Type = MetadataV2ResourceType.RasterDataset,
            StorageBindingIds = [$"binding-{index + 1}"],
            PrimaryStorageBindingId = $"binding-{index + 1}",
            AccessPolicy = policy
        }).ToArray();
        return new MetadataV2Graph
        {
            Services = [new MetadataV2Service
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "service-imagery", Name = "imagery" },
                ServiceType = MetadataV2ServiceType.GeoServicesImageService,
                Protocols = ["Wcs", "ImageServer"], EnabledProtocols = ["Wcs", "ImageServer"], AccessPolicy = policy
            }],
            Resources = resources,
            StorageBindings = resources.Select((resource, index) => new MetadataV2StorageBinding
            {
                Metadata = new MetadataV2ObjectMetadata { Id = $"binding-{index + 1}", Name = $"binding-{index + 1}" },
                ResourceId = resource.Metadata.Id,
                StorageLayerId = 101 + index
            }).ToArray(),
            Publications = resources.Select((resource, index) => new MetadataV2Publication
            {
                Metadata = new MetadataV2ObjectMetadata { Id = $"publication-{index + 1}", Name = resource.Metadata.Name },
                ServiceId = "service-imagery",
                ResourceId = resource.Metadata.Id,
                StorageBindingId = $"binding-{index + 1}",
                LayerIndex = index + 1,
                Identifier = new MetadataV2PublicationIdentifier { Value = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), IsNumeric = true }
            }).ToArray()
        };
    }

    private static async Task<Response> ExecuteAsync(string version, string operation, ClaimsPrincipal principal,
        int coverage = 1, MetadataV2Graph? graph = null, int? directLayer = null)
    {
        var exports = new List<int>();
        var store = Substitute.For<IRasterStore>();
        var extent = new RasterExtent { XMin = 0, YMin = 0, XMax = 4, YMax = 4, Srid = 4326 };
        store.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call => Task.FromResult<RasterInfo?>(new RasterInfo
        {
            Id = call.ArgAt<int>(0),
            LayerId = call.ArgAt<int>(0),
            Name = "fixture",
            Width = 4,
            Height = 4,
            BandCount = 1,
            PixelType = "8BUI",
            Srid = 4326,
            Extent = extent,
            GeoTransform = [0, 1, 0, 4, 0, -1]
        }));
        store.ExportImageAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            exports.Add(call.ArgAt<int>(0));
            return Task.FromResult(new RasterResult { Data = CoverageBytes, ContentType = "image/png", Width = 4, Height = 4, Srid = 4326, Extent = extent });
        });
        var crs = Substitute.For<ICrsRegistry>();
#pragma warning disable CA2012 // NSubstitute records this ValueTask call; the handler consumes fresh return values.
        crs.IsSridSupportedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
#pragma warning restore CA2012
        var logger = Substitute.For<ILogger<Wcs20Handler>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var handler = new Wcs20Handler(new TestMetadataV2GraphProvider(graph ?? Graph()),
            new Wcs20CoverageBackend(store, Substitute.For<IZarrStore>(), Substitute.For<IZarrRasterSliceReader>()),
            crs, logger);
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("local");
        using var services = new ServiceCollection().AddLogging().AddOptions()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>().AddSingleton(tenant).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, User = principal };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("wcs.test");
        context.Request.Path = "/ogc/wcs/imagery";
        var id = directLayer ?? coverage;
        var identifier = version == "1.0.0" ? "COVERAGE" : "COVERAGEID";
        context.Request.QueryString = new QueryString($"?SERVICE=WCS&REQUEST={operation}&VERSION={version}&{identifier}=coverage_{id}&FORMAT=image/png");
        context.Response.Body = new MemoryStream();
        var scope = directLayer.HasValue ? Wcs20RouteScope.ForLayer(directLayer.Value) : Wcs20RouteScope.ForService("imagery");
        var result = await handler.HandleAsync(context, scope);
        await result.ExecuteAsync(context);
        var body = ((MemoryStream)context.Response.Body).ToArray();
        var errors = string.Join("\n", logger.ReceivedCalls().SelectMany(call => call.GetArguments()).OfType<Exception>());
        return new Response(context.Response.StatusCode, context.Response.ContentType ?? "", body, exports, errors);
    }

    private sealed record Response(int Status, string ContentType, byte[] Body, IReadOnlyList<int> Exports, string Errors)
    {
        public string Text => Encoding.UTF8.GetString(Body) + Errors;
    }
}
