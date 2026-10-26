// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Exceptions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class OperationApprovalDenialRecorderTests
{
    private const string Canary = "FAKE_AUDIT_SINK_SECRET_CANARY";

    [UnitTheory]
    [InlineData(0, "self_approval")]
    [InlineData(1, "evidence_invalid")]
    public async Task RecordAsync_PersistedDecision_JoinsOnlyAllowlistedDetails(
        int reason, string code)
    {
        var sinks = new List<TestAudit>();
        await using var services = new ServiceCollection().AddScoped<IAuditLog>(_ =>
        {
            var sink = new TestAudit();
            sinks.Add(sink);
            return sink;
        }).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var proposal = Proposal() with { Plan = new() { ExecutionPayload = Canary } };
        var factory = services.GetRequiredService<IServiceScopeFactory>();
        await OperationApprovalDenialRecorder.RecordAsync(factory, proposal, "decider", (OperationApprovalDenialReason)reason, default);
        await OperationApprovalDenialRecorder.RecordAsync(factory, proposal, "decider", (OperationApprovalDenialReason)reason, default);
        sinks.Should().HaveCount(2);
        sinks.Should().OnlyContain(sink => sink.Disposed);
        var audit = sinks[0].Event!;
        audit.ResourceId.Should().Be(proposal.ProposalId);
        audit.CorrelationId.Should().Be("correlation");
        audit.Actor.Should().Be("decider");
        audit.Action.Should().Be("operation.approval_denied");
        audit.Outcome.Should().Be(AuditOutcome.Denied);
        JsonSerializer.Serialize(audit).Should().NotContain(Canary);
        using var details = JsonDocument.Parse(audit.Details!);
        details.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("kind", "reason");
        details.RootElement.GetProperty("reason").GetString().Should().Be(code);
    }

    [UnitTheory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("throw")]
    [InlineData("uncancelled-cancel")]
    [InlineData("nonpersisted")]
    [InlineData("missing")]
    public async Task RecordAsync_UnavailableSink_ThrowsSafeTypedException(string mode)
    {
        var services = new ServiceCollection();
        if (mode != "missing") services.AddScoped<IAuditLog>(_ => new TestAudit(mode));
        await using var provider = services.BuildServiceProvider();
        var act = () => OperationApprovalDenialRecorder.RecordAsync(
            provider.GetRequiredService<IServiceScopeFactory>(), Proposal(), "decider",
            OperationApprovalDenialReason.SelfApproval, default);
        var error = (await act.Should().ThrowAsync<ServiceUnavailableException>()).Which;
        error.Message.Should().Be(OperationApprovalDenialRecorder.UnavailableMessage);
        error.InnerException.Should().BeNull();
        error.ToString().Should().NotContain(Canary);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordAsync_CancelledDuringWrite_PropagatesCancellation(bool returnsReceipt)
    {
        using var cancellation = new CancellationTokenSource();
        await using var provider = new ServiceCollection().AddScoped<IAuditLog>(_ =>
            new TestAudit(onWrite: token =>
            {
                cancellation.Cancel();
                if (!returnsReceipt) token.ThrowIfCancellationRequested();
            })).BuildServiceProvider();
        var act = () => OperationApprovalDenialRecorder.RecordAsync(
            provider.GetRequiredService<IServiceScopeFactory>(), Proposal(), "decider",
            OperationApprovalDenialReason.SelfApproval, cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [UnitTest]
    public async Task RecordAsync_NoCorrelation_UsesTrustedProposalId()
    {
        var sink = new TestAudit();
        await using var provider = new ServiceCollection().AddScoped<IAuditLog>(_ => sink).BuildServiceProvider();
        await OperationApprovalDenialRecorder.RecordAsync(
            provider.GetRequiredService<IServiceScopeFactory>(), Proposal() with { Audit = new() }, "decider",
            OperationApprovalDenialReason.SelfApproval, default);
        sink.Event!.CorrelationId.Should().Be("proposal");
    }

    private static OperationProposal Proposal() => new()
    {
        ProposalId = "proposal",
        Kind = OperationClass.Deploy,
        Status = OperationProposalStatus.AwaitingApproval,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Audit = new() { CorrelationId = "correlation" }
    };

    private sealed class TestAudit(string mode = "ok", Action<CancellationToken>? onWrite = null)
        : IAuditLog, IDisposable
    {
        public bool IsPersisted => mode != "nonpersisted";
        public bool Disposed { get; private set; }
        public AuditEvent? Event { get; private set; }
        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Event = auditEvent;
            onWrite?.Invoke(cancellationToken);
            if (mode == "throw") throw new InvalidOperationException(Canary);
            if (mode == "uncancelled-cancel") throw new OperationCanceledException(Canary);
            return Task.FromResult<string?>(mode switch { "null" => null, "empty" => " ", _ => "receipt" });
        }
        public void Dispose() => Disposed = true;
    }
}
