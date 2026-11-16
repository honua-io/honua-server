// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Db.Postgres.Features.AuditLog;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.AuditLog;

public sealed partial class PostgresAuditLogChainTests
{
    private const string AgentActor = "ApiKey:api-key:canonical-agent";

    [IntegrationTest]
    public async Task AgentActions_ExecuteAndDeny_RetainIdentityInVerifiedAuditChain()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(AgentActions_ExecuteAndDeny_RetainIdentityInVerifiedAuditChain));
        try
        {
            await EnsureAuditLogTableAsync(schema);
            const string actor = AgentActor;
            var context = new OperationPolicyContext { PrincipalId = actor, Roles = ["agent"] };
            var (dispatcher, executor, descriptor) = AgentDispatcher(schema);
            var request = new OperationRequest { OperationId = descriptor.OperationId };

            var executed = await dispatcher.SubmitAsync(request, context);
            var denied = await dispatcher.SubmitAsync(request, context);
            var scopeDenied = await dispatcher.SubmitAsync(request, context with { ScopeGoverned = true });

            executed.Status.Should().Be(OperationHandleStatus.Completed);
            denied.Status.Should().Be(OperationHandleStatus.Denied);
            scopeDenied.Status.Should().Be(OperationHandleStatus.Denied);
            await executor.Received(1).SubmitAsync(Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>());
            await AssertAgentChainAsync(schema, actor, [executed, denied, scopeDenied]);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task AgentIdentity_MixedLegacyChain_BindsStoredIdentityAndFiltersExports()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(AgentIdentity_MixedLegacyChain_BindsStoredIdentityAndFiltersExports));
        try
        {
            await EnsureAuditLogTableAsync(schema);
            await KeyedSink(schema).RecordAsync(Event("legacy", "auth.success"));
            var agentEvent = Event("agent", "auth.success") with
            {
                ActorAgent = "agent-1",
                ActorType = AuditActorType.ApiKey,
            };
            var receipt = await KeyedSink(schema).RecordAsync(agentEvent);
            receipt.Should().NotBeNull();
            await KeyedSink(schema).RecordAsync(agentEvent with { ActorAgent = "agent-2", CorrelationId = "other" });

            var page = await new PostgresAuditLogReader(Provider(schema), schema)
                .ListAsync(new AuditLogFilter { ActorAgent = "agent-1" });
            page.Items.Should().ContainSingle().Which.ActorType.Should().Be(AuditActorType.ApiKey);
            var exported = await CollectAsync(new PostgresAuditLogExporter(Provider(schema), schema)
                .ExportAsync(new AuditExportFilter { ActorAgent = "agent-1" }));
            exported.Should().ContainSingle().Which.ActorAgent.Should().Be("agent-1");
            var verified = await KeyedVerifier(schema).VerifyAsync();
            verified.Verified.Should().BeTrue();
            verified.RowsChecked.Should().Be(3);

            await ExecuteAsync(schema, $"""
                DROP RULE audit_log_no_update ON "{schema}".audit_log;
                UPDATE "{schema}".audit_log SET actor_agent = 'forged' WHERE actor_agent = 'agent-1';
                CREATE RULE audit_log_no_update AS ON UPDATE TO "{schema}".audit_log DO INSTEAD NOTHING;
                """);
            var tampered = await KeyedVerifier(schema).VerifyAsync();
            tampered.Verified.Should().BeFalse();
            tampered.FirstBrokenAuditId.Should().Be(long.Parse(receipt!, System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private (OperationDispatcher, IOperationExecutor, OperationDescriptor) AgentDispatcher(string schema)
    {
        var catalog = Substitute.For<IOperationCatalog>();
        var descriptor = AgentActionDescriptor();
        catalog.GetDescriptorAsync(descriptor.OperationId, Arg.Any<CancellationToken>()).Returns(descriptor);
        var executor = Substitute.For<IOperationExecutor>();
        executor.OperationId.Returns(descriptor.OperationId);
        executor.ValidateAsync(Arg.Any<OperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OperationValidation { IsValid = true, Status = "valid" });
        executor.SubmitAsync(Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
            .Returns(call => CompletedAgentAction(call.Arg<OperationPolicyContext>(), descriptor.OperationId));
        var policy = Substitute.For<IOperationPolicyDecisionPoint>();
        policy.EvaluateAsync(descriptor, Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Allowed, new PolicyDecision { Kind = PolicyDecisionKind.Deny, Reason = "Denied by policy." });
        // The agent holds a ceiling that covers this action; without one, an agent's change becomes a proposal.
        var ceilings = Substitute.For<IAgentAuthorityStore>();
        ceilings.GetAsync(null, AgentActor, Arg.Any<CancellationToken>()).Returns(new AgentAuthorityCeiling
        {
            PrincipalId = AgentActor,
            AllowedOperationIds = [descriptor.OperationId],
            MaximumBlastRadius = descriptor.Policy.BlastRadiusClass,
            MaximumSideEffect = descriptor.Policy.SideEffectClass,
        });
        var dispatcher = new OperationDispatcher(catalog, [executor], policy, TimeProvider.System, auditLog: KeyedSink(schema),
            guards: new OperationActuationGuards { AgentAuthorityStore = ceilings });
        return (dispatcher, executor, descriptor);
    }

    private async Task AssertAgentChainAsync(string schema, string actor, IReadOnlyList<OperationHandle> handles)
    {
        var reader = new PostgresAuditLogReader(Provider(schema), schema);
        var page = await reader.ListAsync(new AuditLogFilter { Actor = actor, ActorAgent = actor, ActorTypes = [AuditActorType.UserId] });
        page.Items.Should().HaveCount(5);
        page.Items.Should().OnlyContain(row => row.Actor == actor && row.ActorType == AuditActorType.UserId && row.ActorAgent == actor);
        page.Items.Where(row => row.Action == "operation.accepted").Select(row => row.ResourceId)
            .Should().BeEquivalentTo(new[] { handles[0].OperationInstanceId, handles[1].OperationInstanceId });
        page.Items.Should().ContainSingle(row => row.Action == "operation.completed" && row.ResourceId == handles[0].OperationInstanceId);
        page.Items.Should().ContainSingle(row => row.Action == "operation.denied" && row.ResourceId == handles[2].OperationInstanceId);
        page.Items.Should().ContainSingle(row => row.Action == "operation.denied" && row.ResourceId == handles[1].OperationInstanceId
            && row.Outcome == AuditOutcome.Denied);
        var exported = await CollectAsync(new PostgresAuditLogExporter(Provider(schema), schema).ExportAsync(new AuditExportFilter { ActorAgent = actor }));
        exported.Should().HaveCount(page.Items.Count).And.OnlyContain(row => row.Actor == actor && row.ActorType == AuditActorType.UserId && row.ActorAgent == actor);
        var report = await KeyedVerifier(schema).VerifyAsync();
        report.Verified.Should().BeTrue();
        report.RowsChecked.Should().Be(page.Items.Count);
        report.UnhashedRows.Should().Be(0);
    }

    private static OperationDescriptor AgentActionDescriptor() => new()
    {
        OperationId = "test.agent-action",
        ProviderId = "test",
        Title = "Agent action",
        Description = "Test canonical agent audit identity",
        Category = "Test",
        ExecutionKind = OperationExecutionKind.Synchronous,
        ApprovalModel = OperationApprovalModel.None,
        Policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.ServiceScope,
            SideEffectClass = OperationSideEffectClass.CreatesMetadata,
            Determinism = OperationDeterminism.Deterministic,
            SupportsDryRun = false,
        },
    };

    private static OperationHandle CompletedAgentAction(OperationPolicyContext context, string operationId) => new()
    {
        OperationInstanceId = context.OperationInstanceId!,
        OperationId = operationId,
        CorrelationId = context.CorrelationId!,
        Status = OperationHandleStatus.Completed,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
