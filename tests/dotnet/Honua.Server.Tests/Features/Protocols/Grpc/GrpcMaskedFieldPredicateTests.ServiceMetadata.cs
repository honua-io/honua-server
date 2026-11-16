// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcMaskedFieldPredicateTests
{
    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeService")]
    public async Task DescribeService_PagesOnlyAuthorizedLayersAndReauthorizesContinuation()
    {
        var snapshot = ServiceMetadataSnapshot(1);
        _resourceValidator.ValidateServiceV2Async("test", Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(_service, snapshot));
        using var context = CreateCallContext();

        var first = await _sut.DescribeService(new() { ServiceId = "test", PageSize = 1 }, context);
        first.Layers.Select(layer => layer.LayerId).Should().Equal(0);
        first.NextPageToken.Should().NotBeEmpty();
        var second = await _sut.DescribeService(new()
        {
            ServiceId = "test",
            PageSize = 1,
            PageToken = first.NextPageToken
        }, context);
        second.Layers.Select(layer => layer.LayerId).Should().Equal(2);
        second.NextPageToken.Should().BeEmpty();

        using var deniedContext = CreateCallContext();
        ((Microsoft.AspNetCore.Http.HttpContext)deniedContext.UserState["__HttpContext"]).User = new();
        var resume = () => _sut.DescribeService(new()
        {
            ServiceId = "test",
            PageSize = 1,
            PageToken = first.NextPageToken
        }, deniedContext);
        (await resume.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
        AssertNoRead();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeService")]
    public async Task DescribeService_RejectsContinuationAfterSnapshotRevisionChanges()
    {
        _resourceValidator.ValidateServiceV2Async("test", Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(_service, ServiceMetadataSnapshot(1)));
        using var context = CreateCallContext();
        var first = await _sut.DescribeService(new() { ServiceId = "test", PageSize = 1 }, context);
        _resourceValidator.ValidateServiceV2Async("test", Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(_service, ServiceMetadataSnapshot(2)));

        var resume = () => _sut.DescribeService(new()
        {
            ServiceId = "test",
            PageSize = 1,
            PageToken = first.NextPageToken
        }, context);

        (await resume.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        AssertNoRead();
    }

    private static MetadataV2GraphSnapshot ServiceMetadataSnapshot(long revision)
    {
        var resources = Enumerable.Range(0, 3).Select(index => _resource with
        {
            Metadata = _resource.Metadata with { Id = $"resource-{index}", Name = $"layer-{index}" },
            AccessPolicy = index == 1 ? new AccessPolicy { AllowedRoles = ["private-reader"] } : null
        }).ToArray();
        var publications = resources.Select((resource, index) => CreateTriple().Publication with
        {
            Metadata = new() { Id = $"publication-{index}", Name = $"layer-{index}" },
            ResourceId = resource.Metadata.Id,
            Identifier = new() { Value = index.ToString(System.Globalization.CultureInfo.InvariantCulture), IsNumeric = true }
        }).ToArray();
        return new(new MetadataV2Graph
        {
            Revision = revision,
            Environment = "test",
            Resources = resources,
            Services = [_service],
            Publications = publications
        }, $"revision-{revision}", DateTimeOffset.UtcNow);
    }
}
