// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.Server.Tests.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

    [UnitTest]
    public async Task Refresh_AddedFieldMask_AppliesToNextLiveEvent()
    {
        using var manager = CreateManager();
        var current = CreateUnrestrictedSecurity();
        var security = CreateUnrestrictedSecurity();
        security.EnableRefresh(_ => Task.FromResult(current));
        using var session = manager.CreateSession("SSE", null, new StreamSubscriptionFilter(subscriberSecurity: security));

        manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope("west")));
        session.Reader.TryRead(out var before).Should().BeTrue();
        before.Envelope.Attributes!.Should().ContainKey("SECRET");

        current = CreateSecurity();
        await manager.RefreshSubscriberSecurityAsync();

        manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope("west")));
        session.Reader.TryRead(out var after).Should().BeTrue();
        after.Envelope.Attributes!.Keys.Should().Equal("region");
        after.Envelope.ChangedAttributes!.Keys.Should().Equal("region");
        session.DisconnectToken.IsCancellationRequested.Should().BeFalse();
    }

    [UnitTest]
    public async Task Refresh_AddedRowPredicate_HidesSubsequentRows()
    {
        using var manager = CreateManager();
        var current = CreateUnrestrictedSecurity();
        var security = CreateUnrestrictedSecurity();
        security.EnableRefresh(_ => Task.FromResult(current));
        using var session = manager.CreateSession("SSE", null, new StreamSubscriptionFilter(subscriberSecurity: security));

        manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope("east")));
        session.Reader.TryRead(out _).Should().BeTrue();

        current = CreateSecurity();
        await manager.RefreshSubscriberSecurityAsync();

        manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope("east")));
        session.Reader.TryRead(out _).Should().BeFalse("the refreshed row predicate hides region=east");
        security.HasRowPredicates.Should().BeTrue();
    }

    [UnitTest]
    public async Task Refresh_RevokedLayerAccess_EndsSession()
    {
        using var manager = CreateManager();
        var security = CreateUnrestrictedSecurity();
        security.EnableRefresh(_ => Task.FromResult(new StreamSubscriberSecurity(
            new HashSet<string>(["svc"], StringComparer.Ordinal),
            new Dictionary<(string, int), StreamLayerReadPolicy>(),
            new Dictionary<(string, int), StreamLayerReadPolicy>(),
            new HashSet<(string, int)> { ("svc", 0) })));
        using var session = manager.CreateSession("SSE", null, new StreamSubscriptionFilter(subscriberSecurity: security));

        await manager.RefreshSubscriberSecurityAsync();

        session.DisconnectToken.IsCancellationRequested.Should().BeTrue();
        manager.SessionCount.Should().Be(0);
        security.Allows(CreateEnvelope("west")).Should().BeFalse();
    }

    [UnitTest]
    public async Task Refresh_RetiredLayer_KeepsSessionButDeliversNothing()
    {
        using var manager = CreateManager();
        var security = CreateUnrestrictedSecurity();
        security.EnableRefresh(_ => Task.FromResult(new StreamSubscriberSecurity(
            new HashSet<string>(["svc"], StringComparer.Ordinal),
            new Dictionary<(string, int), StreamLayerReadPolicy>(),
            new Dictionary<(string, int), StreamLayerReadPolicy>())));
        using var session = manager.CreateSession("SSE", null, new StreamSubscriptionFilter(subscriberSecurity: security));

        await manager.RefreshSubscriberSecurityAsync();

        session.DisconnectToken.IsCancellationRequested.Should().BeFalse();
        manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope("west")));
        session.Reader.TryRead(out _).Should().BeFalse();
    }

    [UnitTest]
    public async Task Refresh_PolicySourceFailure_FailsClosedAndEndsSession()
    {
        using var manager = CreateManager();
        var security = CreateUnrestrictedSecurity();
        security.EnableRefresh(_ => Task.FromException<StreamSubscriberSecurity>(new InvalidOperationException("store down")));
        using var session = manager.CreateSession("SSE", null, new StreamSubscriptionFilter(subscriberSecurity: security));

        await manager.RefreshSubscriberSecurityAsync();

        session.DisconnectToken.IsCancellationRequested.Should().BeTrue();
        security.Allows(CreateEnvelope("west")).Should().BeFalse();
    }

    [UnitTest]
    public void Project_RouteRetractedAfterAllows_DropsAttributesInsteadOfThrowing()
    {
        var security = new StreamSubscriberSecurity(
            new HashSet<string>(["svc"], StringComparer.Ordinal),
            new Dictionary<(string, int), StreamLayerReadPolicy>(),
            new Dictionary<(string, int), StreamLayerReadPolicy>());

        var projected = security.Project(CreateEnvelope("west"));

        projected.Attributes.Should().BeNull();
        projected.ChangedAttributes.Should().BeNull();
    }

    private static FeatureStreamSessionManager CreateManager()
        => new(
            Options.Create(new FeatureStreamOptions()),
            NullLogger<FeatureStreamSessionManager>.Instance,
            TestTelemetry.CreateFeatureStreamMetrics());

    private static StreamSubscriberSecurity CreateUnrestrictedSecurity()
    {
        var policy = new StreamLayerReadPolicy([], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return new StreamSubscriberSecurity(
            new HashSet<string>(["svc"], StringComparer.Ordinal),
            new Dictionary<(string, int), StreamLayerReadPolicy> { [("svc", 0)] = policy },
            new Dictionary<(string, int), StreamLayerReadPolicy> { [("PUBLIC-NAME", 0)] = policy });
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
