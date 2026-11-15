// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;

namespace Honua.Core.Tests.Features.AuditLog;

/// <summary>
/// Sanity checks on the <see cref="AuditEvent"/> shape and the
/// <see cref="NullAuditLog"/> fallback (#1144).
/// </summary>
public sealed class AuditEventTests
{
    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
    [Fact]
    public void AnonymousActor_Sentinel_IsStable()
    {
        // The sentinel value is part of the contract — downstream forensic
        // queries can filter on it directly, so it must not silently change.
        AuditEvent.AnonymousActor.Should().Be("anonymous");
    }

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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
            AuditActorType.Agent,
        });
    }

    private static string Hash(
        string? prev = null,
        string actor = "user-1",
        string action = "auth.success",
        string resourceType = "http",
        string? resourceId = "/api/v1/admin/x",
        string details = "{}",
        string? actorAgent = null)
        => AuditEntryHasher.ComputeEntryHash(
            prev,
            new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero),
            AuditEventType.Authentication,
            actor,
            AuditActorType.UserId,
            resourceType,
            resourceId,
            action,
            AuditOutcome.Success,
            "corr-1",
            "10.0.0.1",
            "agent/1.0",
            details,
            actorAgent);


    [Trait("Tier", "Fast")]
    [Fact]
    public void ComputeEntryHash_LegacyRow_PreservesPublishedCanonicalBytes()
    {
        Hash().Should().Be("187b3048b06dfe1416175f54fb55a31e8e871bcd2700b66da108d21f07e0a6af");
        AuditEntryHasher.ComputeEntryMac(
            Enumerable.Repeat((byte)0x21, 32).ToArray(), null,
            new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero),
            AuditEventType.Authentication, "user-1", AuditActorType.UserId,
            "http", "/api/v1/admin/x", "auth.success", AuditOutcome.Success,
            "corr-1", "10.0.0.1", "agent/1.0", "{}")
            .Should().Be("b5ce867ada1427e9e695be38a3c9ac02305c3558f897f17f6dc11e0376e35751");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void ComputeEntryHash_AgentIdentity_IsBoundAndDistinctFromLegacy()
    {
        Hash(actorAgent: "agent-1").Should().NotBe(Hash());
        Hash(actorAgent: "agent-1").Should().NotBe(Hash(actorAgent: "agent-2"));
        Hash(actorAgent: string.Empty).Should().NotBe(Hash());
    }

    private const string OperationId = "service.publish";
    private const string McpApiKeyActor = "ApiKey:api-key:3f1c2b4a-8d7e-4c55-9a10-6b2e7d9c0f11";

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(false, AuditActorType.UserId)]
    [InlineData(true, AuditActorType.UserId)]
    public async Task SubmitAsync_AuthenticatedCallCanceledBeforeActuation_AuditsTheCallerNotAnonymous(
        bool agent, AuditActorType expectedType)
    {
        using var requestCancellation = new CancellationTokenSource();
        var audit = new CapturingAuditLog();
        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new StubDescriptorProvider()], TimeProvider.System),
            [new CancelingDuringValidationExecutor(requestCancellation)],
            new AllowAllPolicyDecisionPoint(),
            TimeProvider.System,
            auditLog: audit);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.SubmitAsync(
            new OperationRequest { OperationId = OperationId },
            new OperationPolicyContext { PrincipalId = McpApiKeyActor, Roles = agent ? ["agent"] : [], AuthorizationOutcome = "authorized" },
            requestCancellation.Token));

        var accepted = Assert.Single(audit.Events, entry => entry.Action == "operation.accepted");
        var cancelled = Assert.Single(audit.Events, entry => entry.Action == "operation.cancelled");
        Assert.Equal(accepted.ResourceId, cancelled.ResourceId);
        foreach (var entry in new[] { accepted, cancelled })
        {
            Assert.Equal(McpApiKeyActor, entry.Actor);
            Assert.NotEqual(AuditEvent.AnonymousActor, entry.Actor);
            Assert.Equal(expectedType, entry.ActorType);
            Assert.Equal(agent ? McpApiKeyActor : null, entry.ActorAgent);
        }
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task SubmitAsync_AgentWithoutRequiredScope_AuditsDenialBeforeAcceptance()
    {
        var audit = new CapturingAuditLog();
        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new StubDescriptorProvider()], TimeProvider.System),
            [], new AllowAllPolicyDecisionPoint(), TimeProvider.System, auditLog: audit);
        var result = await dispatcher.SubmitAsync(
            new OperationRequest { OperationId = OperationId },
            new OperationPolicyContext { PrincipalId = McpApiKeyActor, Roles = ["agent"], ScopeGoverned = true });

        Assert.Equal(OperationHandleStatus.Denied, result.Status);
        var denied = Assert.Single(audit.Events);
        Assert.Equal("operation.denied", denied.Action);
        Assert.Equal(McpApiKeyActor, denied.Actor);
        Assert.Equal(AuditActorType.UserId, denied.ActorType);
        Assert.Equal(McpApiKeyActor, denied.ActorAgent);
        Assert.NotNull(result.AuditId);
    }

    private sealed class CapturingAuditLog : IAuditLog
    {
        private readonly List<AuditEvent> _events = [];

        public IReadOnlyList<AuditEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            lock (_events)
            {
                _events.Add(auditEvent);
            }

            return Task.FromResult<string?>($"audit-{Guid.NewGuid():N}");
        }
    }

    private sealed class StubDescriptorProvider : IOperationDescriptorProvider
    {
        public string ProviderId => "test";

        public Task<IReadOnlyList<IOperationDescriptor>> ListDescriptorsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IOperationDescriptor>>(
            [
                new OperationDescriptor
                {
                    OperationId = OperationId,
                    ProviderId = "test",
                    Title = "Publish",
                    Description = "Publish a layer",
                    Category = "Publishing",
                    ExecutionKind = OperationExecutionKind.Synchronous,
                    ApprovalModel = OperationApprovalModel.None,
                    Policy = new OperationPolicyMetadata
                    {
                        BlastRadiusClass = OperationBlastRadiusClass.ServiceScope,
                        SideEffectClass = OperationSideEffectClass.CreatesMetadata,
                        Determinism = OperationDeterminism.Deterministic,
                        SupportsDryRun = false,
                    },
                },
            ]);
    }

    private sealed class CancelingDuringValidationExecutor(CancellationTokenSource requestCancellation)
        : IOperationExecutor
    {
        public string OperationId => AuditEventTests.OperationId;

        public Task<OperationValidation> ValidateAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default)
        {
            requestCancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Actuator must not run after canceled validation.");

        public Task<OperationStatus> GetStatusAsync(
            OperationHandle handle,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

/// <summary>
/// The <see cref="NullAuditLog"/> fallback must accept any well-formed event
/// and never throw — call sites rely on it for tests / non-database hosts.
/// </summary>
public sealed class NullAuditLogTests
{
    [Trait("Tier", "Fast")]
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

        var auditId = await NullAuditLog.Instance.RecordAsync(evt);
        auditId.Should().BeNull("a no-op sink cannot claim a durable audit identity");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task RecordAsync_NullEvent_Throws()
    {
        var act = async () => await NullAuditLog.Instance.RecordAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Instance_IsSingleton()
    {
        NullAuditLog.Instance.Should().BeSameAs(NullAuditLog.Instance);
    }
}
