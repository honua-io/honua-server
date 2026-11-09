// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Server.Features.Operations;
using Honua.Server.Tests.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using LegacyExecutor = Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ApprovalManagement)]
public sealed class PostgresProposalApprovalRestartTests : IAsyncLifetime
{
    private readonly AdminAuthEndpointsTests.StubHttpClientFactory _identityProvider = AdminAuthEndpointsTests.CreateStubFactory();
    private readonly LegacyExecutor _actuator = Substitute.For<LegacyExecutor>();
    private readonly WebAppFixture _fixture;

    public PostgresProposalApprovalRestartTests()
    {
        _actuator.OperationClass.Returns(OperationClass.AdminConfigChange);
        _actuator.PlanAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OperationProposalPlan?>(new OperationProposalPlan { Summary = "Change a setting" }));
        _actuator.ExecuteAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("postgres-approved-operation"));
        _fixture = HumanApprovalHttpClient.Configure(new WebAppFixture(), _identityProvider)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "postgres-proposal-test-key");
                builder.UseSetting("ConnectionStrings:redis", "");
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");
                builder.UseSetting("Operations:Policy:Enabled", "true");
                builder.UseSetting("Operations:Policy:DefaultDecision", "Deny");
                builder.UseSetting("Operations:Policy:Rules:0:OperationId", "*");
                builder.UseSetting("Operations:Policy:Rules:0:Decision", "RequireApproval");
            })
            .ConfigureServices(ConfigureActuator);
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public async Task DisposeAsync()
    {
        try { await _fixture.DisposeAsync(); }
        finally { _identityProvider.Dispose(); }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/proposals/{id}/approval-review")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task ApproveProposal_WithoutRedisAfterHostRestart_PreservesEvidenceAndExecutesOnce()
    {
        Assert.True(_fixture.GetService<IAuditLog>().IsPersisted);
        var gateway = _fixture.GetService<IOperationGateway>();
        var routed = await gateway.RouteAsync(new OperationGatewayRequest
        {
            TenantId = "public",
            Kind = OperationClass.AdminConfigChange,
            RequestedBy = "agent:postgres-requester",
            Plan = new OperationProposalPlan { Summary = "Change a setting", RiskLevel = ProposalRiskLevel.Medium }
        });
        var id = Assert.IsType<string>(routed.ProposalId);
        var pending = Assert.IsType<OperationProposal>(await Proposals.GetAsync(id));
        Assert.Equal(OperationProposalStatus.AwaitingApproval, pending.Status);
        Assert.NotNull(pending.Initiation);
        Assert.NotNull(pending.Audit.OperationInstanceId);

        await _fixture.RestartHostAsync();

        var restored = Assert.IsType<OperationProposal>(await Proposals.GetAsync(id));
        restored.Should().BeEquivalentTo(pending);
        using var human = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identityProvider, "postgres-human", "public");
        using var response = await human.ApproveAsync(id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var approved = Assert.IsType<OperationProposal>(await Proposals.GetAsync(id));
        Assert.NotNull(approved.HumanApproval);
        Assert.Equal(restored.Audit.OperationInstanceId, approved.Audit.OperationInstanceId);
        Assert.Equal(restored.SealedPlanHash, approved.SealedPlanHash);
        await _actuator.Received(1).ExecuteAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        await _fixture.RestartHostAsync();

        var retained = Assert.IsType<OperationProposal>(await Proposals.GetAsync(id));
        retained.Should().BeEquivalentTo(approved);
        var instance = await _fixture.GetService<IOperationInstanceStore>().GetAsync(retained.Audit.OperationInstanceId!);
        Assert.NotNull(instance);
        Assert.Equal(retained.ProposalId, instance.ProposalId);
        Assert.Equal(retained.Audit.CorrelationId, instance.CorrelationId);
        await _actuator.Received(1).ExecuteAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private IOperationProposalStore Proposals => _fixture.GetService<IOperationProposalStore>();

    private void ConfigureActuator(IServiceCollection services)
    {
        var ladder = Substitute.For<IGuardrailLadder>();
        var decision = new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.AdminConfigChange, HonuaEdition.Pro, "postgres-approval-test");
        ladder.Resolve(default).ReturnsForAnyArgs(decision);
        ladder.Resolve(default, (string?)null).ReturnsForAnyArgs(decision);
        ladder.Resolve(default, HonuaEdition.Pro).ReturnsForAnyArgs(decision);
        ladder.Resolve(default, (string?)null, HonuaEdition.Pro).ReturnsForAnyArgs(decision);
        services.RemoveAll<IGuardrailLadder>();
        services.AddSingleton(ladder);
        services.RemoveAll<LegacyExecutor>();
        services.AddSingleton(_actuator);
        var catalog = Substitute.For<IOperationExecutorCatalog>();
        catalog.SupportedKinds.Returns(new[] { OperationClass.AdminConfigChange });
        services.RemoveAll<IOperationExecutorCatalog>();
        services.AddSingleton(catalog);
        services.RemoveAll<IOperationEnvelopeFactory>();
        services.AddSingleton<IOperationEnvelopeFactory>(provider =>
            new ScopedOperationEnvelopeFactory(provider.GetRequiredService<IServiceScopeFactory>(), useVolatileAudit: false));
    }
}
