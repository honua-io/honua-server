// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class AttachmentReturnUrlLayerIdTests
{
    [UnitTest]
    public async Task QueryAttachments_ReturnUrlUsesPublicLayerId()
    {
        var store = Substitute.For<IAttachmentStore>();
        store.ListAsync(37, 99L, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new[]
            {
                Attachment.Create(
                    5,
                    99,
                    37,
                    "photo.jpg",
                    "image/jpeg",
                    12,
                    DateTime.UtcNow,
                    "blob")
            }));
        var context = new DefaultHttpContext();
        context.Request.RouteValues["serviceId"] = "parcels";

        var result = await AttachmentHandler.QueryAttachmentsAsync(
            37,
            [7L],
            returnUrl: true,
            AttachmentQueryFilter.None,
            store,
            NullLogger<AttachmentOperations>.Instance,
            context,
            CancellationToken.None,
            new Dictionary<long, long> { [7] = 99 },
            publicLayerId: 0);

        await store.Received(1).ListAsync(37, 99L, Arg.Any<CancellationToken>());
        var ok = result.Should().BeOfType<Ok<AttachmentQueryResponse>>().Subject;
        ok.Value!.AttachmentGroups.Should().ContainSingle();
        ok.Value.AttachmentGroups[0].ParentObjectId.Should().Be(7);
        ok.Value.AttachmentGroups[0].AttachmentInfos.Should().ContainSingle()
            .Which.Url.Should().Be("/rest/services/parcels/FeatureServer/0/7/attachments/5");
    }
}
