// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Google.Protobuf;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcFeatureServiceTests
{
    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/BeginAttachmentUpload")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/BeginAttachmentUpload")]
    public async Task BeginAttachmentUpload_ReducedReceiveLimit_ReturnsUsableChunkSize()
    {
        ArrangeAttachmentEditing();
        var store = Substitute.For<IAttachmentUploadSessionStore>();
        store.BeginAsync(Arg.Any<AttachmentUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(NewAttachmentSession(call.Arg<AttachmentUploadRequest>())));
        using var context = CreateCallContext(null, null, null, services =>
        {
            services.AddSingleton(store);
            services.AddSingleton(Options.Create(new GrpcServiceOptions { MaxReceiveMessageSize = 524288 }));
        });

        var response = await _sut.BeginAttachmentUpload(new() { Upload = ValidAttachmentUpload() }, context);

        response.Session.MaxChunkBytes.Should().Be(524160);
        var wire = new Proto.UploadAttachmentChunkRequest
        {
            SessionId = response.Session.SessionId,
            OffsetBytes = long.MaxValue,
            Sha256 = ByteString.CopyFrom(new byte[32]),
            Content = ByteString.CopyFrom(new byte[(int)response.Session.MaxChunkBytes])
        };
        wire.CalculateSize().Should().BeLessThanOrEqualTo(524288);
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/UploadAttachment")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/UploadAttachment")]
    public async Task UploadAttachment_TransportRequiresChunks_DoesNotBeginUnarySession()
    {
        var store = Substitute.For<IAttachmentUploadSessionStore>();
        using var context = CreateCallContext(null, null, null, services =>
        {
            services.AddSingleton(store);
            services.AddSingleton(Options.Create(new GrpcServiceOptions { MaxReceiveMessageSize = 524288 }));
        });

        var action = () => _sut.UploadAttachment(new()
        {
            Upload = ValidAttachmentUpload(), Content = ByteString.CopyFromUtf8("test")
        }, context);

        (await action.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        await store.DidNotReceiveWithAnyArgs().BeginAsync(default!);
    }
}
