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
        var validator = CreateLayerValidator(202, tenantBResource);
        var context = BuildMultiTenantContext(TenantAAdminScenario(graphProvider));

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
        var validator = CreateLayerValidator(203, serviceOwnedTenantBResource);
        var context = BuildMultiTenantContext(TenantAAdminScenario(graphProvider));

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
        var validator = CreateLayerValidator(101, tenantAResource);
        var context = BuildMultiTenantContext(TenantAAdminScenario(graphProvider));

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
        var validator = CreateLayerValidator(303, unscopedResource);
        var context = BuildMultiTenantContext(TenantAAdminScenario(graphProvider));

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
        var validator = CreateLayerValidator(202, tenantBResource);
        var context = BuildSingleTenantContext(SingleTenantAdminScenario(graphProvider));

        var (resource, problem) = await ServiceResourceValidationHelpers.ValidateAdminStorageLayerAsync(
            (202, context), validator, CancellationToken.None);

        problem.Should().BeNull();
        resource.Should().NotBeNull();
    }

    [UnitTest]
    public void PlatformDeployAuthority_DeniesTenantBoundAdminInMultiTenantMode_AndAllowsSingleTenantOrPlatformAdmin()
    {
        var tenantBoundContext = BuildMultiTenantContext(TenantAAdminScenario());
        var platformAdminContext = BuildMultiTenantContext(new SecurityContextScenario
        {
            TenantId = "public",
            TenantSource = TenantContextSource.Default,
            Roles = ["admin"],
        });
        var singleTenantContext = BuildSingleTenantContext(new SecurityContextScenario
        {
            TenantId = "public",
            TenantSource = TenantContextSource.Default,
            Roles = ["admin"],
            ClaimTenantId = "tenant-a",
        });

        PlatformDeployAuthority.Deny(tenantBoundContext)
            .Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        PlatformDeployAuthority.Deny(platformAdminContext).Should().BeNull();
        PlatformDeployAuthority.Deny(singleTenantContext).Should().BeNull();
    }

    [UnitTest]
    public void SensorThingsTenantScope_MultiTenantWithoutActiveSchema_FailsClosedForReadersAndAdmins()
    {
        var readerContext = BuildMultiTenantContext(new SecurityContextScenario
        {
            TenantId = "tenant-a",
            TenantSource = TenantContextSource.Claim,
            Roles = ["reader"],
            ClaimTenantId = "tenant-a",
        });
        var adminContext = BuildMultiTenantContext(TenantAAdminScenario());

        SensorThingsTenantScope.HasSchemaIsolation(readerContext).Should().BeFalse();
        SensorThingsTenantScope.IsResolved(readerContext).Should().BeFalse();
        SensorThingsTenantScope.HasSchemaIsolation(adminContext).Should().BeFalse();
        SensorThingsTenantScope.IsResolved(adminContext).Should().BeFalse();
    }

    [UnitTest]
    public void SensorThingsTenantScope_MultiTenantWithActiveSchemaOrSingleTenantOrNullServices_Allows()
    {
        var routedContext = BuildMultiTenantContext(new SecurityContextScenario
        {
            TenantId = "tenant-a",
            TenantSource = TenantContextSource.Claim,
            Roles = ["reader"],
            ClaimTenantId = "tenant-a",
            CurrentSchema = "tenant_a",
        });
        var singleTenantContext = BuildSingleTenantContext(new SecurityContextScenario
        {
            TenantId = "public",
            TenantSource = TenantContextSource.Default,
            Roles = ["reader"],
        });

        SensorThingsTenantScope.HasSchemaIsolation(routedContext).Should().BeTrue();
        SensorThingsTenantScope.IsResolved(routedContext).Should().BeTrue();
        SensorThingsTenantScope.HasSchemaIsolation(singleTenantContext).Should().BeTrue();
        SensorThingsTenantScope.IsResolved(singleTenantContext).Should().BeTrue();
        SensorThingsTenantScope.HasSchemaIsolation(new DefaultHttpContext()).Should().BeTrue();
    }

    [UnitTest]
    public async Task OgcStyles_AuthorizeStyleWriteAsync_EnforcesTenantVisibilityForNamedMirroredAndStandaloneStyles()
    {
        var graphProvider = BuildTwoTenantGraph();
        var tenantAContext = BuildMultiTenantContext(TenantAAdminScenario(graphProvider));
        var singleTenantContext = BuildSingleTenantContext(SingleTenantAdminScenario(graphProvider));

        var otherTenantNamed = await OgcStylesEndpoints.AuthorizeStyleWriteAsync(tenantAContext, "layer-b", CancellationToken.None);
        var otherTenantMirrored = await OgcStylesEndpoints.AuthorizeStyleWriteAsync(tenantAContext, "style-layer-202", CancellationToken.None);
        var ownTenantNamed = await OgcStylesEndpoints.AuthorizeStyleWriteAsync(tenantAContext, "layer-a", CancellationToken.None);
        var ownTenantMirrored = await OgcStylesEndpoints.AuthorizeStyleWriteAsync(tenantAContext, "style-layer-101", CancellationToken.None);
        var standaloneInMultiTenant = await OgcStylesEndpoints.AuthorizeStyleWriteAsync(tenantAContext, "standalone-style", CancellationToken.None);
        var standaloneInSingleTenant = await OgcStylesEndpoints.AuthorizeStyleWriteAsync(singleTenantContext, "standalone-style", CancellationToken.None);

        otherTenantNamed.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        otherTenantMirrored.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        ownTenantNamed.Should().BeNull();
        ownTenantMirrored.Should().BeNull();
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

    private sealed record SecurityContextScenario
    {
        public string? TenantId { get; init; }

        public TenantContextSource TenantSource { get; init; } = TenantContextSource.Default;

        public string[] Roles { get; init; } = [];

        public string? ClaimTenantId { get; init; }

        public IMetadataV2GraphProvider? GraphProvider { get; init; }

        public string? CurrentSchema { get; init; }
    }

    private static SecurityContextScenario TenantAAdminScenario(IMetadataV2GraphProvider? graphProvider = null) =>
        new()
        {
            TenantId = "tenant-a",
            TenantSource = TenantContextSource.Claim,
            Roles = ["admin"],
            ClaimTenantId = "tenant-a",
            GraphProvider = graphProvider,
        };

    private static SecurityContextScenario SingleTenantAdminScenario(IMetadataV2GraphProvider? graphProvider = null) =>
        new()
        {
            TenantId = "public",
            TenantSource = TenantContextSource.Default,
            Roles = ["admin"],
            GraphProvider = graphProvider,
        };

    private static IResourceValidator CreateLayerValidator(int storageLayerId, MetadataV2Object resource)
    {
        var validator = Substitute.For<IResourceValidator>();
        validator.ValidateStorageLayerV2Async(storageLayerId, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(resource));
        return validator;
    }

    private static TestMetadataV2GraphProvider BuildTwoTenantGraph()
        => new(ApplyTenantOwnership(BuildBaseTwoTenantGraph()));

    private static MetadataV2Graph BuildBaseTwoTenantGraph() =>
        new TestMetadataV2GraphBuilder()
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

    private static MetadataV2Graph ApplyTenantOwnership(MetadataV2Graph baseGraph) =>
        baseGraph with
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

    private static DefaultHttpContext BuildMultiTenantContext(SecurityContextScenario scenario)
        => CreateHttpContext(scenario, new TenantContextOptions { Enabled = true });

    private static DefaultHttpContext BuildSingleTenantContext(SecurityContextScenario scenario)
        => CreateHttpContext(scenario, new TenantContextOptions { Enabled = false });

    private static DefaultHttpContext CreateHttpContext(
        SecurityContextScenario scenario,
        TenantContextOptions tenantOptions)
    {
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns(scenario.TenantId);
        tenantContext.Source.Returns(scenario.TenantSource);

        var schemaContext = Substitute.For<ISchemaContext>();
        schemaContext.CurrentSchema.Returns(scenario.CurrentSchema);

        var services = new ServiceCollection();
        services.AddSingleton<IOptions<TenantContextOptions>>(Options.Create(tenantOptions));
        services.AddSingleton(tenantContext);
        services.AddSingleton(schemaContext);
        if (scenario.GraphProvider is not null)
        {
            services.AddSingleton(scenario.GraphProvider);
        }

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = BuildPrincipal(scenario),
        };
    }

    private static ClaimsPrincipal BuildPrincipal(SecurityContextScenario scenario)
    {
        var claims = scenario.Roles.Select(role => new Claim(ClaimTypes.Role, role)).ToList();
        if (!string.IsNullOrWhiteSpace(scenario.ClaimTenantId))
        {
            claims.Add(new Claim("tenant_id", scenario.ClaimTenantId));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
