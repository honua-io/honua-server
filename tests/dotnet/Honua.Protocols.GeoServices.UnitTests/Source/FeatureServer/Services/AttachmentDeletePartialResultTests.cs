// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
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
        var failed = results[1].Error;
        failed.Should().NotBeNull();
        var error = failed!;
        error.Code.Should().Be(500);
        error.Description.Should().Be("Failed to delete attachment.");
        error.Description.Should().NotContain("transient");
        results[2].Error.Should().BeNull();

        // Execute Results.Ok through HTTP's global serializer, whose null policy
        // may differ from FeatureServerJsonContext's source-generated policy.
        await using var services = new ServiceCollection().AddLogging()
            .ConfigureHttpJsonOptions(options => options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Position = 0;
        using var document = await JsonDocument.ParseAsync(body);
        var wireResults = document.RootElement.GetProperty("deleteAttachmentResults");
        wireResults.GetArrayLength().Should().Be(3);
        foreach (var index in new[] { 0, 2 })
        {
            wireResults[index].GetProperty("success").GetBoolean().Should().BeTrue();
            wireResults[index].TryGetProperty("error", out _).Should().BeFalse();
            wireResults[index].GetProperty("globalId").ValueKind.Should().Be(JsonValueKind.Null);
        }
        wireResults[1].GetProperty("success").GetBoolean().Should().BeFalse();
        wireResults[1].GetProperty("error").GetProperty("code").GetInt32().Should().Be(500);
        wireResults[1].GetProperty("error").GetProperty("description").GetString()
            .Should().Be("Failed to delete attachment.");
    }
}
