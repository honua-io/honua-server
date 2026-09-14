// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Validation;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Validation;

[Protocol(Honua.TestKit.Constants.ProtocolNames.TestQuality)]
public sealed class LayerValidationHelpersV2Tests
{
    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task ValidateLayerWithAccessV2_PublicationMatch_ReturnsTriple()
    {
        var (context, _) = BuildContext(allowAnonymous: true);

        var result = await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
            context,
            layerId: 0);

        result.IsValid.Should().BeTrue();
        result.Publication.Should().NotBeNull();
        result.Publication!.LayerIndex.Should().Be(0);
        result.Resource.Should().NotBeNull();
        result.Service.Should().NotBeNull();
        result.ErrorResult.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task ValidateLayerWithAccessV2_MissingLayer_ReturnsNotFound()
    {
        var (context, _) = BuildContext(allowAnonymous: true);

        var result = await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
            context,
            layerId: 999);

        result.IsValid.Should().BeFalse();
        result.Publication.Should().BeNull();
        result.ErrorResult.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task ValidateLayerWithAccessV2_DeniedAccess_ReturnsForbidden()
    {
        var (context, _) = BuildContext(allowAnonymous: false);

        var result = await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
            context,
            layerId: 0);

        result.IsValid.Should().BeFalse();
        result.ErrorResult.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task ValidateLayerWithAccessV2_RequiredProtocolMismatch_ReturnsNotFound()
    {
        var (context, _) = BuildContext(allowAnonymous: true);

        var result = await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
            context,
            layerId: 0,
            scope: AccessScope.Read,
            requiredProtocol: ServiceProtocols.ImageServer);

        result.IsValid.Should().BeFalse();
        result.ErrorResult.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task ValidateCollectionWithAccessV2_PublicationMatch_ReturnsTriple()
    {
        var (context, _) = BuildContext(allowAnonymous: true);

        var result = await LayerValidationHelpers.ValidateCollectionWithAccessV2Async(
            context,
            collectionId: "test-collection");

        result.IsValid.Should().BeTrue();
        result.Publication.Should().NotBeNull();
        result.Resource.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task ValidateCollectionWithAccessV2_MissingCollection_ReturnsNotFound()
    {
        var (context, _) = BuildContext(allowAnonymous: true);

        var result = await LayerValidationHelpers.ValidateCollectionWithAccessV2Async(
            context,
            collectionId: "does-not-exist");

        result.IsValid.Should().BeFalse();
        result.ErrorResult.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task ValidateLayerWithAccessV2_TenantScopedLayerHiddenFromUnauthenticatedDefaultTenant_ChallengesInsteadOfNotFound()
    {
        // The deployed pipeline gives a request without a valid credential the default
        // "public" tenant, which cannot see tenant-a's layer. The receipt candidate answered
        // 404 "Layer 10 not found" there (honua-server#4778).
        var context = BuildTenantScopedContext(requestTenant: "public", authenticatedTenant: null);

        var result = await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
            context,
            layerId: 0,
            LayerValidationHelpers.ValidationProtocol.OData);

        result.IsValid.Should().BeFalse();
        result.Publication.Should().BeNull("a hidden layer's metadata must not reach the caller");
        await result.ErrorResult!.ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.Headers.WWWAuthenticate.ToString().Should().NotBeNullOrEmpty();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task ValidateLayerWithAccessV2_TenantScopedLayerHiddenFromAuthenticatedOtherTenant_KeepsNotFound()
    {
        var context = BuildTenantScopedContext(requestTenant: "tenant-b", authenticatedTenant: "tenant-b");

        var result = await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
            context,
            layerId: 0,
            LayerValidationHelpers.ValidationProtocol.OData);

        result.IsValid.Should().BeFalse();
        await result.ErrorResult!.ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound, "another tenant's principal keeps tenant concealment");
        context.Response.Headers.WWWAuthenticate.ToString().Should().BeEmpty();
    }

    private static HttpContext BuildTenantScopedContext(string requestTenant, string? authenticatedTenant)
    {
        var (context, graph) = BuildContext(allowAnonymous: false);
        var resource = graph.Resources[0];
        graph = graph with { Resources = [resource with { Metadata = resource.Metadata with { Tenant = "tenant-a" } }] };

        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(requestTenant);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph));
        services.AddSingleton(context.RequestServices.GetRequiredService<IAccessPolicyEvaluator>());
        services.AddSingleton(tenant);
        context.RequestServices = services.BuildServiceProvider();
        context.Request.Path = "/odata/Features(0)";
        context.Response.Body = new MemoryStream();
        if (authenticatedTenant is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", authenticatedTenant)], "PortalToken"));
        }

        return context;
    }

    private static (HttpContext Context, MetadataV2Graph Graph) BuildContext(bool allowAnonymous)
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddService("svc-test", "test-service", protocols: [ServiceProtocols.OgcFeatures])
            .AddResource("res-test", "test-resource")
            .AddPublication(
                "pub-test",
                "svc-test",
                "res-test",
                layerIndex: 0,
                serviceLocalId: "test-collection",
                publicationType: MetadataV2PublicationType.OgcCollection)
            .Build();

        // Apply access policy to the resource.
        var resource = graph.Resources[0] with
        {
            AccessPolicy = new AccessPolicy { AllowAnonymous = allowAnonymous },
        };
        graph = graph with { Resources = [resource] };

        var graphProvider = new TestMetadataV2GraphProvider(graph);
        var evaluator = Substitute.For<IAccessPolicyEvaluator>();
        evaluator.Evaluate(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<AccessPolicy?>(),
                Arg.Any<AccessPolicy?>(),
                Arg.Any<AccessScope>())
            .Returns(call =>
            {
                var layerPolicy = call.ArgAt<AccessPolicy?>(1);
                var servicePolicy = call.ArgAt<AccessPolicy?>(2);
                if (layerPolicy?.AllowAnonymous == true || servicePolicy?.AllowAnonymous == true)
                {
                    return AccessDecision.Allowed();
                }
                var principal = call.ArgAt<ClaimsPrincipal>(0);
                return principal.Identity?.IsAuthenticated == true
                    ? AccessDecision.Allowed()
                    : AccessDecision.RequiresAuth();
            });

        var services = new ServiceCollection();
        services.AddSingleton<IMetadataV2GraphProvider>(graphProvider);
        services.AddSingleton<IAccessPolicyEvaluator>(evaluator);
        var serviceProvider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = serviceProvider };
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        return (context, graph);
    }

}
