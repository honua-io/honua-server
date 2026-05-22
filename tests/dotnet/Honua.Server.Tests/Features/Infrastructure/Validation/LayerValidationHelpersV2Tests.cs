// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Validation;

[Protocol(Honua.TestKit.Constants.Protocols.TestQuality)]
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
    public async Task ValidateLayerWithAccessV2_RequiredServiceTypeMismatch_ReturnsNotFound()
    {
        var (context, _) = BuildContext(allowAnonymous: true);

        var result = await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
            context,
            layerId: 0,
            scope: AccessScope.Read,
            requiredServiceType: MetadataV2ServiceType.EsriImageService);

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
    public async Task ValidateCollectionWithAccessV2_NumericLayerIndex_ReturnsTriple()
    {
        var (context, _) = BuildContext(allowAnonymous: true);

        var result = await LayerValidationHelpers.ValidateCollectionWithAccessV2Async(
            context,
            collectionId: "0");

        result.IsValid.Should().BeTrue();
        result.Publication.Should().NotBeNull();
        result.Publication!.LayerIndex.Should().Be(0);
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

    private static (HttpContext Context, MetadataV2Graph Graph) BuildContext(bool allowAnonymous)
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddService("svc-test", "test-service", MetadataV2ServiceType.OgcApiFeatures)
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
