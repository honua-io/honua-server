// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.MultiTenancy;
using Honua.Protocols.Ogc.Api.Styles;
using Honua.Protocols.SensorThings;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Honua.Server.Tests.Features.Security;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class MultiTenantAdminIsolationRegressionTests
{
    [UnitTest]
    public async Task ValidateAdminStorageLayerAsync_TenantBoundAdminTargetingOtherTenantResource_Returns404()
    {
        var graphProvider = BuildTwoTenantGraph();
        var tenantBResource = (await graphProvider.GetCurrentAsync()).Index.ResourcesByStorageLayerId[202];
        var validator = Substitute.For<IResourceValidator>();
        validator.ValidateStorageLayerV2Async(202, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(tenantBResource));

        var context = BuildHttpContext(
            multiTenancyEnabled: true,
            tenantId: "tenant-a",
            tenantSource: TenantContextSource.Claim,
            roles: ["admin"],
            claimTenantId: "tenant-a",
            graphProvider: graphProvider);

        var (resource, problem) = await ServiceResourceValidationHelpers.ValidateAdminStorageLayerAsync(
            (202, context), validator, CancellationToken.None);

        resource.Should().BeNull();
        problem.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    public async Task ValidateAdminStorageLayerAsync_TenantBoundAdminTargetingOtherTenantServiceOwnedLayer_Returns404()
    {
        var graphProvider = BuildTwoTenantGraph();
        var serviceOwnedTenantBResource = (await graphProvider.GetCurrentAsync()).Index.ResourcesByStorageLayerId[203];
        var validator = Substitute.For<IResourceValidator>();
        validator.ValidateStorageLayerV2Async(203, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(serviceOwnedTenantBResource));

        var context = BuildHttpContext(
            multiTenancyEnabled: true,
            tenantId: "tenant-a",
            tenantSource: TenantContextSource.Claim,
            roles: ["admin"],
            claimTenantId: "tenant-a",
            graphProvider: graphProvider);

        var (resource, problem) = await ServiceResourceValidationHelpers.ValidateAdminStorageLayerAsync(
            (203, context), validator, CancellationToken.None);

        resource.Should().BeNull();
        problem.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    public async Task ValidateAdminStorageLayerAsync_TenantBoundAdminTargetingOwnTenantLayer_Succeeds()
    {
        var graphProvider = BuildTwoTenantGraph();
        var tenantAResource = (await graphProvider.GetCurrentAsync()).Index.ResourcesByStorageLayerId[101];
        var validator = Substitute.For<IResourceValidator>();
        validator.ValidateStorageLayerV2Async(101, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(tenantAResource));

        var context = BuildHttpContext(
            multiTenancyEnabled: true,
            tenantId: "tenant-a",
            tenantSource: TenantContextSource.Claim,
            roles: ["admin"],
            claimTenantId: "tenant-a",
            graphProvider: graphProvider);

        var (resource, problem) = await ServiceResourceValidationHelpers.ValidateAdminStorageLayerAsync(
            (101, context), validator, CancellationToken.None);

        problem.Should().BeNull();
        resource.Should().NotBeNull();
        resource!.Metadata.Id.Should().Be("res-a");
    }

    [UnitTest]
    public async Task ValidateAdminStorageLayerAsync_TenantBoundAdminTargetingUnscopedPlatformLayerInMultiTenantMode_Returns404()
    {
        var graphProvider = BuildTwoTenantGraph();
        var unscopedResource = (await graphProvider.GetCurrentAsync()).Index.ResourcesByStorageLayerId[303];
        var validator = Substitute.For<IResourceValidator>();
        validator.ValidateStorageLayerV2Async(303, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(unscopedResource));

        var context = BuildHttpContext(
            multiTenancyEnabled: true,
            tenantId: "tenant-a",
            tenantSource: TenantContextSource.Claim,
            roles: ["admin"],
            claimTenantId: "tenant-a",
            graphProvider: graphProvider);

        var (resource, problem) = await ServiceResourceValidationHelpers.ValidateAdminStorageLayerAsync(
            (303, context), validator, CancellationToken.None);

        resource.Should().BeNull();
        problem.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    public async Task ValidateAdminStorageLayerAsync_SingleTenantAdminTargetingAnyLayer_Succeeds()
    {
        var graphProvider = BuildTwoTenantGraph();
        var tenantBResource = (await graphProvider.GetCurrentAsync()).Index.ResourcesByStorageLayerId[202];
        var validator = Substitute.For<IResourceValidator>();
        validator.ValidateStorageLayerV2Async(202, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(tenantBResource));

        var context = BuildHttpContext(
            multiTenancyEnabled: false,
            tenantId: "public",
            tenantSource: TenantContextSource.Default,
            roles: ["admin"],
            claimTenantId: null,
            graphProvider: graphProvider);

        var (resource, problem) = await ServiceResourceValidationHelpers.ValidateAdminStorageLayerAsync(
            (202, context), validator, CancellationToken.None);

        problem.Should().BeNull();
        resource.Should().NotBeNull();
    }

    [UnitTest]
    public void PlatformDeployAuthority_DeniesTenantBoundAdminInMultiTenantMode_AndAllowsSingleTenantOrPlatformAdmin()
    {
        var tenantBoundContext = BuildHttpContext(
            multiTenancyEnabled: true,
            tenantId: "tenant-a",
            tenantSource: TenantContextSource.Claim,
            roles: ["admin"],
            claimTenantId: "tenant-a");
        var platformAdminContext = BuildHttpContext(
            multiTenancyEnabled: true,
            tenantId: "public",
            tenantSource: TenantContextSource.Default,
            roles: ["admin"],
            claimTenantId: null);
        var singleTenantContext = BuildHttpContext(
            multiTenancyEnabled: false,
            tenantId: "public",
            tenantSource: TenantContextSource.Default,
            roles: ["admin"],
            claimTenantId: "tenant-a");

        PlatformDeployAuthority.Deny(tenantBoundContext)
            .Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        PlatformDeployAuthority.Deny(platformAdminContext).Should().BeNull();
        PlatformDeployAuthority.Deny(singleTenantContext).Should().BeNull();
    }

    [UnitTest]
    public void SensorThingsTenantScope_MultiTenantWithoutActiveSchema_FailsClosedForReadersAndAdmins()
    {
        var readerContext = BuildHttpContext(
            multiTenancyEnabled: true,
            tenantId: "tenant-a",
            tenantSource: TenantContextSource.Claim,
            roles: ["reader"],
            claimTenantId: "tenant-a",
            currentSchema: null);
        var adminContext = BuildHttpContext(
            multiTenancyEnabled: true,
            tenantId: "tenant-a",
            tenantSource: TenantContextSource.Claim,
            roles: ["admin"],
            claimTenantId: "tenant-a",
            currentSchema: null);

        SensorThingsTenantScope.HasSchemaIsolation(readerContext).Should().BeFalse();
        SensorThingsTenantScope.IsResolved(readerContext).Should().BeFalse();
        SensorThingsTenantScope.HasSchemaIsolation(adminContext).Should().BeFalse();
        SensorThingsTenantScope.IsResolved(adminContext).Should().BeFalse();
    }

    [UnitTest]
    public void SensorThingsTenantScope_MultiTenantWithActiveSchemaOrSingleTenant_Allows()
    {
        var routedContext = BuildHttpContext(
            multiTenancyEnabled: true,
            tenantId: "tenant-a",
            tenantSource: TenantContextSource.Claim,
            roles: ["reader"],
            claimTenantId: "tenant-a",
            currentSchema: "tenant_a");
        var singleTenantContext = BuildHttpContext(
            multiTenancyEnabled: false,
            tenantId: "public",
            tenantSource: TenantContextSource.Default,
            roles: ["reader"],
            claimTenantId: null,
            currentSchema: null);

        SensorThingsTenantScope.HasSchemaIsolation(routedContext).Should().BeTrue();
        SensorThingsTenantScope.IsResolved(routedContext).Should().BeTrue();
        SensorThingsTenantScope.HasSchemaIsolation(singleTenantContext).Should().BeTrue();
        SensorThingsTenantScope.IsResolved(singleTenantContext).Should().BeTrue();
    }

    [UnitTest]
    public async Task OgcStyles_AuthorizeStyleWriteAsync_EnforcesTenantVisibilityAndPlatformAuthority()
    {
        var graphProvider = BuildTwoTenantGraph();
        var tenantAContext = BuildHttpContext(
            multiTenancyEnabled: true,
            tenantId: "tenant-a",
            tenantSource: TenantContextSource.Claim,
            roles: ["admin"],
            claimTenantId: "tenant-a",
            graphProvider: graphProvider);
        var singleTenantContext = BuildHttpContext(
            multiTenancyEnabled: false,
            tenantId: "public",
            tenantSource: TenantContextSource.Default,
            roles: ["admin"],
            claimTenantId: null,
            graphProvider: graphProvider);

        var otherTenantResult = await OgcStylesEndpoints.AuthorizeStyleWriteAsync(tenantAContext, "layer-b", CancellationToken.None);
        var ownTenantResult = await OgcStylesEndpoints.AuthorizeStyleWriteAsync(tenantAContext, "layer-a", CancellationToken.None);
        var standaloneInMultiTenant = await OgcStylesEndpoints.AuthorizeStyleWriteAsync(tenantAContext, "standalone-style", CancellationToken.None);
        var standaloneInSingleTenant = await OgcStylesEndpoints.AuthorizeStyleWriteAsync(singleTenantContext, "standalone-style", CancellationToken.None);

        otherTenantResult.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        ownTenantResult.Should().BeNull();
        standaloneInMultiTenant.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        standaloneInSingleTenant.Should().BeNull();
    }

    [UnitTest]
    public void OidcAuthenticationExtensions_ResolveAllowedJwtAlgorithms_PinsSafeAlgorithmSets()
    {
        var asymmetric = OidcAuthenticationExtensions.ResolveAllowedJwtAlgorithms(staticSigningKey: null);
        var symmetric = OidcAuthenticationExtensions.ResolveAllowedJwtAlgorithms(staticSigningKey: "secret-key-material");

        asymmetric.Should().Contain([
            SecurityAlgorithms.RsaSha256,
            SecurityAlgorithms.RsaSha384,
            SecurityAlgorithms.RsaSha512,
            SecurityAlgorithms.RsaSsaPssSha256,
            SecurityAlgorithms.EcdsaSha256,
        ]);
        asymmetric.Should().NotContain(SecurityAlgorithms.HmacSha256);
        asymmetric.Should().NotContain("none");

        symmetric.Should().BeEquivalentTo([
            SecurityAlgorithms.HmacSha256,
            SecurityAlgorithms.HmacSha384,
            SecurityAlgorithms.HmacSha512,
        ]);
        symmetric.Should().NotContain("none");
    }

    private static TestMetadataV2GraphProvider BuildTwoTenantGraph()
    {
        var baseGraph = new TestMetadataV2GraphBuilder()
            .AddService("svc-a", "svc-a", protocols: [ServiceProtocols.FeatureServer])
            .AddService("svc-b", "svc-b", protocols: [ServiceProtocols.FeatureServer])
            .AddService("svc-global", "svc-global", protocols: [ServiceProtocols.FeatureServer])
            .AddResource("res-a", "layer-a")
            .AddResource("res-b", "layer-b")
            .AddResource("res-b-via-svc", "layer-b-via-svc")
            .AddResource("res-global", "layer-global")
            .AddStorageBinding("bind-a", "res-a", "features:101", storageLayerId: 101)
            .AddStorageBinding("bind-b", "res-b", "features:202", storageLayerId: 202)
            .AddStorageBinding("bind-b-svc", "res-b-via-svc", "features:203", storageLayerId: 203)
            .AddStorageBinding("bind-global", "res-global", "features:303", storageLayerId: 303)
            .AddPublication("pub-a", "svc-a", "res-a", layerIndex: 0, storageBindingId: "bind-a")
            .AddPublication("pub-b", "svc-b", "res-b", layerIndex: 0, storageBindingId: "bind-b")
            .AddPublication("pub-b-svc", "svc-b", "res-b-via-svc", layerIndex: 1, storageBindingId: "bind-b-svc")
            .AddPublication("pub-global", "svc-global", "res-global", layerIndex: 0, storageBindingId: "bind-global")
            .Build();

        var graph = baseGraph with
        {
            Services = baseGraph.Services.Select(service => service.Metadata.Id switch
            {
                "svc-a" => service with { Metadata = service.Metadata with { Tenant = "tenant-a" } },
                "svc-b" => service with { Metadata = service.Metadata with { Tenant = "tenant-b" } },
                _ => service,
            }).ToArray(),
            Resources = baseGraph.Resources.Select(resource => resource.Metadata.Id switch
            {
                "res-a" => resource with { Metadata = resource.Metadata with { Tenant = "tenant-a" } },
                "res-b" => resource with { Metadata = resource.Metadata with { Tenant = "tenant-b" } },
                _ => resource,
            }).ToArray(),
            Publications = baseGraph.Publications.Select(publication => publication.Metadata.Id switch
            {
                "pub-a" => publication with { Metadata = publication.Metadata with { Tenant = "tenant-a" } },
                "pub-b" => publication with { Metadata = publication.Metadata with { Tenant = "tenant-b" } },
                _ => publication,
            }).ToArray(),
        };

        return new TestMetadataV2GraphProvider(graph);
    }

    private static DefaultHttpContext BuildHttpContext(
        bool multiTenancyEnabled,
        string? tenantId,
        TenantContextSource tenantSource,
        string[] roles,
        string? claimTenantId,
        IMetadataV2GraphProvider? graphProvider = null,
        string? currentSchema = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<TenantContextOptions>>(
            Options.Create(new TenantContextOptions { Enabled = multiTenancyEnabled }));
        services.AddSingleton<ITenantContext>(new StubTenantContext(tenantId, tenantSource));
        services.AddSingleton<ISchemaContext>(new StubSchemaContext(currentSchema));
        if (graphProvider is not null)
        {
            services.AddSingleton(graphProvider);
        }

        var claims = new List<Claim>();
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (!string.IsNullOrWhiteSpace(claimTenantId))
        {
            claims.Add(new Claim("tenant_id", claimTenantId));
        }

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
        };
    }

    private sealed class StubTenantContext(string? tenantId, TenantContextSource source) : ITenantContext
    {
        public string? TenantId { get; } = tenantId;

        public TenantContextSource Source { get; } = source;

        public bool RequireTenantId(out string tenantId, out string? reason)
        {
            tenantId = TenantId ?? string.Empty;
            reason = string.IsNullOrEmpty(tenantId) ? "missing" : null;
            return !string.IsNullOrEmpty(tenantId);
        }
    }

    private sealed class StubSchemaContext(string? currentSchema) : ISchemaContext
    {
        public string? CurrentSchema { get; } = currentSchema;
    }
}
