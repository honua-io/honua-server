// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.GeoServices.FeatureServer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// The GeoServices <c>layers</c> parameter (createReplica / extractChanges) is a JSON
/// array per the Esri spec. The ArcGIS API for Python
/// (<c>FeatureLayerCollection.extract_changes</c> / <c>create_replica</c>) sends the
/// bracketed JSON-array form (<c>[0]</c> / <c>[0,1]</c>), while the comma-separated
/// form (<c>0</c> / <c>0,1</c>) is also widely used. Both forms must parse identically.
/// </summary>
public sealed class FeatureServerLayerListParsingTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("[0]")]
    [InlineData(" [0] ")]
    [InlineData("[ 0 ]")]
    public void TryParseLayerIdList_SingleLayer_AcceptsCommaAndJsonArrayForms(string raw)
    {
        var ok = FeatureServerEndpoints.TryParseLayerIdList(raw, out var ids, out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        ids.Should().Equal(0);
    }

    [Theory]
    [InlineData("0,1")]
    [InlineData("[0,1]")]
    [InlineData("[0, 1]")]
    [InlineData(" [ 0 , 1 ] ")]
    public void TryParseLayerIdList_MultipleLayers_AcceptsCommaAndJsonArrayForms(string raw)
    {
        var ok = FeatureServerEndpoints.TryParseLayerIdList(raw, out var ids, out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        ids.Should().BeEquivalentTo([0, 1]);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("[a]")]
    [InlineData("[0,a]")]
    [InlineData("[0,]")]
    [InlineData("[]")]
    public void TryParseLayerIdList_NonNumericOrEmpty_ReturnsError(string raw)
    {
        var ok = FeatureServerEndpoints.TryParseLayerIdList(raw, out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("[0]", "0")]
    [InlineData("[0,1]", "0,1")]
    [InlineData(" [ 0 , 1 ] ", " 0 , 1 ")]
    public void StripLayerListBrackets_RemovesOuterBrackets(string raw, string expected)
    {
        FeatureServerEndpoints.StripLayerListBrackets(raw).Should().Be(expected);
    }

    [Fact]
    public void TryResolveRequestedServiceLayersV2_RetiredLayer_IsNotSelectableOrReturnedByDefault()
    {
        var activeStatus = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active };
        var retiredStatus = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Retired };
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-a", Name = "alpha" },
        };
        var activeResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-active" },
            Status = activeStatus,
        };
        var retiredResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-retired" },
            Status = retiredStatus,
        };
        var activePublication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "publication-active" },
            ServiceId = service.Metadata.Id,
            ResourceId = activeResource.Metadata.Id,
            LayerIndex = 0,
            Status = activeStatus,
        };
        var retiredPublication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "publication-retired" },
            ServiceId = service.Metadata.Id,
            ResourceId = retiredResource.Metadata.Id,
            LayerIndex = 1,
            Status = retiredStatus,
        };
        var snapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph
            {
                Services = [service],
                Resources = [activeResource, retiredResource],
                Publications = [activePublication, retiredPublication],
            },
            "\"test\"",
            DateTimeOffset.UtcNow);

        var defaultResult = FeatureServerEndpoints.TryResolveRequestedServiceLayersV2(
            service,
            snapshot,
            new Dictionary<string, StringValues>(),
            out var defaultLayers,
            out var selectorSpecified,
            out var defaultError);
        var retiredResult = FeatureServerEndpoints.TryResolveRequestedServiceLayersV2(
            service,
            snapshot,
            new Dictionary<string, StringValues> { ["layerId"] = "1" },
            out var retiredLayers,
            out var retiredSelectorSpecified,
            out var retiredError);

        defaultResult.Should().BeTrue();
        selectorSpecified.Should().BeFalse();
        defaultError.Should().BeNull();
        defaultLayers.Should().ContainSingle().Which.Publication.Should().Be(activePublication);
        retiredResult.Should().BeFalse();
        retiredSelectorSpecified.Should().BeTrue();
        retiredLayers.Should().BeEmpty();
        retiredError.Should().Contain("valid layer identifiers");
    }

    [Fact]
    public void FilterAccessibleLayersV2_NonServingPublicationResourceOrBinding_IsNotVisible()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
                .BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity([], "test")),
        };
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-a" },
        };
        var activeStatus = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active };
        var draftStatus = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Draft };
        var retiredStatus = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Retired };
        var activeResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-active" },
            Status = activeStatus,
        };
        var retiredResource = activeResource with
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-retired" },
            Status = retiredStatus,
        };
        var draftBindingResource = activeResource with
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-draft-binding" },
            StorageBindingIds = ["binding-draft"],
        };
        var activePublication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "publication-active" },
            ServiceId = service.Metadata.Id,
            ResourceId = activeResource.Metadata.Id,
            Status = activeStatus,
        };
        var retiredPublication = activePublication with
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "publication-retired" },
            Status = retiredStatus,
        };
        var retiredResourcePublication = activePublication with
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "publication-retired-resource" },
            ResourceId = retiredResource.Metadata.Id,
        };
        var draftBindingPublication = activePublication with
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "publication-draft-binding" },
            ResourceId = draftBindingResource.Metadata.Id,
            StorageBindingId = "binding-draft",
        };
        var draftBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-draft" },
            ResourceId = draftBindingResource.Metadata.Id,
            Status = draftStatus,
        };
        var snapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph
            {
                Services = [service],
                Resources = [activeResource, retiredResource, draftBindingResource],
                StorageBindings = [draftBinding],
                Publications =
                [
                    activePublication,
                    retiredPublication,
                    retiredResourcePublication,
                    draftBindingPublication,
                ],
            },
            "\"filter-test\"",
            DateTimeOffset.UtcNow);

        var visible = FeatureServerEndpoints.FilterAccessibleLayersV2(
            context,
            snapshot,
            service,
            [
                (activePublication, activeResource),
                (retiredPublication, activeResource),
                (retiredResourcePublication, retiredResource),
                (draftBindingPublication, draftBindingResource),
            ]);

        visible.Should().ContainSingle().Which.Should().Be((activePublication, activeResource));
    }
}
