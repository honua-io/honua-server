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
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
            Metadata = new MetadataV2Identity { Id = "service", Name = "survey" }
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
        var service = new MetadataV2Service { Metadata = new MetadataV2Identity { Id = "service", Name = "survey" } };
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

    private static ClaimsPrincipal Principal(string name)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, name), new Claim(ClaimTypes.Role, "role")], "test"));
}
