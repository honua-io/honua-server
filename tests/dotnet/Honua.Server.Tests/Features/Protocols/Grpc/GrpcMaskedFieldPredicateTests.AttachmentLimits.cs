// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcMaskedFieldPredicateTests
{
    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeLayer")]
    public async Task DescribeLayer_AttachmentLimits_AdvertisesEffectiveConfiguredLimits()
    {
        var triple = CreateTriple();
        var resource = triple.Resource with { Editing = new MetadataV2ResourceEditing { SupportsAttachments = true } };
        _resourceValidator.ValidateServiceLayerV2Async("test", 0, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(triple with { Resource = resource }));
        var limits = new LimitsOptions { MaxUploadSizeBytes = 512000 };
        limits.Attachments.MaxAttachmentSize = 900000;
        limits.Attachments.MaxAttachmentsPerFeature = 3;
        limits.Attachments.MaxTotalAttachmentSize = 1500000;
        limits.Attachments.AllowedMimeTypes = " image/png, application/pdf, ";
        using var context = CreateCallContext(services => services.AddSingleton(Options.Create(limits)));

        var response = await _sut.DescribeLayer(new() { ServiceId = "test", LayerId = 0 }, context);

        response.Layer.AttachmentLimits.MaxFileBytes.Should().Be(512000);
        response.Layer.AttachmentLimits.MaxUnaryUploadBytes.Should().Be(512000);
        response.Layer.AttachmentLimits.MaxChunkBytes.Should().Be(1048576);
        response.Layer.AttachmentLimits.MaxAttachmentsPerFeature.Should().Be(3);
        response.Layer.AttachmentLimits.MaxTotalBytesPerFeature.Should().Be(1500000);
        response.Layer.AttachmentLimits.AllowedContentTypes.Should().Equal("image/png", "application/pdf");
        response.Layer.Capabilities.ListAttachments.Should().BeTrue();
        response.Layer.Capabilities.UploadAttachments.Should().BeFalse();
        AssertNoRead();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeLayer")]
    public async Task DescribeLayer_WithoutAttachments_DoesNotAdvertiseUploadsOrLimits()
    {
        using var context = CreateCallContext();
        var response = await _sut.DescribeLayer(new() { ServiceId = "test", LayerId = 0 }, context);
        response.Layer.AttachmentLimits.MaxFileBytes.Should().Be(0);
        response.Layer.Capabilities.ListAttachments.Should().BeFalse();
        response.Layer.Capabilities.UploadAttachments.Should().BeFalse();
        response.Layer.Capabilities.DeleteAttachments.Should().BeFalse();
        AssertNoRead();
    }
}
