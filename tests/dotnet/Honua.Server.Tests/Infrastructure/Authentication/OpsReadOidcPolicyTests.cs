// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// With OIDC enabled, OpsRead stays an ops:read policy. It must not be rebuilt as an
/// admin-role policy.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class OpsReadOidcPolicyTests
{
    [UnitTest]
    public async Task OpsReadKey_CanReadAndCannotMutate_AndPolicyStaysOpsRead()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Oidc:Enabled"] = "true" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApiKeyAuthentication(configuration);
        services.AddOidcAuthorization(configuration);
        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        var policy = options.GetPolicy(AuthenticationExtensions.OpsReadPolicy);
        Assert.NotNull(policy);
        Assert.Contains(policy!.Requirements, requirement => requirement is OpsReadRequirement);
        Assert.DoesNotContain(policy.Requirements, requirement => requirement is AdminPermissionRequirement);

        var principal = await AuthenticateOpsReadKeyAsync();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var get = new DefaultHttpContext { RequestServices = provider };
        get.Request.Method = HttpMethods.Get;
        var post = new DefaultHttpContext { RequestServices = provider };
        post.Request.Method = HttpMethods.Post;

        var read = await authorization.AuthorizeAsync(principal, get, AuthenticationExtensions.OpsReadPolicy);
        var mutate = await authorization.AuthorizeAsync(principal, post, AuthenticationExtensions.OpsReadPolicy);

        Assert.True(read.Succeeded);
        Assert.False(mutate.Succeeded);
    }

    private static async Task<System.Security.Claims.ClaimsPrincipal> AuthenticateOpsReadKeyAsync()
    {
        var store = new InMemoryAdminApiKeyStore();
        var created = await store.CreateAsync("ops", ["ops:read"], expiresAt: null, createdBy: "test", CancellationToken.None);
        var dependencies = new ApiKeyAuthenticationDependencies(
            Options.Create(new ApiKeyAuthenticationOptions
            {
                AdminPassword = "unused-admin-password",
                EnvironmentName = "Test",
            }),
            adminApiKeyStore: store);
        var validation = await ApiKeyAuthenticationHandler.ValidateApiKeyAsync(
            created.Key,
            AuthenticationExtensions.ApiKeyScheme,
            dependencies,
            NullLogger.Instance,
            CancellationToken.None);
        Assert.Equal(ApiKeyRejection.None, validation.Rejection);
        return validation.Success!.Principal;
    }
}
