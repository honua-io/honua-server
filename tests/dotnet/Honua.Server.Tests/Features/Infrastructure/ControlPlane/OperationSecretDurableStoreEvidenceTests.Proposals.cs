// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Server.Features.Operations;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using IOperationExecutor = Honua.Core.Features.Operations.Abstractions.IOperationExecutor;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class OperationSecretDurableStoreEvidenceTests
{
    [IntegrationTheory]
    [InlineData("admin.api-key.create", false, false)]
    [InlineData("admin.api-key.revoke", false, false)]
    [InlineData("admin.api-key.create", true, false)]
    [InlineData("admin.api-key.create", false, true)]
    public async Task ProposeAsync_AllowPolicy_RequiresDistinctApprovalAndReplaysOriginalExactlyOnce(string operationId, bool loseProposalAudit, bool revokeProposer)
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var proposals = new RedisOperationProposalStore(multiplexer, NullLogger<RedisOperationProposalStore>.Instance);
        var instances = new RedisOperationInstanceStore(multiplexer);
        var secrets = BuildRedisSecretStore(multiplexer);
        var audit = new RecordingAuditLog { FailAction = loseProposalAudit ? "operation.proposed" : null };
        var definition = AdminAccessOperationCatalog.Definitions.Single(d => d.OperationId == operationId);
        var mapper = new AdminOperateOperationApprovalRequestMapper(definition, secrets);
        var ladder = new ApprovalGuardrailLadder();
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<IAuditLog>(audit);
        services.AddSingleton<IOperationInstanceStore>(instances);
        services.AddSingleton<IOperationProposalStore>(proposals);
        services.AddSingleton<IGuardrailLadder>(ladder);
        services.AddSingleton<IOperationApprovalRequestMapper>(mapper);
        var secret = "one-time-6662-" + Guid.NewGuid().ToString("N");
        using var handler = new ProposalAdminApi(secret);
        using var client = new HttpClient(handler);
        var credentialStore = new InMemoryAdminApiKeyStore(TimeProvider.System);
        var issued = await credentialStore.CreateAsync("proposer-6662", ["admin:*"], null, "fixture-operator", default);
        services.AddSingleton<IAdminApiKeyStore>(credentialStore);
        var executor = new ContradictoryProposalExecutor(BuildAccessExecutor(operationId, client, secrets, credentialStore));
        services.AddSingleton<IOperationGateway>(sp => new OperationGateway(ladder, proposals, [],
            sp.GetRequiredService<IServiceScopeFactory>(), new NullProposalNotifier(), NullLogger<OperationGateway>.Instance,
            proposerKeyAuthority: new OperationProposerApiKeyAuthority(TimeProvider.System, credentialStore)));
        services.AddSingleton<IOperationInvoker>(sp => new OperationDispatcher(
            new OperationCatalog([new AdminAccessOperationDescriptorProvider()], TimeProvider.System), [executor],
            new AllowPolicyDecisionPoint(), TimeProvider.System,
            new AdminOperationApprovalBridge(sp, [mapper], NullLogger<AdminOperationApprovalBridge>.Instance),
            instances, audit, new OperationApprovalReplayVerifier(proposals)));
        using var provider = services.BuildServiceProvider();
        var invoker = provider.GetRequiredService<IOperationInvoker>();
        var original = $"apikey:api-key:{issued.Record.Id:D}";
        var tenant = "tenant-6662-" + Guid.NewGuid().ToString("N");
        var request = new OperationRequest
        {
            OperationId = operationId,
            Parameters = operationId.EndsWith("create", StringComparison.Ordinal)
                ? new Dictionary<string, string?> { ["name"] = "synthetic-6662" }
                : new Dictionary<string, string?> { ["id"] = Guid.NewGuid().ToString() },
        };
        var context = new OperationPolicyContext
        {
            PrincipalId = original,
            TenantId = tenant,
            IdempotencyKey = "scoped-6662-" + Guid.NewGuid().ToString("N")
        };
        var pending = await ((IOperationProposalSubmitter)invoker).ProposeAsync(request, context);
        if (loseProposalAudit)
        {
            pending.Status.Should().Be(OperationHandleStatus.Failed); pending.ProposalId.Should().BeNull();
            handler.Sends.Should().Be(0);
            var failedProposalId = audit.Events.Single(e => e.Action == "operation.proposed").ResourceId;
            var failedProposal = await proposals.GetAsync(failedProposalId!);
            failedProposal!.Status.Should().Be(Honua.Core.Features.ControlPlane.Domain.OperationProposalStatus.Failed);
            var forbiddenReplay = () => provider.GetRequiredService<IOperationGateway>()
                .ApplyApprovedProposalAsync(failedProposalId!, "distinct-approver-6662");
            await forbiddenReplay.Should().ThrowAsync<InvalidOperationException>();
            handler.Sends.Should().Be(0);
            return;
        }
        pending.Status.Should().Be(OperationHandleStatus.RequiresApproval, "reason: {0}", pending.Reason);
        pending.ProposalId.Should().NotBeNullOrWhiteSpace(); pending.AuditId.Should().NotBeNullOrWhiteSpace();
        handler.Sends.Should().Be(0);
        var proposal = await proposals.GetAsync(pending.ProposalId!);
        proposal!.RequestedBy.Should().Be(original);
        proposal.Audit.OperationInstanceId.Should().Be(pending.OperationInstanceId);
        proposal.SealedPlanHash.Should().NotBeNullOrWhiteSpace();
        var retry = await ((IOperationProposalSubmitter)invoker).ProposeAsync(request, context);
        retry.ProposalId.Should().Be(pending.ProposalId); handler.Sends.Should().Be(0);
        var gateway = provider.GetRequiredService<IOperationGateway>();
        if (revokeProposer)
        {
            (await credentialStore.RevokeAsync(issued.Record.Id, default)).Should().NotBeNull();
            var refused = () => gateway.ApplyApprovedProposalAsync(pending.ProposalId!, "distinct-approver-6662");
            await refused.Should().ThrowAsync<InvalidOperationException>(); handler.Sends.Should().Be(0);
            (await proposals.GetAsync(pending.ProposalId!))!.Status.Should()
                .Be(Honua.Core.Features.ControlPlane.Domain.OperationProposalStatus.AwaitingApproval);
            var denial = audit.Events.Single(e => e.Action == "operation.approval_denied");
            denial.ResourceId.Should().Be(pending.ProposalId); denial.Actor.Should().Be("distinct-approver-6662");
            denial.Details.Should().Contain("current_authority");
            return;
        }
        var self = () => gateway.ApplyApprovedProposalAsync(pending.ProposalId!, original);
        await self.Should().ThrowAsync<InvalidOperationException>(); handler.Sends.Should().Be(0);
        var approved = await gateway.ApplyApprovedProposalAsync(pending.ProposalId!, "distinct-approver-6662");
        approved!.Status.Should().Be(Honua.Core.Features.ControlPlane.Domain.OperationProposalStatus.Succeeded);
        handler.Sends.Should().Be(1);
        handler.Path.Should().Be(operationId.EndsWith("create", StringComparison.Ordinal)
            ? "/api/v1/admin/api-keys" : "/api/v1/admin/api-keys/" + request.Parameters["id"] + "/revoke");
        if (operationId.EndsWith("create", StringComparison.Ordinal)) handler.Body.Should().Contain("synthetic-6662");
        var again = () => gateway.ApplyApprovedProposalAsync(pending.ProposalId!, "distinct-approver-6662");
        await again.Should().ThrowAsync<InvalidOperationException>(); handler.Sends.Should().Be(1);
        var completed = await instances.GetAsync(pending.OperationInstanceId);
        completed!.Status.Should().Be(OperationHandleStatus.Completed);
        completed.ProposalId.Should().Be(pending.ProposalId).And.NotBe("actuator-contradiction-6662");
        var completedRetry = await ((IOperationProposalSubmitter)invoker).ProposeAsync(request, context);
        completedRetry.Status.Should().Be(OperationHandleStatus.Completed);
        completedRetry.OperationInstanceId.Should().Be(pending.OperationInstanceId);
        completedRetry.ProposalId.Should().Be(pending.ProposalId); handler.Sends.Should().Be(1);
        if (operationId.EndsWith("create", StringComparison.Ordinal))
        {
            JsonSerializer.Serialize(completed).Should().NotContain(secret);
            var reference = completed.Result!.SecretReferences.Should().ContainSingle().Subject;
            secrets.Consume(reference, pending.OperationInstanceId, operationId, "distinct-approver-6662", tenant).Should().BeNull();
            secrets.Consume(reference, pending.OperationInstanceId, operationId, original, tenant).Should().Be(secret);
            secrets.Consume(reference, pending.OperationInstanceId, operationId, original, tenant).Should().BeNull();
        }
        JsonSerializer.Serialize(audit.Events).Should().NotContain(secret);
    }

    private sealed class ContradictoryProposalExecutor(AdminOperateOperationExecutor inner) : IOperationExecutor
    {
        public string OperationId => inner.OperationId;
        public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
            => inner.ValidateAsync(request, cancellationToken);
        public async Task<OperationHandle> SubmitAsync(OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default)
        {
            var result = await inner.SubmitAsync(request, context, cancellationToken).ConfigureAwait(false);
            return result with { ProposalId = "actuator-contradiction-6662" };
        }
        public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
            => inner.GetStatusAsync(handle, cancellationToken);
    }

    private sealed class ProposalAdminApi(string secret) : HttpMessageHandler
    {
        public int Sends { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++; var path = request.RequestUri!.AbsolutePath; Path = path;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var payload = "{\"data\":{\"key\":\"" + secret + "\"}}";
            if (path.EndsWith("/revoke", StringComparison.Ordinal))
            {
                var revoked = new AdminApiKeyResponse
                {
                    Id = Guid.Parse(path.Split('/')[^2]),
                    Name = "synthetic-6662",
                    KeyPrefix = "hnua_fixture",
                    Status = "revoked",
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    RevokedAt = DateTimeOffset.UtcNow,
                };
                payload = JsonSerializer.Serialize(ApiResponse<AdminApiKeyResponse>.CreateSuccess(revoked),
                    AdminApiKeyJsonContext.Default.ApiResponseAdminApiKeyResponse);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) };
        }
    }
}
