// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin.Services;
using Honua.Server.Features.AgentWorkspace;
using Honua.Server.Features.Studio;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class AgentWorkspaceAuthorizationTests
{
    [UnitTest]
    public async Task ResolveCallerAsync_ViewerRoleOnExecuteCustomCode_IsDeniedWithForbidden()
    {
        var roleStore = new InMemoryRoleStore();
        await using var provider = BuildServices(roleStore);
        var authorization = CreateAuthorization(provider);
        var context = BuildContext(
            provider,
            CreateInteractivePrincipal("viewer-1", "viewer"),
            OperatorResourceType.Process,
            OperatorOperation.ExecuteCustomCode);

        var act = () => authorization.ResolveCallerAsync(context);

        (await act.Should().ThrowAsync<LocalProcessWorkspaceException>())
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [UnitTest]
    public async Task ResolveCallerAsync_AuthorizedOperatorOnExecuteCustomCode_Succeeds()
    {
        var roleStore = new InMemoryRoleStore();
        await roleStore.CreateRoleAsync(new RoleDefinition
        {
            RoleId = Guid.NewGuid(),
            Name = "custom-code-operator",
            Description = "Permitted to execute custom code",
            Permissions = [new PermissionGrant { Service = "process", Layer = "*", Operation = "ExecuteCustomCode" }]
        });
        await using var provider = BuildServices(roleStore);
        var authorization = CreateAuthorization(provider);
        var context = BuildContext(
            provider,
            CreateInteractivePrincipal("operator-1", "custom-code-operator"),
            OperatorResourceType.Process,
            OperatorOperation.ExecuteCustomCode);

        var access = await authorization.ResolveCallerAsync(context);

        access.ActorId.Should().Contain("operator-1");
        access.TargetId.Should().Be("studio-env-test");
    }

    [UnitTest]
    public async Task ResolveCallerAsync_AdminRoleOnExecuteCustomCode_Succeeds()
    {
        var roleStore = new InMemoryRoleStore();
        await using var provider = BuildServices(roleStore);
        var authorization = CreateAuthorization(provider);
        var context = BuildContext(
            provider,
            CreateInteractivePrincipal("admin-1", "admin"),
            OperatorResourceType.Process,
            OperatorOperation.ExecuteCustomCode);

        var access = await authorization.ResolveCallerAsync(context);

        access.ActorId.Should().Contain("admin-1");
    }

    private static ServiceProvider BuildServices(IRoleStore roleStore)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(roleStore);
        services.AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions { RoleClaimType = "roles" }));
        services.AddSingleton<IOperatorAuthorizationEvaluator, OperatorAuthorizationEvaluator>();
        return services.BuildServiceProvider();
    }

    private static AgentWorkspaceAuthorization CreateAuthorization(IServiceProvider provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Studio:EndUserAuthorization:Enabled"] = "true",
                [StudioSessionEndpoints.EnvironmentIdKey] = "studio-env-test",
            })
            .Build();
        var store = new AgentWorkspaceIdentityStore(database: null);
        return new AgentWorkspaceAuthorization(store, configuration, new OperatorScopeAuthorizer());
    }

    private static DefaultHttpContext BuildContext(
        IServiceProvider provider,
        ClaimsPrincipal user,
        OperatorResourceType resourceType,
        OperatorOperation operation)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = user,
        };
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new AgentWorkspaceScope(resourceType, operation)),
            "local-process-run"));
        return context;
    }

    private static ClaimsPrincipal CreateInteractivePrincipal(string subject, string role)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, subject),
            new Claim("sub", subject),
            new Claim("iss", "https://idp.example.com"),
            new Claim("amr", "mfa"),
            new Claim("roles", role),
            new Claim("scope", OperatorScopeCatalog.Full),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Oidc"));
    }
}
