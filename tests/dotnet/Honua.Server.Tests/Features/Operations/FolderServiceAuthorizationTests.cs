// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
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

    private static ClaimsPrincipal Principal(string name)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, name)], "test"));
}
