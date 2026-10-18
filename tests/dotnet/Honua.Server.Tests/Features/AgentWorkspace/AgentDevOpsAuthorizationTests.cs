// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class AgentDevOpsAuthorizationTests
{
    [UnitTest]
    public async Task AuthorizationRequiresCurrentTargetEnvironmentAndDeploymentGrant()
    {
        using var fixture = Create();
        var request = new AgentDevOpsAuthorizationRequest("server-1", "ecs-1", "dev", null);
        var result = await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, request);
        ((IValueHttpResult)result).Value.Should().Be(new AgentDevOpsAuthorizationResponse("server-1", "ecs-1", "dev", "aws-ecs", "honua-aws-ecs-alb"));
        await fixture.Operator.Received().EvaluateAsync(fixture.Context.User,
            Arg.Is<OperatorAuthorizationRequest>(value => value.ResourceId == "ecs-1" && value.Operation == OperatorOperation.Read),
            Arg.Any<CancellationToken>());
        Status(await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, request with { Environment = "prod" })).Should().Be(403);
        Status(await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, request with { DeployTargetId = "missing" })).Should().Be(403);
        fixture.Operator.EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Honua.Core.Features.Security.Domain.AccessDecision(false, false, "revoked")));
        Status(await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, request)).Should().Be(403);
    }

    [UnitTest]
    public async Task StatusAndProposalReadsRequireActualOperationTargetAndTenant()
    {
        using var fixture = Create();
        var operation = Operation("ecs-1", "tenant-a");
        fixture.Operations.GetAsync("op-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<WorkflowOperationRecord?>(operation));
        foreach (var tool in new[] { "get_gitops_proposal", "get_devops_operation_status" })
        {
            var request = new AgentDevOpsAuthorizationRequest("server-1", "ecs-1", "dev", tool, Args(new { operationId = "op-1" }));
            Status(await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, request)).Should().Be(200);
            fixture.Operations.GetAsync("op-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<WorkflowOperationRecord?>(operation with { Deploy = operation.Deploy! with { TargetId = "ecs-other" } }));
            Status(await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, request)).Should().Be(403);
            fixture.Operations.GetAsync("op-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<WorkflowOperationRecord?>(operation with { Audit = new() { TenantId = "tenant-b" } }));
            Status(await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, request)).Should().Be(403);
            fixture.Operations.GetAsync("op-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<WorkflowOperationRecord?>(operation));
        }
    }

    [UnitTest]
    public async Task EffectiveArgumentsCannotSelectAnotherEnvironmentOrAnImplicitMetadataDefault()
    {
        using var fixture = Create();
        var denied = new[]
        {
            new AgentDevOpsAuthorizationRequest("server-1", "ecs-1", "dev", "deploy_service_gitops", Args(new { environmentsCsv = "dev,prod" })),
            new AgentDevOpsAuthorizationRequest("server-1", "ecs-1", "dev", "create_scoped_gitops_pull_request", Args(new { releasePackageJson = "{\"targetEnvironments\":[\"prod\"]}" })),
            new AgentDevOpsAuthorizationRequest("server-1", "ecs-1", "dev", "plan_metadata_release_gitops", Args(new { releasePackageJson = "{}" })),
            new AgentDevOpsAuthorizationRequest("server-1", "ecs-1", "dev", "provision_infrastructure", Args(new { variablesJson = "{\"environment\":\"staging\"}" })),
        };
        foreach (var request in denied)
            Status(await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, request)).Should().Be(403);
        var allowed = new AgentDevOpsAuthorizationRequest("server-1", "ecs-1", "dev", "create_scoped_gitops_pull_request", Args(new { releasePackageJson = "{\"targetEnvironments\":[\"dev\"]}" }));
        Status(await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, allowed)).Should().Be(200);
    }

    [UnitTest]
    public async Task TenantAdministratorCannotBorrowPlatformDeploymentAuthority()
    {
        using var fixture = Create();
        fixture.Context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new(ClaimTypes.NameIdentifier, "tenant-admin"), new("tenant_id", "tenant-a"), new("permission", "admin:write"),
        ], "test"));
        var request = new AgentDevOpsAuthorizationRequest("server-1", "ecs-1", "dev", null);
        Status(await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, request)).Should().Be(403);
        await fixture.Registry.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task RuntimeBindingsRequireCanonicalBackendAndMatchingProvisioningStack()
    {
        using var fixture = Create();
        foreach (var (kind, backend, runtime) in new[]
        {
            (DeployTargetKind.AwsEcs, "honua-aws-ecs-alb", "aws-ecs"),
            (DeployTargetKind.AwsLambda, "honua-gitops-aws-lambda", "aws-lambda"),
            (DeployTargetKind.Kubernetes, "honua-kubernetes-deployment", "aws-eks")
        })
        {
            var target = new DeployTargetDefinition
            { TargetId = "cloud-1", Environment = "dev", TargetKind = kind, Backend = backend, TargetName = "cell", Parameters = new Dictionary<string, string> { ["honua.runtime_kind"] = runtime } };
            fixture.Registry.GetAsync("cloud-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<DeployTargetDefinition?>(target));
            var read = new AgentDevOpsAuthorizationRequest("server-1", "cloud-1", "dev", null);
            var result = await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, read);
            ((IValueHttpResult)result).Value.Should().Be(new AgentDevOpsAuthorizationResponse("server-1", "cloud-1", "dev", runtime, backend));
            var provision = read with { CanonicalTool = "provision_infrastructure", Arguments = Args(new { stack = runtime, variablesJson = "{\"environment\":\"dev\"}" }) };
            Status(await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, provision)).Should().Be(200);
            Status(await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace,
                provision with { Arguments = Args(new { stack = "other", variablesJson = "{\"environment\":\"dev\"}" }) })).Should().Be(403);
            fixture.Registry.GetAsync("cloud-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<DeployTargetDefinition?>(target with { Parameters = new Dictionary<string, string> { ["honua.runtime_kind"] = "other" } }));
            Status(await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, read)).Should().Be(403);
            if (runtime == "aws-eks")
            {
                fixture.Registry.GetAsync("cloud-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<DeployTargetDefinition?>(target with { Backend = "honua-kubernetes-argo-rollouts" }));
                Status(await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, read)).Should().Be(403);
                fixture.Registry.GetAsync("cloud-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<DeployTargetDefinition?>(target with { Parameters = new Dictionary<string, string>() }));
                Status(await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, read)).Should().Be(403);
            }
            fixture.Registry.GetAsync("cloud-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<DeployTargetDefinition?>(target with { Backend = "honua-gitops-kubernetes" }));
            Status(await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, read)).Should().Be(403);
        }
    }

    [UnitTest]
    public async Task LambdaAndEksRecheckGrantsAndOperationRuntimeAfterDiscovery()
    {
        using var fixture = Create();
        foreach (var (kind, backend) in new[]
        {
            (DeployTargetKind.AwsLambda, "honua-gitops-aws-lambda"),
            (DeployTargetKind.Kubernetes, "honua-kubernetes-deployment")
        })
        {
            fixture.Registry.GetAsync("cloud-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<DeployTargetDefinition?>(new()
            {
                TargetId = "cloud-1",
                Environment = "dev",
                TargetKind = kind,
                Backend = backend,
                TargetName = "cell",
                Parameters = new Dictionary<string, string> { ["honua.runtime_kind"] = kind == DeployTargetKind.Kubernetes ? "aws-eks" : "aws-lambda" }
            }));
            var operation = Operation("cloud-1", "tenant-a");
            var read = new AgentDevOpsAuthorizationRequest("server-1", "cloud-1", "dev", "get_devops_operation_status", Args(new { operationId = "op-1" }));
            fixture.Operations.GetAsync("op-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<WorkflowOperationRecord?>(operation));
            Status(await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, read)).Should().Be(403);
            operation = operation with { Deploy = operation.Deploy! with { TargetKind = kind, Backend = backend } };
            fixture.Operations.GetAsync("op-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<WorkflowOperationRecord?>(operation));
            Status(await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, read)).Should().Be(200);
            fixture.Operator.EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new Honua.Core.Features.Security.Domain.AccessDecision(false, false, "revoked")));
            Status(await AgentWorkspaceEndpoints.AuthorizeDevOps(fixture.Context, fixture.Workspace, read)).Should().Be(403);
            fixture.Operator.EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new Honua.Core.Features.Security.Domain.AccessDecision(true, false, null)));
        }
    }

    private static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;
    private static Dictionary<string, JsonElement> Args(object value)
        => JsonSerializer.SerializeToElement(value).EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
    private static WorkflowOperationRecord Operation(string target, string tenant) => new()
    {
        OperationId = "op-1",
        Kind = WorkflowOperationKind.Deploy,
        Status = WorkflowOperationStatus.Planned,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Audit = new() { TenantId = tenant },
        Deploy = new() { TargetId = target, TargetKind = DeployTargetKind.AwsEcs, Backend = "honua-aws-ecs-alb", Environment = "dev", TargetName = "cell", DesiredRevision = "candidate" },
    };

    private static Fixture Create()
    {
        var workspace = Substitute.For<IAgentWorkspaceStore>();
        workspace.GetTargetIdAsync().Returns(Task.FromResult("server-1"));
        var registry = Substitute.For<IDeployTargetRegistry>();
        registry.GetAsync("ecs-1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<DeployTargetDefinition?>(new()
        { TargetId = "ecs-1", Environment = "dev", TargetKind = DeployTargetKind.AwsEcs, Backend = "honua-aws-ecs-alb", TargetName = "cell" }));
        var operations = Substitute.For<IWorkflowOperationStore>();
        var evaluator = Substitute.For<IOperatorAuthorizationEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Honua.Core.Features.Security.Domain.AccessDecision(true, false, null)));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<string>()).Returns(Task.FromResult(AuthorizationResult.Success()));
        var services = new ServiceCollection().AddSingleton(authorization).AddSingleton(registry).AddSingleton(operations)
            .AddSingleton(new OperatorApprovalGate(evaluator, Substitute.For<IOperatorApprovalEvaluator>(), NullLogger<OperatorApprovalGate>.Instance))
            .AddSingleton<IOptions<TenantContextOptions>>(Options.Create(new TenantContextOptions
            { Enabled = true, TenantClaimTypes = ["tenant_id"], MultiTenantAdminRoles = ["platform-admin"] })).BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([
            new(ClaimTypes.NameIdentifier, "operator-a"), new(ClaimTypes.Role, "platform-admin"), new("tenant_id", "tenant-a"), new("permission", "admin:write"),
        ], "test"))
        };
        return new(context, workspace, registry, operations, evaluator, services);
    }
    private sealed record Fixture(DefaultHttpContext Context, IAgentWorkspaceStore Workspace, IDeployTargetRegistry Registry,
        IWorkflowOperationStore Operations, IOperatorAuthorizationEvaluator Operator, ServiceProvider Services) : IDisposable
    {
        public void Dispose() => Services.Dispose();
    }
}
