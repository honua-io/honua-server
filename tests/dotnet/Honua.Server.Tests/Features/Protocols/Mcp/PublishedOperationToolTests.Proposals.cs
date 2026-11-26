// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Mrtr;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
        using var catalog = new OperationCatalog([new AdminAccessOperationDescriptorProvider()], TimeProvider.System);
        var dispatcher = new OperationDispatcher(
            catalog, [executor],
            new AllowAllPolicyDecisionPoint(), TimeProvider.System, bridge);
        var tool = new PublishedOperationTool(descriptor, "cat-v6662", NullLogger.Instance);
        tool.Describe().Description.Should().Contain("proposal only").And.Contain("separately authenticated");
        var authorization = ProposalAuthorization();
        var http = Context(dispatcher, authorization: authorization, roles: ["admin"]);
        http.Request.Headers["Idempotency-Key"] = "fixture-6662";
        var create = operationId.EndsWith("create", StringComparison.Ordinal);
        var forged = create
            ? Args("""{"name":"synthetic","approvedProposalId":"forged","approvedPlanHash":"forged","principalId":"forged-actor"}""")
            : Args("""{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","approvedProposalId":"forged"}""");
        // Forged approval evidence is not part of the input schema, so it is refused outright.
        await ((Func<Task>)(() => tool.InvokeAsync(http, forged, default))).Should().ThrowAsync<Honua.Geoprocessing.GeoprocessingValidationException>();
        bridge.Calls.Should().Be(0);
        var args = create ? Args("""{"name":"synthetic"}""") : Args("""{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}""");
        var result = await tool.InvokeAsync(http, args, default);
        result.StructuredContent!.Value.GetProperty("status").GetString().Should().Be("RequiresApproval");
        bridge.Context!.PrincipalId.Should().NotBeNullOrWhiteSpace().And.NotBe("forged-actor");
        bridge.Context.ApprovedProposalId.Should().BeNull(); bridge.Context.ApprovedPlanHash.Should().BeNull();
        bridge.Context.IdempotencyRequestHash.Should().HaveLength(64);
        bridge.Calls.Should().Be(1); executor.SubmitCount.Should().Be(0);
        var retry = await tool.InvokeAsync(http, args, default);
        retry.StructuredContent!.Value.GetProperty("proposalId").GetString().Should().Be("proposal-6662");
        bridge.Calls.Should().Be(1); executor.SubmitCount.Should().Be(0);
        var changed = await tool.InvokeAsync(http, create ? Args("""{"name":"changed"}""") : Args("""{"id":"ffffffff-bbbb-cccc-dddd-eeeeeeeeeeee"}"""), default);
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

    [UnitTheory]
    [InlineData("admin.layer.display.set", """{"storageLayerId":7}""")]
    [InlineData("admin.layer.fields.set", """{"storageLayerId":7}""")]
    [InlineData("admin.services.layer-metadata.set", """{"serviceName":"roads","serviceLayerIndex":0}""")]
    [InlineData("admin.services.timeinfo.set", """{"serviceName":"roads"}""")]
    public Task Invoke_MetadataViewMutatingTool_BackedByMapper_FilesDurableProposal(
        string operationId,
        string arguments)
    {
        var definition = AdminApiOperationCatalog.Definitions.Single(d => d.OperationId == operationId);
        return AssertMappedProposalSubmissionAsync(
            new AdminApiOperationDescriptorProvider(),
            new AdminApiOperationApprovalRequestMapper(definition),
            arguments);
    }

    [UnitTheory]
    [InlineData("admin.role.permissions.set", """{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}""")]
    [InlineData("admin.user.roles.update", """{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}""")]
    [InlineData("admin.api-key.update-restrictions", """{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}""")]
    public Task Invoke_AccessViewMutatingTool_BackedByMapper_FilesDurableProposal(
        string operationId,
        string arguments)
    {
        var definition = AdminAccessOperationCatalog.Definitions.Single(d => d.OperationId == operationId);
        return AssertMappedProposalSubmissionAsync(
            new AdminAccessOperationDescriptorProvider(),
            new AdminOperateOperationApprovalRequestMapper(definition),
            arguments);
    }

    private static async Task AssertMappedProposalSubmissionAsync(
        IOperationDescriptorProvider descriptorProvider,
        IOperationApprovalRequestMapper mapper,
        string arguments)
    {
        var operationId = mapper.OperationId;
        var executor = new RecordingExecutor(operationId);
        var bridge = new CapturingProposalBridge();
        using var catalog = new OperationCatalog([descriptorProvider], TimeProvider.System);
        var dispatcher = new OperationDispatcher(
            catalog, [executor],
            new AllowAllPolicyDecisionPoint(), TimeProvider.System, bridge);
        var source = new PublishedOperationToolSource(
            catalog,
            Options.Create(new McpPublishedOperationOptions { Enabled = true }),
            NullLogger<PublishedOperationToolSource>.Instance,
            requestMappers: [mapper]);

        var tool = (await source.GetToolsAsync(CancellationToken.None))
            .OfType<PublishedOperationTool>()
            .Single(candidate => candidate.Name == PublishedOperationTool.ProjectName(operationId));

        tool.Resumes(McpRequestStateKind.Approval).Should().BeTrue();
        tool.Describe().Description.Should().Contain("proposal only");

        var result = await tool.InvokeAsync(
            Context(dispatcher, authorization: ProposalAuthorization(), roles: ["admin", "agent"]),
            Args(arguments),
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        var body = result.StructuredContent!.Value;
        body.GetProperty("status").GetString().Should().Be("RequiresApproval");
        body.GetProperty("proposalId").GetString().Should().Be("proposal-6662");
        bridge.Calls.Should().Be(1);
        executor.SubmitCount.Should().Be(0);
    }

    [UnitTest]
    public async Task ProductionComposition_EveryProjectedOperatorGateTool_RoutesThroughProposalSubmission()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IOperationProposalStore>());
        services.AddOperationsToolset(configuration, environment);
        services.AddAdminAccessOperations();
        McpServiceCollectionExtensions.AddMcpPublishedOperationTools(services, configuration);

        await using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IOperationCatalog>();
        var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None);
        var descriptorsByName = snapshot.Operations
            .GroupBy(descriptor => PublishedOperationTool.ProjectName(descriptor.OperationId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var sources = provider.GetServices<IMcpToolSource>();
        var published = new List<PublishedOperationTool>();
        foreach (var source in sources)
        {
            published.AddRange((await source.GetToolsAsync(CancellationToken.None)).OfType<PublishedOperationTool>());
        }

        var operatorGated = published
            .Where(tool => descriptorsByName.TryGetValue(tool.Name, out var d)
                && d.ApprovalModel == OperationApprovalModel.OperatorGate)
            .ToArray();

        operatorGated.Should().NotBeEmpty();
        foreach (var tool in operatorGated)
        {
            tool.Resumes(McpRequestStateKind.Approval).Should().BeTrue(
                $"{tool.Name} is an advertised OperatorGate tool backed by an approval mapper and must file a proposal instead of dead-ending");
            tool.Describe().Description.Should().Contain(
                "proposal only",
                $"{tool.Name} must advertise proposal-only behavior");
        }
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
        public OperationRequest? Request { get; private set; }
        public Task<OperationApprovalBridgeResult> CreateProposalAsync(IOperationDescriptor descriptor, OperationRequest request,
            OperationPolicyContext context, PolicyDecision decision, CancellationToken cancellationToken = default)
        {
            Calls++;
            Context = context;
            Request = request;
            return Task.FromResult(new OperationApprovalBridgeResult { IsDurable = true, ProposalId = "proposal-6662", AuditId = "audit-6662" });
        }
    }
}
