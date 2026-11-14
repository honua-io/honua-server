// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Geoprocessing.Tests;

public sealed class StagedImportDiscardExecutorTests
{
    [UnitTest]
    public async Task Discard_SendsTheServerDerivedTableForTheRequesterScope()
    {
        var (executor, ledger) = Create(StagedImportDiscardOutcome.Discarded);

        var handle = await executor.SubmitAsync(Request(), Context(), CancellationToken.None);

        handle.Result!.Details["outcome"].Should().Be("discarded");
        ledger.Requests.Should().Equal(new StagedImportDiscardRequest("tenant-a", "actor-1", "intent-1", "honua_data",
            StagedImportOperation.DestinationTable("tenant-a", "actor-1", "intent-1")));
    }

    [UnitTest]
    public async Task Discard_NothingLeft_ReportsNotFoundIdempotently()
    {
        var (executor, _) = Create(StagedImportDiscardOutcome.NotFound);

        var handle = await executor.SubmitAsync(Request(), Context(), CancellationToken.None);

        handle.Status.Should().Be(OperationHandleStatus.Completed);
        handle.Result!.Details["outcome"].Should().Be("not-found");
    }

    [UnitTheory]
    [InlineData(nameof(StagedImportDiscardOutcome.Referenced))]
    [InlineData(nameof(StagedImportDiscardOutcome.NotOwned))]
    public async Task Discard_ProtectedTable_IsRefused(string outcome)
    {
        var (executor, _) = Create(Enum.Parse<StagedImportDiscardOutcome>(outcome));

        var act = () => executor.SubmitAsync(Request(), Context(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
    }

    [UnitTest]
    public async Task Discard_WithoutImportAuthority_IsRefusedBeforeTheLedger()
    {
        var (executor, ledger) = Create(StagedImportDiscardOutcome.Discarded, authorized: false);

        var act = () => executor.SubmitAsync(Request(), Context(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        ledger.Requests.Should().BeEmpty();
    }

    [UnitTest]
    public async Task Discard_WithTheProcessLocalLedger_IsRefusedAsAPrecondition()
    {
        var (executor, _) = Create(StagedImportDiscardOutcome.Discarded, ledger: new MemoryServerJobArtifactImportLedger());

        var act = () => executor.SubmitAsync(Request(), Context(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>().WithMessage("*durable import ledger*");
    }

    [UnitTest]
    public async Task Discard_SchemaThatIsNotAnIdentifier_IsInvalidBeforeApproval()
    {
        var (executor, _) = Create(StagedImportDiscardOutcome.Discarded);
        var request = Request() with
        {
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["intentId"] = "intent-1", ["schema"] = "x'; drop" },
        };

        (await executor.ValidateAsync(request, CancellationToken.None)).IsValid.Should().BeFalse();
        var map = () => new StagedImportDiscardApprovalMapper().Map(StagedImportOperation.BuildDescriptors()
            .Single(d => d.OperationId == StagedImportOperation.DiscardOperationId), request, Context(),
            new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval });
        map.Should().Throw<GeoprocessingValidationException>();
    }

    [UnitTest]
    public void DiscardApproval_ReplayReadsOnlyTheSealedPlanPayload()
    {
        var mapper = new StagedImportDiscardApprovalMapper();
        var gateway = mapper.Map(StagedImportOperation.BuildDescriptors()
            .Single(d => d.OperationId == StagedImportOperation.DiscardOperationId), Request(), Context(),
            new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval });
        var tampered = gateway with { ExecutionPayload = """{"parameters":{"intentId":"other","schema":"public"},"tenantId":"tenant-z"}""" };

        var replay = mapper.MapReplay(tampered);

        replay.Request.Parameters["intentId"].Should().Be("intent-1");
        replay.TenantId.Should().Be("tenant-a");
        gateway.Plan!.Summary.Should().Contain("honua_data.imported_staged_");
    }

    [UnitTest]
    public void DiscardDescriptor_IsGatedAndDestroysOnlyState()
    {
        var discard = StagedImportOperation.BuildDescriptors()
            .Single(descriptor => descriptor.OperationId == StagedImportOperation.DiscardOperationId);

        discard.ApprovalModel.Should().Be(OperationApprovalModel.OperatorGate);
        discard.Policy.SideEffectClass.Should().Be(OperationSideEffectClass.DestroysState);
        discard.InputSchema.Select(parameter => parameter.Name).Should().Equal("intentId", "schema");
    }

    private static (StagedImportDiscardExecutor Executor, RecordingDiscardLedger Ledger) Create(
        StagedImportDiscardOutcome outcome, bool authorized = true, IServerJobArtifactImportLedger? ledger = null)
    {
        var recording = new RecordingDiscardLedger(outcome);
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(authorized ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "actor-1")], "Test")),
        };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(http);
        var services = new ServiceCollection()
            .AddSingleton(ledger ?? recording)
            .AddSingleton(accessor)
            .AddSingleton(authorization)
            .BuildServiceProvider();
        http.RequestServices = services;
        return (new StagedImportDiscardExecutor(services), recording);
    }

    private static OperationRequest Request() => new()
    {
        OperationId = StagedImportOperation.DiscardOperationId,
        Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["intentId"] = "intent-1",
            ["schema"] = "honua_data",
        },
    };

    private static OperationPolicyContext Context() => new() { TenantId = "tenant-a", PrincipalId = "actor-1" };

    // The ledger interface is internal to Honua.Core, so the test records calls with a small fake.
    private sealed class RecordingDiscardLedger(StagedImportDiscardOutcome outcome) : IServerJobArtifactImportLedger
    {
        public List<StagedImportDiscardRequest> Requests { get; } = [];

        public Task<ServerJobArtifactImportReceipt> GetOrImportAsync(ServerJobArtifactImportScope scope,
            Func<IServerJobArtifactImportAttempt, CancellationToken, Task<ServerJobArtifactImportReceipt>> import,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("Discard tests never import.");

        public Task<StagedImportDiscardOutcome> DiscardStagedAsync(
            StagedImportDiscardRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(outcome);
        }
    }
}
