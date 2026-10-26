// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using NSubstitute;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class OperationProposerApiKeyAuthorityTests
{
    private static readonly Guid KeyId = Guid.Parse("99424c41-981a-44f2-90b6-b864f0e60957");
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static string Actor => $"apikey:api-key:{KeyId:D}";

    [UnitTheory]
    [InlineData("apikey", true)]
    [InlineData("ApiKey:api-key:not-a-guid", true)]
    [InlineData("apikey:subject:issuer:subject", true)]
    [InlineData("oidc:subject:issuer:subject", false)]
    [InlineData("someone:api-key:99424c41-981a-44f2-90b6-b864f0e60957", false)]
    [InlineData(null, false)]
    public void IsApiKeyActor_RecognizesOnlyFrameworkScheme(string? actor, bool expected)
        => OperationProposerApiKeyAuthority.IsApiKeyActor(actor).Should().Be(expected);

    [UnitTheory]
    [InlineData("apikey")]
    [InlineData("apikey:subject:issuer:subject")]
    [InlineData("apikey:api-key:not-a-guid")]
    [InlineData("apikey:api-key:00000000-0000-0000-0000-000000000000")]
    [InlineData("apikey:api-key:99424c41981a44f290b6b864f0e60957")]
    [InlineData("apikey:api-key:99424c41-981a-44f2-90b6-b864f0e60957:extra")]
    public async Task ValidateAsync_MalformedCanonicalIdentity_DeniesBeforeLookup(string actor)
    {
        var store = Substitute.For<IAdminApiKeyStore>();
        var helper = new OperationProposerApiKeyAuthority(new Clock(), store);
        var act = () => helper.ValidateAsync(actor, CancellationToken.None);
        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Be(OperationProposerApiKeyAuthority.Refusal);
        failure.Which.InnerException.Should().BeNull();
        await store.DidNotReceiveWithAnyArgs().GetAsync(default, default);
    }

    [UnitTheory]
    [InlineData("missing")]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("read-only")]
    [InlineData("wrong-id")]
    public async Task ValidateAsync_CurrentRecordHasNoAuthority_Denies(string state)
    {
        var store = Substitute.For<IAdminApiKeyStore>();
        AdminApiKeyRecord? record = state switch
        {
            "missing" => null,
            "revoked" => Record() with { RevokedAt = Now },
            "expired" => Record() with { ExpiresAt = Now },
            "read-only" => Record() with { Permissions = ["admin:read"] },
            "wrong-id" => Record() with { Id = Guid.NewGuid() },
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
        store.GetAsync(KeyId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(record));
        var act = () => new OperationProposerApiKeyAuthority(new Clock(), store).ValidateAsync(Actor, CancellationToken.None);
        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Be(OperationProposerApiKeyAuthority.Refusal);
        failure.Which.InnerException.Should().BeNull();
    }

    [UnitTest]
    public async Task ValidateAsync_CurrentWritableRecord_AllowsExactId()
    {
        var store = Substitute.For<IAdminApiKeyStore>();
        store.GetAsync(KeyId, Arg.Any<CancellationToken>()).Returns(Task.FromResult<AdminApiKeyRecord?>(Record()));
        await new OperationProposerApiKeyAuthority(new Clock(), store).ValidateAsync(Actor, CancellationToken.None);
        await store.Received(1).GetAsync(KeyId, CancellationToken.None);
    }

    [UnitTest]
    public async Task ValidateAsync_MissingStoreOrLookupFailure_HasNoRawInnerException()
    {
        var absent = () => new OperationProposerApiKeyAuthority(new Clock()).ValidateAsync(Actor, CancellationToken.None);
        (await absent.Should().ThrowAsync<InvalidOperationException>()).Which.InnerException.Should().BeNull();
        var store = Substitute.For<IAdminApiKeyStore>();
        store.GetAsync(KeyId, Arg.Any<CancellationToken>()).Returns<Task<AdminApiKeyRecord?>>(_ =>
            throw new InvalidOperationException("synthetic-private-store-canary"));
        var failed = () => new OperationProposerApiKeyAuthority(new Clock(), store).ValidateAsync(Actor, CancellationToken.None);
        var error = (await failed.Should().ThrowAsync<InvalidOperationException>()).Which;
        error.Message.Should().Be(OperationProposerApiKeyAuthority.Refusal);
        error.InnerException.Should().BeNull();
        error.ToString().Should().NotContain("synthetic-private-store-canary");
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidateAsync_CallerCancellation_IsPreservedEvenWhenStoreReturns(bool storeThrows)
    {
        using var cancellation = new CancellationTokenSource();
        var store = Substitute.For<IAdminApiKeyStore>();
        store.GetAsync(KeyId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancellation.Cancel();
            return storeThrows ? Task.FromCanceled<AdminApiKeyRecord?>(cancellation.Token)
                : Task.FromResult<AdminApiKeyRecord?>(Record());
        });
        var act = () => new OperationProposerApiKeyAuthority(new Clock(), store).ValidateAsync(Actor, cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [UnitTheory]
    [InlineData("apikey:api-key:99424c41-981a-44f2-90b6-b864f0e60957")]
    [InlineData("apikey:subject:malformed")]
    public async Task Gateway_MissingAuthorityHelper_DeniesBeforeClaimOrScope(string actor)
    {
        var store = Substitute.For<IOperationProposalStore>();
        var proposal = new OperationProposal
        {
            ProposalId = "synthetic",
            Kind = OperationClass.ServicePublish,
            Status = OperationProposalStatus.AwaitingApproval,
            RequestedBy = actor,
            CreatedAt = Now,
            UpdatedAt = Now,
            Plan = new OperationProposalPlan { Summary = "synthetic" },
            Audit = new OperationAuditInfo { OperationInstanceId = "synthetic-instance", CorrelationId = "synthetic-correlation", Reason = "test" }
        };
        store.GetAsync("synthetic", Arg.Any<CancellationToken>()).Returns(Task.FromResult<OperationProposal?>(proposal));
        var scopes = Substitute.For<IServiceScopeFactory>();
        var gateway = new OperationGateway(Substitute.For<IGuardrailLadder>(), store, [], scopes,
            Substitute.For<IProposalNotifier>(), NullLogger<OperationGateway>.Instance);
        var act = () => gateway.ApplyApprovedProposalAsync("synthetic", "distinct-approver");
        var error = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        error.Message.Should().Be(OperationProposerApiKeyAuthority.Refusal);
        error.InnerException.Should().BeNull();
        scopes.DidNotReceive().CreateScope();
        store.ReceivedCalls().Should().OnlyContain(call => call.GetMethodInfo().Name == nameof(IOperationProposalStore.GetAsync));
    }

    private static AdminApiKeyRecord Record() => new(KeyId, "synthetic", "fake-prefix", [], ["admin:*"],
        Now.AddHours(-1), Now.AddHours(-1), Now.AddHours(1), null, null, null, "test");
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
