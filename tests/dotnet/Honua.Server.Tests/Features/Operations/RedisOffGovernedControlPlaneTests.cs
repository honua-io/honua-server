// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Exceptions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsRedisOff;

/// <summary>
/// 2026.1 rc.3 J1/S1: a host composed without Redis must still advertise the governed
/// <c>honua_admin_*</c> catalog and refuse proposal-requiring calls with the typed
/// capability-unavailable outcome. Before the fix the 20 <see cref="AdminApiOperationCatalog"/>
/// tools were silently absent on Redis-off cells, because <c>AddOperationsToolset</c> gates their
/// executors on a registered <see cref="IOperationProposalStore"/> and Program.cs registered one
/// only inside the Redis block.
/// </summary>
public sealed class RedisOffGovernedControlPlaneTests
{
    [UnitTest]
    public void AdminApiCatalog_IsTheTwentyToolsTheRedisOffCellLost()
    {
        AdminApiOperationCatalog.Definitions.Should().HaveCount(20);
    }

    [UnitTest]
    public void RedisOffComposition_RegistersGovernedAdminCatalogs_WithoutClaimingADurableRuntime()
    {
        var services = ComposeRedisOff(Environments.Production);

        services.Should().Contain(descriptor =>
            descriptor.ImplementationType == typeof(AdminApiOperationDescriptorProvider));
        services.Should().Contain(descriptor =>
            descriptor.ImplementationType == typeof(AdminConnectImportOperationDescriptorProvider));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            (descriptor.ImplementationType == typeof(OperationRuntimeStartupValidator) ||
             descriptor.ImplementationType == typeof(PlannedProposalReconciler) ||
             descriptor.ImplementationType == typeof(QueuedOperationReconciler)),
            "the fail-closed placeholder must never satisfy the durable-runtime gate");

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = true,
        });
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IOperationApprovalReplayVerifier>()
            .Should().BeOfType<UnavailableOperationApprovalReplayVerifier>(
                "replay can never verify against the placeholder store");
        provider.GetService<IOperationGateway>().Should().BeNull(
            "no gateway is composed on Redis-off, so approval refuses instead of persisting");
    }

    [UnitTest]
    public async Task RedisOffComposition_ProjectsAllTwentyAdminApiTools()
    {
        var services = ComposeRedisOff(Environments.Production);
        await using var provider = services.BuildServiceProvider();
        var source = BuildToolSource(provider);

        var published = (await source.GetToolsAsync(CancellationToken.None))
            .Select(static tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);

        published.Should().Contain(AdminApiOperationCatalog.Definitions
            .Select(static definition => PublishedOperationTool.ProjectName(definition.OperationId)));
    }

    [UnitTest]
    public async Task CompositionWithoutAnyProposalStore_DropsTheAdminApiTools_RootCause()
    {
        // Documents the defect the placeholder closes: with no proposal store registered at all
        // the executors are never composed, so the projection filters every one of the 20 tools.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IAuditLog>());
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        services.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);
        await using var provider = services.BuildServiceProvider();

        var published = (await BuildToolSource(provider).GetToolsAsync(CancellationToken.None))
            .Select(static tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);

        published.Should().NotContain(AdminApiOperationCatalog.Definitions
            .Select(static definition => PublishedOperationTool.ProjectName(definition.OperationId)));
    }

    [UnitTest]
    public async Task RedisOffProduction_ApprovalGatedAdminCall_RefusesWithTypedUnavailableOutcome()
    {
        var services = ComposeRedisOff(Environments.Production);
        services.AddSingleton<IOperationPolicyDecisionPoint>(new RequireApprovalPolicyDecisionPoint());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IOperationInvoker>();

        var refusal = await Assert.ThrowsAsync<CapabilityUnavailableException>(() => invoker.SubmitAsync(
            new OperationRequest
            {
                OperationId = "admin.layer.filter.set",
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["layerId"] = "1" },
            },
            new OperationPolicyContext()));

        refusal.MissingDependency.Should().Be("redis");
        var mcpError = McpErrorMapper.Map(refusal);
        mcpError.Data!.Code.Should().Be(McpErrorMapper.Codes.Unavailable);
        mcpError.Data.Retryable.Should().BeFalse();
        mcpError.Data.MissingDependency.Should().Be("redis");
    }

    [UnitTest]
    public async Task RedisOffComposition_ApprovalBridge_RefusesWithTypedRedisDependency()
    {
        // honua-server#5733: even where the operation record itself is volatile (Development/Test),
        // an approval-gated call must not invent a proposal, and must not degrade to a non-durable
        // failure every adapter projects as an untyped 500. The bridge refuses with the typed
        // capability-unavailable receipt the rest of the Redis-off surface emits.
        var services = ComposeRedisOff("Test");
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IOperationCatalog>();
        var descriptor = await catalog.GetDescriptorAsync("admin.layer.filter.set");
        descriptor.Should().NotBeNull("the governed descriptor is composed on Redis-off hosts");
        var bridge = scope.ServiceProvider.GetRequiredService<IOperationApprovalBridge>();

        var refusal = await Assert.ThrowsAsync<CapabilityUnavailableException>(() => bridge.CreateProposalAsync(
            descriptor!,
            LayerFilterRequest(),
            new OperationPolicyContext
            {
                OperationInstanceId = "opinst-redis-off",
                CorrelationId = "corr-redis-off",
            },
            new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval }));

        refusal.MissingDependency.Should().Be(CapabilityUnavailableCodes.RedisDependency);
        refusal.Capability.Should().Be(CapabilityUnavailableCodes.ControlPlaneProposalsCapability);
        refusal.RemediationRef.Should().Be(CapabilityUnavailableCodes.RedisRemediationRef);
        var mcpError = McpErrorMapper.Map(refusal);
        mcpError.Data!.Code.Should().Be(McpErrorMapper.Codes.Unavailable);
        mcpError.Data.Capability.Should().Be(CapabilityUnavailableCodes.ControlPlaneProposalsCapability);
    }

    [UnitTest]
    public async Task ApprovalBridge_DurableStoreWithoutGateway_FailsNonDurablyWithoutClaimingRedisIsMissing()
    {
        // A real proposal store with no gateway is a composition defect, not a Redis-off host, so
        // the bridge keeps its fail-closed non-durable result instead of naming Redis.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IAuditLog>());
        services.AddSingleton(Substitute.For<IOperationProposalStore>());
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Test");
        services.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);
        services.RemoveAll<IOperationGateway>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var descriptor = await scope.ServiceProvider.GetRequiredService<IOperationCatalog>()
            .GetDescriptorAsync("admin.layer.filter.set");
        var bridge = scope.ServiceProvider.GetRequiredService<IOperationApprovalBridge>();

        var result = await bridge.CreateProposalAsync(
            descriptor!,
            LayerFilterRequest(),
            new OperationPolicyContext { OperationInstanceId = "opinst-1", CorrelationId = "corr-1" },
            new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval });

        result.IsDurable.Should().BeFalse();
        result.ProposalId.Should().BeNull();
        result.Reason.Should().Contain("durable proposal gateway is unavailable");
    }

    private static OperationRequest LayerFilterRequest() => new()
    {
        OperationId = "admin.layer.filter.set",
        Parameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["layerId"] = "1" },
    };

    [UnitTest]
    public async Task UnavailableOperationProposalStore_RefusesWithTypedRedisDependency()
    {
        var store = new UnavailableOperationProposalStore();

        var refusal = await Assert.ThrowsAsync<CapabilityUnavailableException>(() => store.GetAsync("proposal-1"));

        refusal.MissingDependency.Should().Be("redis");
        refusal.Capability.Should().Be(CapabilityUnavailableCodes.ControlPlaneProposalsCapability);
        UnavailableOperationProposalStore.IsDurable(store).Should().BeFalse();
        UnavailableOperationProposalStore.IsDurable(null).Should().BeFalse();
        UnavailableOperationProposalStore.IsDurable(Substitute.For<IOperationProposalStore>()).Should().BeTrue();
    }

    private static ServiceCollection ComposeRedisOff(string environmentName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IAuditLog>());
        var readiness = Substitute.For<IReadinessCheckService>();
        readiness.CheckReadinessAsync(Arg.Any<CancellationToken>()).Returns(ReadinessResult.Ready());
        services.AddSingleton(readiness);
        // Mirrors the Program.cs no-Redis branch.
        services.AddSingleton<IOperationProposalStore, UnavailableOperationProposalStore>();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        services.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);
        return services;
    }

    private static PublishedOperationToolSource BuildToolSource(IServiceProvider provider)
        => new(
            provider.GetRequiredService<IOperationCatalog>(),
            Options.Create(new McpPublishedOperationOptions { Enabled = true }),
            NullLogger<PublishedOperationToolSource>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetServices<IOperationApprovalRequestMapper>());

    private sealed class RequireApprovalPolicyDecisionPoint : IOperationPolicyDecisionPoint
    {
        public Task<PolicyDecision> EvaluateAsync(
            IOperationDescriptor descriptor,
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval });
    }
}
