// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Validation;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// A scoped API key must not read every auth-only layer. Grants on the key are the
/// authority, including when the layer has no access policy.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class ScopedApiKeyResourceAccessTests
{
    [UnitTest]
    public async Task WriteScopedKey_CannotQueryAnUnscopedLayer()
    {
        var principal = await AuthenticateAsync(["write:svcA/layer1"]);
        var context = ContextFor(principal);

        var ownLayer = AccessPolicyHelpers.IsResourceAccessible(
            context, Resource("layer1"), Service("svcA"), AccessScope.Read);
        var otherLayer = AccessPolicyHelpers.IsResourceAccessible(
            context, Resource("layer2"), Service("svcB"), AccessScope.Read);
        var coarse = new AccessPolicyEvaluator().Evaluate(principal, layerPolicy: null, servicePolicy: null);

        Assert.False(ownLayer);
        Assert.False(otherLayer);
        Assert.False(coarse.IsAllowed);
    }

    [UnitTest]
    public async Task ReadGrant_AuthorizesOnlyTheNamedLayer()
    {
        var principal = await AuthenticateAsync(["read:svcA/layer1"]);
        var context = ContextFor(principal);

        Assert.True(AccessPolicyHelpers.IsResourceAccessible(
            context, Resource("layer1"), Service("svcA"), AccessScope.Read));
        Assert.False(AccessPolicyHelpers.IsResourceAccessible(
            context, Resource("layer2"), Service("svcB"), AccessScope.Read));
    }

    [UnitTheory]
    [InlineData("metadata:read")]
    [InlineData("ops:read")]
    public async Task NonReadGrant_CannotQueryAnAuthOnlyLayer(string grant)
    {
        var principal = await AuthenticateAsync([grant]);
        var context = ContextFor(principal);

        Assert.False(AccessPolicyHelpers.IsResourceAccessible(
            context, Resource("layer2"), Service("svcB"), AccessScope.Read));
    }

    [UnitTest]
    public void AnonymousCaller_CannotListAnAuthOnlyLayer()
    {
        using var services = new ServiceCollection()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };

        Assert.False(AccessPolicyHelpers.AllowsAnonymousResourceAccess(context, Resource("layer2"), Service("svcB")));
    }

    private static DefaultHttpContext ContextFor(ClaimsPrincipal principal) => new() { User = principal };

    private static async Task<ClaimsPrincipal> AuthenticateAsync(IReadOnlyList<string> permissions)
    {
        var store = new InMemoryAdminApiKeyStore();
        var created = await store.CreateAsync("scoped", permissions, expiresAt: null, createdBy: "test", CancellationToken.None);
        var dependencies = new ApiKeyAuthenticationDependencies(
            Options.Create(new ApiKeyAuthenticationOptions
            {
                AdminPassword = "unused-admin-password",
                EnvironmentName = "Test",
            }),
            adminApiKeyStore: store);
        var validation = await ApiKeyAuthenticationHandler.ValidateApiKeyAsync(
            created.Key,
            "ApiKey",
            dependencies,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(ApiKeyRejection.None, validation.Rejection);
        return validation.Success?.Principal
            ?? throw new InvalidOperationException("API key validation did not produce a principal.");
    }

    private static MetadataV2Service Service(string name) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = name, Name = name },
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
    };

    private static MetadataV2Resource Resource(string name) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = name, Name = name },
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
    };
}
