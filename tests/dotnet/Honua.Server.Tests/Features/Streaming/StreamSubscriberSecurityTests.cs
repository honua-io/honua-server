// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>Subscriber projection and fail-closed event image authorization.</summary>
[Protocol(TestProtocols.Streaming)]
public sealed class StreamSubscriberSecurityTests
{
    [UnitTest]
    public void Project_RemovesMaskedValuesWithoutChangingOtherSubscribersEvent()
    {
        var security = CreateSecurity();
        var envelope = CreateEnvelope("west");

        security.Allows(envelope).Should().BeTrue();
        var projected = security.Project(envelope);

        projected.Attributes!.Keys.Should().Equal("region");
        projected.ChangedAttributes!.Keys.Should().Equal("region");
        envelope.Attributes!.Should().ContainKey("SECRET");
        envelope.ChangedAttributes!.Should().ContainKey("secret");
    }

    [UnitTest]
    public void Allows_HiddenRowAndMissingDeleteBeforeImage_FailClosed()
    {
        var security = CreateSecurity();
        security.Allows(CreateEnvelope("east")).Should().BeFalse();
        security.Allows(CreateEnvelope("east") with { Operation = "delete" }).Should().BeFalse();
        security.Allows(CreateEnvelope("west") with { Operation = "delete", Attributes = null }).Should().BeFalse();
        security.Allows(CreateEnvelope("west") with { Operation = "delete" }).Should().BeTrue();
    }

    [UnitTest]
    public void Allows_UnknownPublication_FailsClosed()
    {
        var security = CreateSecurity();
        security.Allows(CreateEnvelope("west") with { ServiceId = "other-tenant" }).Should().BeFalse();
        security.Allows(CreateEnvelope("west") with { LayerId = 99 }).Should().BeFalse();
        security.Allows(CreateEnvelope("west") with { ServiceId = "PUBLIC-NAME" }).Should().BeTrue();
    }

    private static StreamSubscriberSecurity CreateSecurity()
    {
        var policy = new StreamLayerReadPolicy(
            [new BinaryExpression(new PropertyReference("region"), BinaryOperator.Equal, new Literal("west", LiteralType.Text))],
            new HashSet<string>(["secret"], StringComparer.OrdinalIgnoreCase));
        return new StreamSubscriberSecurity(
            new HashSet<string>(["svc"], StringComparer.Ordinal),
            new Dictionary<(string, int), StreamLayerReadPolicy> { [("svc", 0)] = policy },
            new Dictionary<(string, int), StreamLayerReadPolicy> { [("PUBLIC-NAME", 0)] = policy });
    }

    private static FeatureStreamEnvelope CreateEnvelope(string region)
        => new()
        {
            EventId = "event",
            Cursor = 1,
            Timestamp = DateTimeOffset.UtcNow,
            Protocol = "rest",
            RequestId = "request",
            ServiceId = "svc",
            LayerId = 0,
            ObjectId = 1,
            FeatureId = "1",
            Operation = "update",
            Attributes = new Dictionary<string, JsonElement>
            {
                ["region"] = JsonSerializer.SerializeToElement(region),
                ["SECRET"] = JsonSerializer.SerializeToElement("private")
            },
            ChangedAttributes = new Dictionary<string, object?> { ["region"] = region, ["secret"] = "private" }
        };
}
