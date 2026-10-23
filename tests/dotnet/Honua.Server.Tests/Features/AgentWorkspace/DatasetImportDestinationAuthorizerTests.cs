// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class DatasetImportDestinationAuthorizerTests
{
    [UnitTest]
    public async Task OccupiedTargetsAreUnsupportedForBulkWritesButRemainReadableAfterOwnImport()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "human-1"), new Claim(ClaimTypes.Role, "admin")], "Bearer"));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<string>()).Returns(AuthorizationResult.Success());
        var store = Substitute.For<IAgentWorkspaceStore>();
        store.GetTargetIdAsync().Returns("target-1");
        using var services = new ServiceCollection().AddSingleton(authorization).AddSingleton(store)
            .AddSingleton<IOperatorScopeAuthorizer, OperatorScopeAuthorizer>().BuildServiceProvider();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal, RequestServices = services } };
        var session = Substitute.For<IDatabaseSession>();
        var sessions = Substitute.For<IDatabaseSessionFactory>();
        sessions.OpenAsync(Arg.Any<CancellationToken>()).Returns(session);
        session.QuerySingleOrDefaultAsync<bool>(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>()).Returns(true);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var authorizer = new DatasetImportDestinationAuthorizer(accessor, configuration, sessions);
        var target = new DatasetImportDestination("public", "roads");
        foreach (var mode in new[] { ImportLoadMode.Append, ImportLoadMode.Replace })
            (await authorizer.IsAuthorizedAsync(principal, "target-1", target, new(mode), true)).Should().BeFalse();
        sessions.ClearReceivedCalls();
        (await authorizer.IsAuthorizedAsync(principal, "target-1", target, new(), false)).Should().BeTrue();
        await sessions.DidNotReceive().OpenAsync(Arg.Any<CancellationToken>());
        (await authorizer.IsAuthorizedAsync(principal, "target-1", target, new(ImportLoadMode.Upsert), true)).Should().BeFalse();
        (await authorizer.IsAuthorizedAsync(principal, "target-1", new("honua", "roads"), new(), true)).Should().BeFalse();
        session.QuerySingleOrDefaultAsync<bool>(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>()).Returns(false);
        (await authorizer.IsAuthorizedAsync(principal, "target-1", target, new(), true)).Should().BeTrue();
        (await authorizer.IsAuthorizedAsync(principal, "target-other", target, new(), true)).Should().BeFalse();
        sessions.ClearReceivedCalls();
        configuration["DataSource:Provider"] = "duckdb";
        (await authorizer.IsAuthorizedAsync(principal, "target-1", target, new(), true)).Should().BeFalse();
        await sessions.DidNotReceive().OpenAsync(Arg.Any<CancellationToken>());
    }
}
