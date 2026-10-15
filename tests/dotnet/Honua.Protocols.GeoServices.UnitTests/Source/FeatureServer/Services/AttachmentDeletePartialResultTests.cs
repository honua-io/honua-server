// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class AttachmentDeletePartialResultTests
{
    [UnitTest]
    public async Task DeleteAttachments_WhenALaterIdThrows_ReturnsPerIdResults()
    {
        var store = Substitute.For<IAttachmentStore>();
        store.DeleteAsync(3, 7L, 11L, Arg.Any<CancellationToken>()).Returns(true);
        store.DeleteAsync(3, 7L, 12L, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new InvalidOperationException("transient store failure")));
        store.DeleteAsync(3, 7L, 13L, Arg.Any<CancellationToken>()).Returns(true);

        var result = await AttachmentHandler.DeleteAttachmentsAsync(
            new DefaultHttpContext(),
            3,
            7L,
            [11L, 12L, 13L],
            store,
            NullLogger<AttachmentOperations>.Instance,
            CancellationToken.None);

        await store.Received(1).DeleteAsync(3, 7L, 13L, Arg.Any<CancellationToken>());
        var ok = result.Should().BeOfType<Ok<DeleteAttachmentsResponse>>().Subject;
        var results = ok.Value!.DeleteAttachmentResults;
        results.Select(item => item.Success).Should().Equal(true, false, true);
        results[0].Error.Should().BeNull();
        results[1].ObjectId.Should().Be(12L);
        results[1].Error!.Code.Should().Be(500);
        results[1].Error.Description.Should().Be("Failed to delete attachment.");
        results[1].Error.Description.Should().NotContain("transient");
        results[2].Error.Should().BeNull();
    }
}
