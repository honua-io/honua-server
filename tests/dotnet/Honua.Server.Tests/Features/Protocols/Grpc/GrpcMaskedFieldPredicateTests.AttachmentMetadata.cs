// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcMaskedFieldPredicateTests
{
    [UnitTheory]
    [InlineData("Update", "admin", true, true, true, false)]
    [InlineData("Delete", "admin", true, true, false, true)]
    [InlineData("Query", "admin", true, true, false, false)]
    [InlineData("Update,Delete", "reader", true, true, false, false)]
    [InlineData("Update,Delete", "admin", false, true, false, false)]
    [InlineData("Update,Delete", "admin", true, false, false, false)]
    [InlineData("Update,Delete", "admin", true, true, true, true)]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeLayer")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/DescribeLayer")]
    public async Task DescribeLayer_AttachmentPolicy_ProjectsEffectiveOperations(
        string declared, string role, bool canModify, bool attachments, bool upload, bool delete)
    {
        var triple = CreateTriple();
        triple = triple with
        {
            Publication = triple.Publication with { Capabilities = declared.Split(',') },
            Resource = triple.Resource with { Editing = new() { CanModify = canModify, SupportsAttachments = attachments } }
        };
        _resourceValidator.ValidateServiceLayerV2Async("test", 0, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(triple));
        using var context = CreateCallContext();
        ((HttpContext)context.UserState["__HttpContext"]).User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "reader"), new Claim(ClaimTypes.Role, role)], "Test"));

        var layer = (await _sut.DescribeLayer(new() { ServiceId = "test", LayerId = 0 }, context)).Layer;

        layer.Capabilities.ListAttachments.Should().BeTrue();
        layer.Capabilities.UploadAttachments.Should().Be(upload);
        layer.Capabilities.DeleteAttachments.Should().Be(delete);
        layer.Capabilities.Update.Should().BeFalse("attachment storage does not require a feature writer");
        AssertNoRead();
    }

    [UnitTheory]
    [InlineData(524288, 2097152, 524288, 524288)]
    [InlineData(8388608, 2097152, 2097152, 1048576)]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeLayer")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/DescribeLayer")]
    public async Task DescribeLayer_ConfiguredAttachmentLimits_ProjectsEffectiveUploadBounds(
        long uploadBytes, long attachmentBytes, long fileBytes, long unaryBytes)
    {
        var limits = new LimitsOptions
        {
            MaxUploadSizeBytes = uploadBytes,
            Attachments = new()
            {
                MaxAttachmentSize = attachmentBytes, MaxAttachmentsPerFeature = 3,
                MaxTotalAttachmentSize = 9000000, AllowedMimeTypes = " image/*, ,application/pdf,text/plain "
            }
        };
        using var context = CreateCallContext(services => services.AddSingleton(Options.Create(limits)));

        var layer = (await _sut.DescribeLayer(new() { ServiceId = "test", LayerId = 0 }, context)).Layer;

        layer.AttachmentLimits.Should().NotBeNull();
        layer.AttachmentLimits.MaxFileBytes.Should().Be(fileBytes);
        layer.AttachmentLimits.MaxUnaryUploadBytes.Should().Be(unaryBytes);
        layer.AttachmentLimits.MaxChunkBytes.Should().Be(1048576);
        layer.AttachmentLimits.MaxAttachmentsPerFeature.Should().Be(3);
        layer.AttachmentLimits.MaxTotalBytesPerFeature.Should().Be(9000000);
        layer.AttachmentLimits.AllowedContentTypes.Should().Equal("image/*", "application/pdf", "text/plain");
        AssertNoRead();
    }

    [UnitTheory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [Endpoint("POST /geospatial.v1.FeatureService/DescribeService")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/DescribeService")]
    public async Task DescribeService_AttachmentEditingOnVisibleOrHiddenLayer_AggregatesOnlyVisibleCapabilities(
        int editingLayer, bool expected)
    {
        var graph = ServiceMetadataSnapshot(1).Graph;
        var resources = graph.Resources.Select((resource, index) => resource with
        {
            Editing = index == editingLayer ? new MetadataV2ObjectEditing { SupportsAttachments = true } : null
        }).ToArray();
        var snapshot = new MetadataV2GraphSnapshot(graph with { Resources = resources }, "attachments", DateTimeOffset.UtcNow);
        _resourceValidator.ValidateServiceV2Async("test", Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(_service, snapshot));
        using var context = CreateCallContext();

        var service = (await _sut.DescribeService(new() { ServiceId = "test", PageSize = 1 }, context)).Service;

        service.Capabilities.ListAttachments.Should().BeTrue();
        service.Capabilities.UploadAttachments.Should().Be(expected);
        service.Capabilities.DeleteAttachments.Should().Be(expected);
        AssertNoRead();
    }
}
