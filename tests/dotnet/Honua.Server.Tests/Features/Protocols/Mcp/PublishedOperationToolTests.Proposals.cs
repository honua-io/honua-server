// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class PublishedOperationToolTests
{
    [UnitTheory]
    [InlineData("admin.api-key.create")]
    [InlineData("admin.api-key.revoke")]
    public async Task Invoke_KeyProposal_AllowStillProposesWithNoCacheOrActuation(string operationId)
    {
        var descriptor = AdminAccessOperationCatalog.Descriptors.Single(d => d.OperationId == operationId);
        var executor = new RecordingExecutor(operationId);
        var bridge = new CapturingProposalBridge();
        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new AdminAccessOperationDescriptorProvider()], TimeProvider.System), [executor],
            new AllowAllPolicyDecisionPoint(), TimeProvider.System, bridge);
        var tool = new PublishedOperationTool(descriptor, "cat-v6662", NullLogger.Instance);
        tool.Describe().Description.Should().Contain("proposal only").And.Contain("separately authenticated");
        var authorization = ProposalAuthorization();
        var http = Context(dispatcher, authorization: authorization, roles: ["admin"]);
        http.Request.Headers["Idempotency-Key"] = "fixture-6662";
        var args = operationId.EndsWith("create", StringComparison.Ordinal) ? Args("""{"name":"synthetic","approvedProposalId":"forged","approvedPlanHash":"forged","principalId":"forged-actor"}""") : Args("""{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","approvedProposalId":"forged"}""");
        var result = await tool.InvokeAsync(http, args, default);
        result.StructuredContent!.Value.GetProperty("status").GetString().Should().Be("RequiresApproval");
        bridge.Context!.PrincipalId.Should().NotBeNullOrWhiteSpace().And.NotBe("forged-actor");
        bridge.Context.ApprovedProposalId.Should().BeNull(); bridge.Context.ApprovedPlanHash.Should().BeNull();
        bridge.Context.IdempotencyRequestHash.Should().HaveLength(64);
        bridge.Calls.Should().Be(1); executor.SubmitCount.Should().Be(0);
        var retry = await tool.InvokeAsync(http, args, default);
        retry.StructuredContent!.Value.GetProperty("proposalId").GetString().Should().Be("proposal-6662");
        bridge.Calls.Should().Be(1); executor.SubmitCount.Should().Be(0);
        var changed = await tool.InvokeAsync(http, Args("""{"name":"changed","id":"different"}"""), default);
        changed.StructuredContent!.Value.GetProperty("status").GetString().Should().Be("Failed");
        bridge.Calls.Should().Be(1); executor.SubmitCount.Should().Be(0);
    }

    [UnitTest]
    public async Task Invoke_KeyProposal_MissingProposalRuntimeRefusesInsteadOfOrdinarySubmit()
    {
        var invoker = new CountingInvoker(_ => CompletedHandle("admin.api-key.create"));
        var tool = new PublishedOperationTool(AdminAccessOperationCatalog.Descriptors.Single(d => d.OperationId == "admin.api-key.create"), "cat-v6662", NullLogger.Instance);
        var result = await tool.InvokeAsync(Context(invoker, authorization: ProposalAuthorization(), roles: ["admin"]), Args("""{"name":"synthetic"}"""), default);
        result.IsError.Should().BeTrue(); invoker.SubmitCount.Should().Be(0);
    }

    [UnitTest]
    public async Task Invoke_KeyProposal_DryRunRefusesWithoutDispatch()
    {
        var invoker = new CountingInvoker(_ => CompletedHandle("admin.api-key.create"));
        var tool = new PublishedOperationTool(AdminAccessOperationCatalog.Descriptors.Single(d => d.OperationId == "admin.api-key.create"), "cat-v6662", NullLogger.Instance);
        var result = await tool.InvokeAsync(Context(invoker, authorization: ProposalAuthorization(), roles: ["admin"]), Args("""{"name":"synthetic","dryRun":true}"""), default);
        result.IsError.Should().BeTrue(); invoker.SubmitCount.Should().Be(0);
    }

    [UnitTest]
    public async Task Invoke_KeyProposal_StaleCompletedResponseCacheCannotAuthorizeOrReplaceProposal()
    {
        var descriptor = AdminAccessOperationCatalog.Descriptors.Single(d => d.OperationId == "admin.api-key.create");
        // Even a misclassified read-only tool projection must not enable the response cache
        // on this proposal channel; the runtime resolves the real gated descriptor itself.
        var toolDescriptor = descriptor with
        {
            Policy = descriptor.Policy with
            {
                SideEffectClass = OperationSideEffectClass.ReadOnly,
                Determinism = OperationDeterminism.Deterministic,
            }
        };
        var executor = new RecordingExecutor(descriptor.OperationId); var bridge = new CapturingProposalBridge();
        var dispatcher = new OperationDispatcher(new OperationCatalog([new AdminAccessOperationDescriptorProvider()], TimeProvider.System),
            [executor], new AllowAllPolicyDecisionPoint(), TimeProvider.System, bridge);
        var cache = Substitute.For<IPublishedOperationCache>();
        var tool = new PublishedOperationTool(toolDescriptor, "cat-v6662", NullLogger.Instance);
        var result = await tool.InvokeAsync(Context(dispatcher, cache: cache, authorization: ProposalAuthorization(), roles: ["admin"]),
            Args("""{"name":"synthetic"}"""), default);
        result.StructuredContent!.Value.GetProperty("status").GetString().Should().Be("RequiresApproval");
        cache.DidNotReceiveWithAnyArgs().TryGet(default!);
        executor.SubmitCount.Should().Be(0); bridge.Calls.Should().Be(1);
    }

    private static IAuthorizationService ProposalAuthorization()
    {
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), AuthenticationExtensions.AdminPolicy)
            .Returns(AuthorizationResult.Success());
        return authorization;
    }

    private sealed class CapturingProposalBridge : IOperationApprovalBridge
    {
        public int Calls { get; private set; }
        public OperationPolicyContext? Context { get; private set; }
        public Task<OperationApprovalBridgeResult> CreateProposalAsync(IOperationDescriptor descriptor, OperationRequest request,
            OperationPolicyContext context, PolicyDecision decision, CancellationToken cancellationToken = default)
        {
            Calls++; Context = context;
            return Task.FromResult(new OperationApprovalBridgeResult { IsDurable = true, ProposalId = "proposal-6662", AuditId = "audit-6662" });
        }
    }
}
