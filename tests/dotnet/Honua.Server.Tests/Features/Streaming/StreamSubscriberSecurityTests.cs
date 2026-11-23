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

[Protocol(TestProtocols.Streaming)]
public sealed class StreamSubscriberSecurityTests
{
    [UnitTest]
    public void DescribeUnstreamableLayer_NamedLayerWithoutReadableRoute_IsRefused()
    {
        var security = CreateSecurityWithUnstreamableLayers(7);

        security.DescribeUnstreamableLayer([0, 7]).Should().Contain("Layer 7");
    }

    [UnitTest]
    public void DescribeUnstreamableLayer_UnscopedOrStreamableLayers_IsAccepted()
    {
        var security = CreateSecurityWithUnstreamableLayers(7);

        security.DescribeUnstreamableLayer(null).Should().BeNull("an unscoped subscription stays open");
        security.DescribeUnstreamableLayer([0]).Should().BeNull();
    }

    [UnitTest]
    public void DescribeUnstreamableLayer_LayerReadableThroughAnotherRoute_IsAccepted()
    {
        // Layer 0 is unstreamable in one service but readable in "svc".
        var security = CreateSecurityWithUnstreamableLayers(0);

        security.DescribeUnstreamableLayer([0]).Should().BeNull();
    }

    private static StreamSubscriberSecurity CreateSecurityWithUnstreamableLayers(params int[] layers)
    {
        var policy = new StreamLayerReadPolicy([], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return new StreamSubscriberSecurity(
            new HashSet<string>(["svc"], StringComparer.Ordinal),
            new Dictionary<(string, int), StreamLayerReadPolicy> { [("svc", 0)] = policy },
            new Dictionary<(string, int), StreamLayerReadPolicy>(),
            new HashSet<(string, int)>(layers.Select(layer => ("other", layer))))
        {
            UnstreamablePolicyLayers = layers.ToHashSet(),
        };
    }

    [UnitTest]
    public async Task Delivery_FilterFieldBecomesMasked_DropsQueuedMatch()
    {
        var security = CreateUnrestrictedSecurity();
        security.EnableRefresh(_ => Task.FromResult(CreateSecurity()));
        var filter = new StreamSubscriptionFilter(attributeFilter: IsNotNull("SECRET"), subscriberSecurity: security);
        var envelope = CreateEnvelope("west");
        filter.Matches(envelope, null, PropertiesJson).Should().BeTrue();
        var queued = filter.Project(envelope);

        await security.RefreshAsync(CancellationToken.None);

        filter.PrepareDelivery(queued).Should().BeNull();
    }

    [UnitTheory]
    [InlineData(BinaryOperator.Like)]
    [InlineData(BinaryOperator.NotLike)]
    public async Task Delivery_RefreshedUnevaluableRowPredicate_DropsQueuedFrame(BinaryOperator operation)
    {
        var security = CreateUnrestrictedSecurity();
        var predicate = new BinaryExpression(new PropertyReference("region"), operation,
            new Literal(new string('%', 33), LiteralType.Text));
        var policy = new StreamLayerReadPolicy([predicate], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        security.EnableRefresh(_ => Task.FromResult(new StreamSubscriberSecurity(
            new HashSet<string>(["svc"], StringComparer.Ordinal),
            new Dictionary<(string, int), StreamLayerReadPolicy> { [("svc", 0)] = policy },
            new Dictionary<(string, int), StreamLayerReadPolicy>())));
        var queued = security.Project(CreateEnvelope("west"));

        await security.RefreshAsync(CancellationToken.None);

        security.PrepareDelivery(queued, []).Should().BeNull();
    }

    [UnitTest]
    public void Delivery_RowPredicateOnMaskedField_UsesPrivateAuthorizationImage()
    {
        var policy = new StreamLayerReadPolicy(
            [new BinaryExpression(new PropertyReference("region"), BinaryOperator.Equal, new Literal("west", LiteralType.Text))],
            new HashSet<string>(["region"], StringComparer.OrdinalIgnoreCase));
        var security = new StreamSubscriberSecurity(
            new HashSet<string>(["svc"], StringComparer.Ordinal),
            new Dictionary<(string, int), StreamLayerReadPolicy> { [("svc", 0)] = policy },
            new Dictionary<(string, int), StreamLayerReadPolicy>());
        var projected = security.Project(CreateEnvelope("west"));

        var delivered = security.PrepareDelivery(projected, []);

        delivered.Should().NotBeNull();
        delivered!.Attributes!.Should().NotContainKey("region");
        var json = JsonSerializer.Serialize(delivered, FeatureStreamJsonContext.Default.FeatureStreamEnvelope);
        json.Should().NotContain("AuthorizationAttributes").And.NotContain("authorizationAttributes").And.NotContain("west");
    }

    [UnitTest]
    public async Task Delivery_QueuedFramesUseRefreshedPolicyWithoutSequenceGaps()
    {
        using var manager = CreateManager();
        var security = CreateUnrestrictedSecurity();
        security.EnableRefresh(_ => Task.FromResult(CreateSecurity()));
        using var session = manager.CreateSession("SSE", null, new StreamSubscriptionFilter(subscriberSecurity: security));
        manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope("east")));
        manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope("west") with { EventId = "second", Cursor = 2 }));
        session.Reader.TryRead(out var hidden).Should().BeTrue();
        session.Reader.TryRead(out var visible).Should().BeTrue();

        await manager.RefreshSubscriberSecurityAsync();

        manager.PrepareSubscriptionDelivery(session.SessionId, FeatureStreamSessionManager.DefaultSubscriptionId, 0, hidden.Envelope)
            .Should().BeNull();
        var delivered = manager.PrepareSubscriptionDelivery(
            session.SessionId, FeatureStreamSessionManager.DefaultSubscriptionId, 0, visible.Envelope);
        delivered.Should().NotBeNull();
        delivered!.Sequence.Should().Be(0);
        delivered.Attributes!.Keys.Should().Equal("region");
        delivered.ChangedAttributes!.Keys.Should().Equal("region");
        visible.Envelope.Attributes!.Should().ContainKey("SECRET");
    }

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
        var delivered = filter.PrepareDelivery(filter.Project(CreateEnvelope("west") with { Operation = "delete" }));
        delivered.Should().NotBeNull();
        delivered!.Attributes!.Should().NotContainKey("SECRET");
        filter.PrepareDelivery(CreateEnvelope("east") with { Operation = "delete" }).Should().BeNull();
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

    [UnitTest]
    public void Broadcast_UnsupportedPolicyExpression_DeniesDelivery()
    {
        AssertOnlyRestrictedSubscriberDenied(UnsupportedPolicy());
    }

    [UnitTest]
    public async Task Refresh_UnsupportedPolicyExpression_DeniesLiveAndStoredEvents()
    {
        using var manager = CreateManager();
        var security = CreateUnrestrictedSecurity();
        var policy = new StreamLayerReadPolicy([UnsupportedPolicy()], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        security.EnableRefresh(_ => Task.FromResult(new StreamSubscriberSecurity(
            new HashSet<string>(["svc"], StringComparer.Ordinal),
            new Dictionary<(string, int), StreamLayerReadPolicy> { [("svc", 0)] = policy },
            new Dictionary<(string, int), StreamLayerReadPolicy>())));
        var filter = new StreamSubscriptionFilter(subscriberSecurity: security);
        using var session = manager.CreateSession("SSE", null, filter);

        await manager.RefreshSubscriberSecurityAsync();

        manager.Broadcast(FeatureStreamMessage.Data(CreateEnvelope("west")));
        session.Reader.TryRead(out _).Should().BeFalse();
        filter.Matches(CreateEnvelope("west"), null, PropertiesJson).Should().BeFalse();
    }

    [UnitTest]
    public void Broadcast_PolicyBeyondStreamingDepth_DeniesDelivery()
    {
        FilterExpression predicate = new Literal(false, LiteralType.Boolean);
        for (var depth = 0; depth <= InMemoryFilterEvaluator.MaxStreamingDepth; depth++)
            predicate = new UnaryExpression(UnaryOperator.Not, predicate);

        AssertOnlyRestrictedSubscriberDenied(predicate);
    }

    [UnitTheory]
    [InlineData("OBJECTID")]
    [InlineData("geometry")]
    [InlineData("optional_value")]
    public void Broadcast_PolicyRequiresUnavailableField_DeniesDelivery(string field)
    {
        AssertOnlyRestrictedSubscriberDenied(new UnaryExpression(UnaryOperator.IsNull, new PropertyReference(field)));
    }

    [UnitTest]
    public void Allows_PolicyReadsExplicitNull_PreservesNullSemantics()
    {
        var policy = new StreamLayerReadPolicy(
            [new UnaryExpression(UnaryOperator.IsNull, new PropertyReference("region"))],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var security = new StreamSubscriberSecurity(new HashSet<string>(["svc"], StringComparer.Ordinal),
            new Dictionary<(string, int), StreamLayerReadPolicy> { [("svc", 0)] = policy },
            new Dictionary<(string, int), StreamLayerReadPolicy>());
        var envelope = CreateEnvelope("west") with
        {
            Attributes = new Dictionary<string, JsonElement> { ["region"] = JsonSerializer.SerializeToElement<string?>(null) }
        };

        security.Allows(envelope).Should().BeTrue();
    }

    private static UnaryExpression UnsupportedPolicy()
        => new UnaryExpression(UnaryOperator.Not,
            new SpatialPredicate(SpatialOperator.Intersects, new PropertyReference("geometry"),
                new GeometryLiteral([1, 2], 4326, "WKB")));

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

    [UnitTest]
    public async Task SessionRefresh_RevalidatesEveryActiveEventDrivenSubscription()
    {
        using var manager = CreateManager();
        var first = CreateUnrestrictedSecurity();
        var second = CreateUnrestrictedSecurity();
        first.EnableEventDrivenRefresh();
        second.EnableEventDrivenRefresh();
        var firstRefreshes = 0;
        var secondRefreshes = 0;
        first.EnableRefresh(_ =>
        {
            firstRefreshes++;
            return Task.FromResult(CreateSecurity());
        });
        second.EnableRefresh(_ =>
        {
            secondRefreshes++;
            return Task.FromResult(CreateUnrestrictedSecurity());
        });
        using var session = manager.CreateSession("WebSocket", null, new StreamSubscriptionFilter(subscriberSecurity: first));
        manager.TryAddSubscription(session.SessionId, "second", new StreamSubscriptionFilter(subscriberSecurity: second))
            .Result.Should().Be(AddSubscriptionResult.Added);

        (await manager.RefreshSessionSecurityAsync(session.SessionId, CancellationToken.None)).Should().BeTrue();

        firstRefreshes.Should().Be(1);
        secondRefreshes.Should().Be(1);
        first.Allows(CreateEnvelope("east")).Should().BeFalse("the first subscription must retain its policy refresh after adding another");
        second.Allows(CreateEnvelope("east")).Should().BeTrue();

        first.EnableRefresh(_ => Task.FromResult(new StreamSubscriberSecurity(
            new HashSet<string>(["svc"], StringComparer.Ordinal),
            new Dictionary<(string, int), StreamLayerReadPolicy>(),
            new Dictionary<(string, int), StreamLayerReadPolicy>(),
            new HashSet<(string, int)> { ("svc", 0) })));
        (await manager.RefreshSessionSecurityAsync(session.SessionId, CancellationToken.None)).Should().BeFalse(
            "revoking the earlier subscription must end authorization even when a later subscription remains allowed");
    }

    [UnitTest]
    public async Task SessionRefresh_RemovedSubscriptionDoesNotRevokeRemainingSubscriptions()
    {
        using var manager = CreateManager();
        using var session = manager.TryCreateSession("WebSocket", null, addDefaultSubscription: false)!;
        var removed = CreateUnrestrictedSecurity();
        removed.EnableEventDrivenRefresh();
        var refreshed = false;
        removed.EnableRefresh(_ =>
        {
            refreshed = true;
            throw new InvalidOperationException("removed policy must not participate");
        });
        manager.TryAddSubscription(session.SessionId, "removed", new StreamSubscriptionFilter(subscriberSecurity: removed));
        manager.TryAddSubscription(session.SessionId, "active", new StreamSubscriptionFilter(subscriberSecurity: CreateUnrestrictedSecurity()));
        manager.TryRemoveSubscription(session.SessionId, "removed");

        (await manager.RefreshSessionSecurityAsync(session.SessionId, CancellationToken.None)).Should().BeTrue();

        refreshed.Should().BeFalse();
    }

    public enum RefreshPath
    {
        EventDriven,
        Periodic
    }

    public enum PendingOutcome
    {
        Denies,
        Fails
    }

    public enum AfterRemoval
    {
        Removed,
        Replaced
    }

    [UnitTheory]
    [InlineData(RefreshPath.Periodic, PendingOutcome.Denies, AfterRemoval.Removed)]
    [InlineData(RefreshPath.Periodic, PendingOutcome.Fails, AfterRemoval.Removed)]
    [InlineData(RefreshPath.Periodic, PendingOutcome.Denies, AfterRemoval.Replaced)]
    [InlineData(RefreshPath.Periodic, PendingOutcome.Fails, AfterRemoval.Replaced)]
    [InlineData(RefreshPath.EventDriven, PendingOutcome.Denies, AfterRemoval.Removed)]
    [InlineData(RefreshPath.EventDriven, PendingOutcome.Fails, AfterRemoval.Removed)]
    [InlineData(RefreshPath.EventDriven, PendingOutcome.Denies, AfterRemoval.Replaced)]
    [InlineData(RefreshPath.EventDriven, PendingOutcome.Fails, AfterRemoval.Replaced)]
    public async Task Refresh_SubscriptionRemovedDuringRefresh_DoesNotRevokeRemainingSession(
        RefreshPath path, PendingOutcome outcome, AfterRemoval removal)
    {
        using var manager = CreateManager();
        using var session = manager.TryCreateSession("WebSocket", null, addDefaultSubscription: false)!;
        var blocked = new BlockedRefresh(outcome);
        manager.TryAddSubscription(session.SessionId, "removed", new StreamSubscriptionFilter(subscriberSecurity: blocked.Security));
        manager.TryAddSubscription(session.SessionId, "active", new StreamSubscriptionFilter(subscriberSecurity: CreateUnrestrictedSecurity()));
        var refresh = path == RefreshPath.EventDriven
            ? manager.RefreshSessionSecurityAsync(session.SessionId, CancellationToken.None)
            : RefreshPeriodicAsync();
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        RemoveBlockedSubscription(manager, session.SessionId, removal);
        blocked.Resume.TrySetResult();

        (await refresh).Should().BeTrue();
        session.DisconnectToken.IsCancellationRequested.Should().BeFalse();
        manager.SessionCount.Should().Be(1);

        async Task<bool> RefreshPeriodicAsync()
        {
            await manager.RefreshSubscriberSecurityAsync();
            return true;
        }
    }

    [UnitTest]
    public async Task SessionRefresh_RemovedBlockedSubscriptionDoesNotConsumeRemainingDeadline()
    {
        using var manager = CreateManager();
        using var session = manager.TryCreateSession("WebSocket", null, addDefaultSubscription: false)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = CreateUnrestrictedSecurity();
        removed.EnableRefresh(async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return CreateUnrestrictedSecurity();
        });
        var activeRefreshed = false;
        var active = CreateUnrestrictedSecurity();
        active.EnableRefresh(_ =>
        {
            activeRefreshed = true;
            return Task.FromResult(CreateUnrestrictedSecurity());
        });
        manager.TryAddSubscription(session.SessionId, "removed", new StreamSubscriptionFilter(subscriberSecurity: removed));
        manager.TryAddSubscription(session.SessionId, "active", new StreamSubscriptionFilter(subscriberSecurity: active));
        var refresh = manager.RefreshSessionSecurityAsync(session.SessionId, deadline.Token);
        await started.Task.WaitAsync(deadline.Token);
        manager.TryRemoveSubscription(session.SessionId, "removed").Should().BeTrue();

        (await refresh.WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
        activeRefreshed.Should().BeTrue();
        session.DisconnectToken.IsCancellationRequested.Should().BeFalse();
    }

    [UnitTheory]
    [InlineData(PendingOutcome.Denies, AfterRemoval.Removed)]
    [InlineData(PendingOutcome.Denies, AfterRemoval.Replaced)]
    [InlineData(PendingOutcome.Fails, AfterRemoval.Removed)]
    [InlineData(PendingOutcome.Fails, AfterRemoval.Replaced)]
    public async Task Replay_RemovedPolicyRefreshCannotRevokeRemainingSubscription(PendingOutcome outcome, AfterRemoval removal)
    {
        using var manager = CreateManager();
        using var session = manager.TryCreateSession("WebSocket", null, addDefaultSubscription: false)!;
        var blocked = new BlockedRefresh(outcome);
        manager.TryAddSubscription(session.SessionId, "removed", new StreamSubscriptionFilter(subscriberSecurity: blocked.Security));
        manager.TryAddSubscription(session.SessionId, "active", new StreamSubscriptionFilter(subscriberSecurity: CreateUnrestrictedSecurity()));
        var replay = blocked.Security.RefreshForReplayAsync(CancellationToken.None);
        try
        {
            await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            RemoveBlockedSubscription(manager, session.SessionId, removal);
            blocked.Resume.TrySetResult();
            await replay.WaitAsync(TimeSpan.FromSeconds(2));
            session.DisconnectToken.IsCancellationRequested.Should().BeFalse();
            manager.SessionCount.Should().Be(1);
        }
        finally
        {
            blocked.Resume.TrySetResult();
        }
    }

    private static void RemoveBlockedSubscription(FeatureStreamSessionManager manager, Guid sessionId, AfterRemoval removal)
    {
        manager.TryRemoveSubscription(sessionId, "removed").Should().BeTrue();
        if (removal == AfterRemoval.Replaced)
        {
            manager.TryAddSubscription(sessionId, "removed", new StreamSubscriptionFilter(subscriberSecurity: CreateUnrestrictedSecurity()));
        }
    }

    // A subscription whose policy refresh waits until released, then denies its route or fails.
    private sealed class BlockedRefresh
    {
        public BlockedRefresh(PendingOutcome outcome)
        {
            Security = CreateUnrestrictedSecurity();
            Security.EnableRefresh(async cancellationToken =>
            {
                Started.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
                if (outcome == PendingOutcome.Fails)
                {
                    throw new InvalidOperationException("policy store unavailable");
                }
                return new StreamSubscriberSecurity(new HashSet<string>(["svc"], StringComparer.Ordinal),
                    new Dictionary<(string, int), StreamLayerReadPolicy>(),
                    new Dictionary<(string, int), StreamLayerReadPolicy>(),
                    new HashSet<(string, int)> { ("svc", 0) });
            });
        }

        public StreamSubscriberSecurity Security { get; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
