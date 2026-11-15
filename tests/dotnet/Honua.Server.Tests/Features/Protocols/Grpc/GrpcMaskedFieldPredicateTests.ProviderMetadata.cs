// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcMaskedFieldPredicateTests
{
    [UnitTheory]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeLayer")]
    public async Task DescribeLayer_IntersectsRuntimeProviderWritesAndDeclaredCapabilities(
        bool writableProvider, bool hasWriter, bool queryOnly, bool expected)
    {
        var triple = ProviderMetadataTriple(queryOnly);
        _resourceValidator.ValidateServiceLayerV2Async("test", 0, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(triple));
        var provider = Substitute.For<IFeatureDataProvider>();
        provider.ProviderName.Returns(DataProviderNames.Postgis);
        provider.Reader.Returns(_featureReader);
        provider.Writer.Returns(hasWriter ? Substitute.For<IFeatureWriter>() : null);
        provider.Capabilities.Returns(writableProvider
            ? FeatureProviderCapabilities.ReadWritePostgis : FeatureProviderCapabilities.ReadOnlyMySql);
        var router = new FeatureProviderQueryRouter(Substitute.For<ISecureConnectionRegistry>(),
            new FeatureDataProviderRegistry([provider]));
        using var context = CreateCallContext(services => services.AddSingleton(router));

        var response = await _sut.DescribeLayer(new() { ServiceId = "test", LayerId = 0 }, context);

        response.Layer.Capabilities.Create.Should().Be(expected);
        response.Layer.Capabilities.Update.Should().Be(expected);
        response.Layer.Capabilities.Delete.Should().Be(expected);
        response.Layer.Capabilities.Query.Should().BeTrue();
        AssertNoRead();
    }

    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeLayer")]
    public async Task DescribeLayer_OwnershipPolicyWithoutTracking_ProjectsEditingInfo(bool enabled)
    {
        var triple = CreateTriple();
        var resource = triple.Resource with
        {
            Editing = null,
            OwnerEditPolicy = new() { Enabled = enabled, OwnerField = "name" }
        };
        _resourceValidator.ValidateServiceLayerV2Async("test", 0, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(triple with { Resource = resource }));
        using var context = CreateCallContext();

        var response = await _sut.DescribeLayer(new() { ServiceId = "test", LayerId = 0 }, context);

        response.Layer.EditingInfo.Should().NotBeNull();
        response.Layer.EditingInfo.OwnershipAccessControlEnabled.Should().Be(enabled);
        response.Layer.EditingInfo.CreatorField.Should().BeEmpty();
        response.Layer.EditingInfo.EditorField.Should().BeEmpty();
        response.Layer.EditingInfo.CreatedAtField.Should().BeEmpty();
        response.Layer.EditingInfo.EditedAtField.Should().BeEmpty();
        AssertNoRead();
    }

    private static MetadataV2ServiceLayerTriple ProviderMetadataTriple(bool queryOnly)
    {
        var triple = CreateTriple();
        var resource = triple.Resource with { StorageBindingIds = ["managed-binding"] };
        var publication = triple.Publication with
        {
            StorageBindingId = "managed-binding",
            Capabilities = queryOnly ? ["Query"] : ["Query", "Create", "Update", "Delete"]
        };
        var binding = new MetadataV2StorageBinding
        {
            Metadata = new() { Id = "managed-binding", Name = "managed-binding" },
            ResourceId = resource.Metadata.Id,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "public.features",
            StorageLayerId = 0
        };
        var snapshot = new MetadataV2GraphSnapshot(new()
        {
            Revision = 1,
            Environment = "test",
            Resources = [resource],
            Services = [_service],
            Publications = [publication],
            StorageBindings = [binding]
        }, "provider-metadata", DateTimeOffset.UtcNow);
        return triple with { Resource = resource, Publication = publication, Snapshot = snapshot };
    }
}
