// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    [UnitTheory]
    [InlineData(BinaryOperator.Like)]
    [InlineData(BinaryOperator.NotLike)]
    public void Broadcast_UnevaluableRowPredicate_OnlyDeniesThatSubscriber(BinaryOperator operation)
    {
        var predicate = new BinaryExpression(new PropertyReference("region"), operation,
            new Literal(new string('%', 33), LiteralType.Text));

        AssertOnlyRestrictedSubscriberDenied(predicate);
    }

    [UnitTheory]
    [InlineData(BinaryOperator.Like)]
    [InlineData(BinaryOperator.NotLike)]
    public void Broadcast_NegatedUnevaluableRowPredicate_OnlyDeniesThatSubscriber(BinaryOperator operation)
    {
        var predicate = new BinaryExpression(new PropertyReference("region"), operation,
            new Literal(new string('%', 33), LiteralType.Text));
        var negated = new UnaryExpression(UnaryOperator.Not,
            new BinaryExpression(predicate, BinaryOperator.Or, new Literal(false, LiteralType.Boolean)));

        AssertOnlyRestrictedSubscriberDenied(negated);
    }

    [UnitTheory]
    [InlineData("secret")]
    [InlineData("SECRET")]
    public void Matches_AttributeFilterOverMaskedField_DeliversNothing(string field)
    {
        var filter = new StreamSubscriptionFilter(
            attributeFilter: IsNotNull(field),
            subscriberSecurity: CreateSecurity());
        var storedUnderFilterName = $$"""{"region":"west","{{field}}":"private"}""";

        filter.FindMaskedFilterField().Should().Be(field);
        filter.Matches(CreateEnvelope("west"), null, storedUnderFilterName).Should().BeFalse();
    }

    [UnitTest]
    public void Matches_NegatedAttributeFilterOverMaskedField_DeliversNothing()
    {
        var isNull = new UnaryExpression(UnaryOperator.IsNull, new PropertyReference("SECRET"));
        var filter = new StreamSubscriptionFilter(
            attributeFilter: new UnaryExpression(UnaryOperator.Not, isNull),
            subscriberSecurity: CreateSecurity());

        filter.FindMaskedFilterField().Should().Be("SECRET");
        filter.Matches(CreateEnvelope("west"), null, PropertiesJson).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("PUBLIC-NAME")]
    [InlineData("")]
    public void Matches_AttributeFilterOverMaskedField_OnNamedAndServiceLessRoutes_DeliversNothing(string serviceId)
    {
        var filter = new StreamSubscriptionFilter(
            attributeFilter: IsNotNull("SECRET"),
            subscriberSecurity: CreateSecurity());
        var readable = new StreamSubscriptionFilter(
            attributeFilter: IsNotNull("region"),
            subscriberSecurity: CreateSecurity());
        var envelope = CreateEnvelope("west") with { ServiceId = serviceId };

        filter.Matches(envelope, null, PropertiesJson).Should().BeFalse();
        readable.Matches(envelope, null, PropertiesJson).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("SECRET", null)]
    [InlineData("observed", "SECRET")]
    public void Matches_TemporalFilterOverMaskedField_DeliversNothing(string startField, string? endField)
    {
        var filter = new StreamSubscriptionFilter(
            temporalFilter: new StreamTemporalFilter(startField, endField, TemporalWindowStart, TemporalWindowEnd),
            subscriberSecurity: CreateSecurity());

        filter.FindMaskedFilterField().Should().Be("SECRET");
        filter.Matches(CreateEnvelope("west"), null, TemporalPropertiesJson).Should().BeFalse();
    }

    [UnitTest]
    public void Matches_TemporalFilterOverReadableField_StillDelivers()
    {
        var filter = new StreamSubscriptionFilter(
            temporalFilter: new StreamTemporalFilter("observed", null, TemporalWindowStart, TemporalWindowEnd),
            subscriberSecurity: CreateSecurity());

        filter.FindMaskedFilterField().Should().BeNull();
        filter.Matches(CreateEnvelope("west"), null, TemporalPropertiesJson).Should().BeTrue();
    }

    [UnitTest]
    public void MasksAny_RouteWithoutCurrentPolicy_CountsAsMasked()
    {
        var security = CreateUnrestrictedSecurity();

        security.MasksAny(CreateEnvelope("west") with { LayerId = 99 }, ["region"]).Should().BeTrue();
        security.MasksAny(CreateEnvelope("west"), ["region"]).Should().BeFalse();
    }

    [UnitTest]
    public void Matches_AttributeFilterOverReadableField_StillDelivers()
    {
        var filter = new StreamSubscriptionFilter(
            attributeFilter: IsNotNull("region"),
            subscriberSecurity: CreateSecurity());

        filter.FindMaskedFilterField().Should().BeNull();
        filter.Matches(CreateEnvelope("west"), null, PropertiesJson).Should().BeTrue();
    }

    [UnitTest]
    public void Matches_AttributeFilterWithoutFieldMask_StillDelivers()
    {
        var filter = new StreamSubscriptionFilter(
            attributeFilter: IsNotNull("SECRET"),
            subscriberSecurity: CreateUnrestrictedSecurity());

        filter.FindMaskedFilterField().Should().BeNull();
        filter.Matches(CreateEnvelope("west"), null, PropertiesJson).Should().BeTrue();
    }

    [UnitTest]
    public void Matches_DeleteWithAttributeFilterOverMaskedField_KeepsRowAuthorizationOnly()
    {
        var filter = new StreamSubscriptionFilter(
            attributeFilter: IsNotNull("SECRET"),
            subscriberSecurity: CreateSecurity());

        filter.Matches(CreateEnvelope("west") with { Operation = "delete" }, null, PropertiesJson).Should().BeTrue();
        filter.Matches(CreateEnvelope("east") with { Operation = "delete" }, null, PropertiesJson).Should().BeFalse();
    }

    [UnitTest]
    public async Task Refresh_AddedFieldMask_StopsDeliveryForFilterOverThatField()
    {
        using var manager = CreateManager();
        var current = CreateUnrestrictedSecurity();
        var security = CreateUnrestrictedSecurity();
        security.EnableRefresh(_ => Task.FromResult(current));
        var filter = new StreamSubscriptionFilter(attributeFilter: IsNotNull("SECRET"), subscriberSecurity: security);
        using var session = manager.CreateSession("SSE", null, filter);

        manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope("west"), propertiesJson: PropertiesJson));
        session.Reader.TryRead(out _).Should().BeTrue("no mask applies before the refresh");

        current = CreateSecurity();
        await manager.RefreshSubscriberSecurityAsync();

        manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope("west"), propertiesJson: PropertiesJson));
        session.Reader.TryRead(out _).Should().BeFalse("the filter reads a field the refreshed policy masks");
        filter.FindMaskedFilterField().Should().Be("SECRET");
    }

    private static void AssertOnlyRestrictedSubscriberDenied(FilterExpression predicate)
    {
        var policy = new StreamLayerReadPolicy([predicate], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var security = new StreamSubscriberSecurity(
            new HashSet<string>(["svc"], StringComparer.Ordinal),
            new Dictionary<(string, int), StreamLayerReadPolicy> { [("svc", 0)] = policy },
            new Dictionary<(string, int), StreamLayerReadPolicy>());
        using var manager = CreateManager();
        using var denied = manager.CreateSession("SSE", null, new StreamSubscriptionFilter(subscriberSecurity: security));
        using var open = manager.CreateSession("SSE", null);

        manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope("west")));
        manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope("west") with { Cursor = 2 }));

        denied.Reader.TryRead(out _).Should().BeFalse();
        open.Reader.TryRead(out _).Should().BeTrue();
        open.Reader.TryRead(out var second).Should().BeTrue();
        second.Envelope.Cursor.Should().Be(2);
    }

    private const string PropertiesJson = """{"region":"west","SECRET":"private"}""";

    private const string TemporalPropertiesJson =
        """{"region":"west","observed":"2023-01-15T00:00:00Z","SECRET":"2023-01-16T00:00:00Z"}""";

    private static readonly DateTimeOffset TemporalWindowStart = new(2023, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset TemporalWindowEnd = new(2023, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private static UnaryExpression IsNotNull(string field)
        => new(UnaryOperator.IsNotNull, new PropertyReference(field));

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
