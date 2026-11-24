// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Rendering;
using Honua.Protocols.Ogc.Classic.Wcs20;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wcs20;

public sealed class Wcs20HardeningTests
{
    [UnitTest]
    public async Task DescribeCoverage_TooManyCoverageIds_ReturnsBadRequest()
    {
        var ids = string.Join(",", Enumerable.Range(1, 51).Select(i => $"coverage_{i}"));
        var (status, body, _, _) = await ExecuteAsync(
            $"?SERVICE=WCS&REQUEST=DescribeCoverage&VERSION=2.0.1&COVERAGEID={ids}",
            User("reader"));

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("InvalidParameterValue");
    }

    [UnitTest]
    public async Task DescribeCoverage_AliasCoverageIds_DeduplicatesSingleDescription()
    {
        var (status, body, _, infoCalls) = await ExecuteAsync(
            "?SERVICE=WCS&REQUEST=DescribeCoverage&VERSION=2.0.1&COVERAGEID=1,01,coverage_1,COVERAGE_1",
            User("reader"));

        status.Should().Be(StatusCodes.Status200OK, body);
        var doc = XDocument.Parse(body);
        var wcs = XNamespace.Get(Wcs20Utilities.WcsNamespace);
        doc.Descendants(wcs + "CoverageDescription").Should().ContainSingle();
        infoCalls.Should().Be(1);
    }

    [UnitTest]
    public async Task GetCoverage_WhenRenderCapacityExhausted_Returns503WithRetryAfter()
    {
        using var limiter = new RasterRenderCapacityLimiter(maxConcurrentRenders: 1, maxReservedBytes: 1024 * 1024, acquireTimeout: TimeSpan.Zero);
        await using var held = await limiter.TryAcquireAsync(4, 4, CancellationToken.None);
        held.Should().NotBeNull();

        var (status, body, headers, _) = await ExecuteAsync(
            "?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=coverage_1&FORMAT=image/png",
            User("reader"),
            limiter: limiter);

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain("ExceptionReport");
        headers.RetryAfter.ToString().Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public async Task GetCapabilities_AnonymousOnSecuredService_Returns401WithWwwAuthenticateChallenge()
    {
        var (status, _, headers, _) = await ExecuteAsync(
            "?SERVICE=WCS&REQUEST=GetCapabilities&VERSION=2.0.1",
            new ClaimsPrincipal(new ClaimsIdentity()));

        status.Should().Be(StatusCodes.Status401Unauthorized);
        headers.WWWAuthenticate.ToString().Should().NotBeNullOrWhiteSpace();
    }

    private static ClaimsPrincipal User(string role) => new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));

    private static async Task<(int Status, string Body, IHeaderDictionary Headers, int RasterInfoCalls)> ExecuteAsync(
        string queryString,
        ClaimsPrincipal principal,
        RasterRenderCapacityLimiter? limiter = null)
    {
        var infoCalls = 0;
        var store = Substitute.For<IRasterStore>();
        var extent = new RasterExtent { XMin = 0, YMin = 0, XMax = 4, YMax = 4, Srid = 4326 };
        store.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            infoCalls++;
            return Task.FromResult<RasterInfo?>(new RasterInfo
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
                GeoTransform = [0, 1, 0, 4, 0, -1],
            });
        });
        store.ExportImageAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RasterResult { Data = [0x89, 0x50, 0x4e, 0x47], ContentType = "image/png", Width = 4, Height = 4, Srid = 4326, Extent = extent }));

        var crs = Substitute.For<ICrsRegistry>();
#pragma warning disable CA2012
        crs.IsSridSupportedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
#pragma warning restore CA2012

        var policy = new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["reader"] };
        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-1", Name = "elevation" },
            Type = MetadataV2ObjectType.RasterDataset,
            StorageBindingIds = ["binding-1"],
            PrimaryStorageBindingId = "binding-1",
            AccessPolicy = policy,
        };
        var graph = new MetadataV2Graph
        {
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service-imagery", Name = "imagery" },
                    ServiceType = MetadataV2ServiceType.EsriImageService,
                    Protocols = ["Wcs", "ImageServer"],
                    EnabledProtocols = ["Wcs", "ImageServer"],
                    AccessPolicy = policy,
                }
            ],
            Resources = [resource],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-1", Name = "binding-1" },
                    ResourceId = resource.Metadata.Id,
                    StorageLayerId = 101,
                }
            ],
            Publications =
            [
                new MetadataV2Layer
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "publication-1", Name = resource.Metadata.Name },
                    ServiceId = "service-imagery",
                    ResourceId = resource.Metadata.Id,
                    StorageBindingId = "binding-1",
                    LayerIndex = 1,
                    Identifier = new MetadataV2LayerIdentifier { Value = "1", IsNumeric = true },
                }
            ],
        };

        var handler = new Wcs20Handler(
            new TestMetadataV2GraphProvider(graph),
            new Wcs20CoverageBackend(store, Substitute.For<IZarrStore>(), Substitute.For<IZarrRasterSliceReader>()),
            crs,
            Substitute.For<ILogger<Wcs20Handler>>());

        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("local");
        var services = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .AddSingleton(tenant);
        if (limiter is not null)
        {
            services.AddSingleton(limiter);
        }

        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider, User = principal };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("wcs.test");
        context.Request.Path = "/ogc/wcs/imagery";
        context.Request.QueryString = new QueryString(queryString);
        context.Response.Body = new MemoryStream();

        var result = await handler.HandleAsync(context, Wcs20RouteScope.ForService("imagery"));
        await result.ExecuteAsync(context);
        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        return (context.Response.StatusCode, body, context.Response.Headers, infoCalls);
    }
}
