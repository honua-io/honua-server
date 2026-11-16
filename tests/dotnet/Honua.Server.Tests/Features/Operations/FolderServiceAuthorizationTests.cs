// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Validation;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class FolderServiceAuthorizationTests
{
    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RequireServiceAccess_ApprovedReplay_EvaluatesRequesterWithoutReplacingHttpPrincipal(bool allowed)
    {
        var evaluator = Substitute.For<IAccessPolicyEvaluator>();
        await using var services = new ServiceCollection().AddSingleton(evaluator).BuildServiceProvider();
        var operatorPrincipal = Principal("operator");
        var requester = Principal("requester");
        var context = new DefaultHttpContext { RequestServices = services, User = operatorPrincipal };
        var proposal = new OperationProposal
        {
            ProposalId = "proposal", Kind = OperationClass.AdminConfigChange,
            Status = OperationProposalStatus.Executing,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        context.Features.Set(new FolderReplayServicePrincipal(requester, proposal));
        ClaimsPrincipal? observedHttpPrincipal = null;
        ClaimsPrincipal? evaluatedPrincipal = null;
        evaluator.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<AccessPolicy?>(), Arg.Any<AccessPolicy?>(), AccessScope.Write)
            .Returns(call =>
            {
                observedHttpPrincipal = context.User;
                evaluatedPrincipal = call.ArgAt<ClaimsPrincipal>(0);
                return allowed ? AccessDecision.Allowed() : AccessDecision.Forbidden();
            });

        var result = await FolderEndpoints.RequireServiceAccessAsync(context, new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service", Name = "survey" }
        });

        evaluatedPrincipal.Should().BeSameAs(requester);
        observedHttpPrincipal.Should().BeSameAs(operatorPrincipal);
        context.User.Should().BeSameAs(operatorPrincipal);
        (result is null).Should().Be(allowed);
    }

    [UnitTest]
    public async Task RequireServiceAccess_ApprovedReplay_DoesNotBorrowOperatorPermissionCache()
    {
        var roleStore = Substitute.For<IRoleStore>();
        var evaluatedUsers = new List<string>();
        RecordEffectivePermissions(roleStore, evaluatedUsers);
        var evaluator = Substitute.For<IAccessPolicyEvaluator>();
        evaluator.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<AccessPolicy?>(), Arg.Any<AccessPolicy?>(), AccessScope.Write)
            .Returns(call => call.ArgAt<ClaimsPrincipal>(0).FindFirstValue(ClaimTypes.NameIdentifier) == "operator"
                ? AccessDecision.Allowed() : AccessDecision.Forbidden());
        await using var services = new ServiceCollection()
            .AddSingleton(roleStore).AddSingleton<IPermissionResolver, PermissionResolver>()
            .AddSingleton(evaluator).AddOptions<RbacOptions>().Services.BuildServiceProvider();
        var operatorPrincipal = Principal("operator");
        var requester = Principal("requester");
        var context = new DefaultHttpContext { RequestServices = services, User = operatorPrincipal };
        var service = new MetadataV2Service { Metadata = new MetadataV2ObjectMetadata { Id = "service", Name = "survey" } };
        (await AccessPolicyHelpers.RequireServiceAccessAsync(context, service, AuthorizationOperation.Admin))
            .Should().BeNull();
        context.Features.Set(new FolderReplayServicePrincipal(requester, new OperationProposal
        {
            ProposalId = "proposal", Kind = OperationClass.AdminConfigChange,
            Status = OperationProposalStatus.Executing,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        }));

        var result = await FolderEndpoints.RequireServiceAccessAsync(context, service);

        result.Should().NotBeNull();
        evaluatedUsers.Should().Equal("operator", "requester");
        context.User.Should().BeSameAs(operatorPrincipal);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedAuthorization_RequiresEveryFacetWithRequesterAdminAccess(bool denyLastFacet)
    {
        var operatorPrincipal = Principal("operator");
        var requester = Principal("requester");
        var operatorEvaluator = Substitute.For<IOperatorAuthorizationEvaluator>();
        operatorEvaluator.EvaluateAsync(operatorPrincipal, Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.Allowed()));
        var policyEvaluator = Substitute.For<IAccessPolicyEvaluator>();
        var first = new AccessPolicy();
        var last = new AccessPolicy();
        policyEvaluator.Evaluate(requester, Arg.Any<AccessPolicy?>(), Arg.Any<AccessPolicy?>(), AccessScope.Write)
            .Returns(call => denyLastFacet && ReferenceEquals(call.ArgAt<AccessPolicy?>(2), last)
                ? AccessDecision.Forbidden() : AccessDecision.Allowed());
        await using var services = new ServiceCollection().AddSingleton(policyEvaluator)
            .AddSingleton(new OperatorApprovalGate(operatorEvaluator, Substitute.For<IOperatorApprovalEvaluator>(),
                NullLogger<OperatorApprovalGate>.Instance)).BuildServiceProvider();
        var graph = CreateFacetGraph(first, last);

        var authorization = await FolderServiceAuthorization.EvaluateAsync(new FolderAuthorizationContext
        {
            Original = graph,
            Mutation = new FolderMutation(graph, null) { ServiceIds = ["map", "feature"] },
            Services = services,
            OperatorPrincipal = operatorPrincipal,
            RequesterAccess = new ServiceAccessContext
            {
                Services = services, Principal = requester,
                // The folder policy must enforce admin access regardless of caller-supplied scope.
                Operation = AuthorizationOperation.Read
            }
        });

        authorization.RequesterEvaluations.Should().HaveCount(2);
        (authorization.Failure is FolderAuthorizationFailure.Requester).Should().Be(denyLastFacet);
        await operatorEvaluator.Received(1).EvaluateAsync(operatorPrincipal,
            Arg.Is<OperatorAuthorizationRequest>(request => request.ResourceId == "survey" && request.Operation == OperatorOperation.Update),
            Arg.Any<CancellationToken>());
        policyEvaluator.Received(1).Evaluate(requester, Arg.Is<AccessPolicy?>(policy => policy == null), Arg.Is<AccessPolicy?>(policy => ReferenceEquals(policy, first)), AccessScope.Write);
        policyEvaluator.Received(1).Evaluate(requester, Arg.Is<AccessPolicy?>(policy => policy == null), Arg.Is<AccessPolicy?>(policy => ReferenceEquals(policy, last)), AccessScope.Write);
    }

    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharedAuthorization_RefusesTenantOrOperatorDenialBeforeRequesterPolicy(bool tenantMismatch)
    {
        var principal = Principal("requester");
        var operatorEvaluator = Substitute.For<IOperatorAuthorizationEvaluator>();
        operatorEvaluator.EvaluateAsync(principal, Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.Forbidden()));
        var policyEvaluator = Substitute.For<IAccessPolicyEvaluator>();
        await using var services = new ServiceCollection().AddSingleton(policyEvaluator)
            .AddSingleton(new OperatorApprovalGate(operatorEvaluator, Substitute.For<IOperatorApprovalEvaluator>(),
                NullLogger<OperatorApprovalGate>.Instance)).BuildServiceProvider();
        var graph = CreateTenantGraph();

        var authorization = await FolderServiceAuthorization.EvaluateAsync(new FolderAuthorizationContext
        {
            Original = graph,
            Mutation = new FolderMutation(graph, null) { ServiceIds = ["service"] },
            Services = services,
            OperatorPrincipal = principal,
            FolderTenant = tenantMismatch ? "tenant-b" : "tenant-a",
            RequesterAccess = new ServiceAccessContext
            {
                Services = services, Principal = principal, Operation = AuthorizationOperation.Admin, TenantId = "tenant-a"
            }
        });

        authorization.RequesterEvaluations.Should().BeEmpty();
        if (tenantMismatch)
        {
            authorization.Failure.Should().BeOfType<FolderAuthorizationFailure.Tenant>();
            await operatorEvaluator.DidNotReceive().EvaluateAsync(Arg.Any<ClaimsPrincipal>(),
                Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>());
        }
        else
        {
            authorization.Failure.Should().BeOfType<FolderAuthorizationFailure.Operator>();
        }
        policyEvaluator.DidNotReceive().Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<AccessPolicy?>(),
            Arg.Any<AccessPolicy?>(), Arg.Any<AccessScope>());
    }

    private static void RecordEffectivePermissions(IRoleStore roleStore, List<string> evaluatedUsers)
    {
        roleStore.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var userId = call.ArgAt<string>(0);
                evaluatedUsers.Add(userId);
                return Task.FromResult(new EffectivePermissions
                {
                    UserId = userId,
                    Roles = call.ArgAt<IReadOnlyList<string>>(1),
                    Permissions = userId == "operator"
                        ? [new PermissionGrant { Service = "survey", Layer = "*", Operation = "admin" }]
                        : []
                });
            });
    }

    private static MetadataV2Graph CreateFacetGraph(AccessPolicy first, AccessPolicy last)
    {
        return new MetadataV2Graph
        {
            Services = [
                new MetadataV2Service { Metadata = new MetadataV2ObjectMetadata { Id = "map", Name = "survey" }, AccessPolicy = first },
                new MetadataV2Service { Metadata = new MetadataV2ObjectMetadata { Id = "feature", Name = "survey" }, AccessPolicy = last }
            ]
        };
    }

    private static MetadataV2Graph CreateTenantGraph()
    {
        return new MetadataV2Graph
        {
            Services = [new MetadataV2Service
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "service", Name = "survey", Tenant = "tenant-a" }
            }]
        };
    }

    private static ClaimsPrincipal Principal(string name)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, name), new Claim(ClaimTypes.Role, "role")], "test"));
}
