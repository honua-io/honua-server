// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.RouteValues["serviceId"] = "parcels";
        context.Response.Body = body;

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
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Position = 0;
        var response = JsonSerializer.Deserialize(
            body,
            FeatureServerJsonContext.Default.AttachmentQueryResponse);
        response.Should().NotBeNull();
        response!.AttachmentGroups.Should().ContainSingle();
        response.AttachmentGroups[0].ParentObjectId.Should().Be(7);
        response.AttachmentGroups[0].AttachmentInfos.Should().ContainSingle()
            .Which.Url.Should().Be("/rest/services/parcels/FeatureServer/0/7/attachments/5");
    }
}
