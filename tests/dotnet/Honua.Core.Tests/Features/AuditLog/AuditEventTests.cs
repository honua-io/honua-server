// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Tests.Features.AuditLog;

/// <summary>
/// Sanity checks on the <see cref="AuditEvent"/> shape and the
/// <see cref="NullAuditLog"/> fallback (#1144).
/// </summary>
public sealed class AuditEventTests
{
    [Fact]
    public void AuditEvent_RoundTrips_AllFields()
    {
        var timestamp = new DateTimeOffset(2026, 5, 20, 12, 34, 56, TimeSpan.Zero);
        var evt = new AuditEvent
        {
            Timestamp = timestamp,
            EventType = AuditEventType.Authentication,
            Actor = "user-123",
            ActorType = AuditActorType.UserId,
            ResourceType = "http",
            ResourceId = "/api/v1/admin/things",
            Action = "auth.success",
            Outcome = AuditOutcome.Success,
            CorrelationId = "corr-abc",
            RemoteIp = "10.0.0.1",
            UserAgent = "honua-cli/1.0",
            Details = "{\"scheme\":\"api-key\"}",
        };

        evt.Timestamp.Should().Be(timestamp);
        evt.EventType.Should().Be(AuditEventType.Authentication);
        evt.Actor.Should().Be("user-123");
        evt.ActorType.Should().Be(AuditActorType.UserId);
        evt.ResourceType.Should().Be("http");
        evt.ResourceId.Should().Be("/api/v1/admin/things");
        evt.Action.Should().Be("auth.success");
        evt.Outcome.Should().Be(AuditOutcome.Success);
        evt.CorrelationId.Should().Be("corr-abc");
        evt.RemoteIp.Should().Be("10.0.0.1");
        evt.UserAgent.Should().Be("honua-cli/1.0");
        evt.Details.Should().Be("{\"scheme\":\"api-key\"}");
    }

    [Fact]
    public void AuditEvent_OptionalFields_DefaultSensibly()
    {
        var evt = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.AdminAction,
            Actor = AuditEvent.AnonymousActor,
            ActorType = AuditActorType.Anonymous,
            ResourceType = "layer",
            Action = "layer.delete",
            Outcome = AuditOutcome.Denied,
            CorrelationId = "x",
        };

        evt.ResourceId.Should().BeNull();
        evt.RemoteIp.Should().BeNull();
        evt.UserAgent.Should().BeNull();
        // Details defaults to empty string so the storage column can be NOT NULL.
        evt.Details.Should().Be(string.Empty);
    }

    [Fact]
    public void AnonymousActor_Sentinel_IsStable()
    {
        // The sentinel value is part of the contract — downstream forensic
        // queries can filter on it directly, so it must not silently change.
        AuditEvent.AnonymousActor.Should().Be("anonymous");
    }

    [Fact]
    public void AuditEventType_CoversAllRequiredCategories()
    {
        var values = Enum.GetValues<AuditEventType>();
        values.Should().Contain(new[]
        {
            AuditEventType.Authentication,
            AuditEventType.Authorization,
            AuditEventType.AdminAction,
            AuditEventType.ConfigChange,
            AuditEventType.DataExport,
            AuditEventType.DataDelete,
        });
    }

    [Fact]
    public void AuditOutcome_CoversAllRequiredOutcomes()
    {
        var values = Enum.GetValues<AuditOutcome>();
        values.Should().Contain(new[]
        {
            AuditOutcome.Success,
            AuditOutcome.Failure,
            AuditOutcome.Denied,
        });
    }

    [Fact]
    public void AuditActorType_CoversAllRequiredTypes()
    {
        var values = Enum.GetValues<AuditActorType>();
        values.Should().Contain(new[]
        {
            AuditActorType.Anonymous,
            AuditActorType.UserId,
            AuditActorType.ApiKey,
            AuditActorType.System,
        });
    }
}

/// <summary>
/// The <see cref="NullAuditLog"/> fallback must accept any well-formed event
/// and never throw — call sites rely on it for tests / non-database hosts.
/// </summary>
public sealed class NullAuditLogTests
{
    [Fact]
    public async Task RecordAsync_ReturnsCompleted_AndDoesNotThrow()
    {
        var evt = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.Authentication,
            Actor = AuditEvent.AnonymousActor,
            ActorType = AuditActorType.Anonymous,
            ResourceType = "http",
            Action = "auth.failure",
            Outcome = AuditOutcome.Failure,
            CorrelationId = "any",
        };

        var act = async () => await NullAuditLog.Instance.RecordAsync(evt);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RecordAsync_NullEvent_Throws()
    {
        var act = async () => await NullAuditLog.Instance.RecordAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Instance_IsSingleton()
    {
        NullAuditLog.Instance.Should().BeSameAs(NullAuditLog.Instance);
    }
}
