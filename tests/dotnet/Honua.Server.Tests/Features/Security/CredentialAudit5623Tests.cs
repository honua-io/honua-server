// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Security;

// Audit platform-20261006 / issue 5623 outcomes:
// SRV-AUTH-001 -> fixed; SRV_AUTH_001_ScopedApiKeyCannotReadAnUnrelatedAuthOnlyLayer.
// SRV-AUTH-004, SRV-AUTH-006, SRV-AUTH-007, SRV-AUTH-005, SRV-AUTH-010,
// SRV-AUTH-011, SRV-AUTH-012, SRV-AUTH-014, SRV-AUTH-015 -> not attempted;
// higher-severity SRV-AUTH-001 was completed first as the bounded fix unit.
public sealed class CredentialAudit5623Tests
{
    [UnitTest]
    public async Task SRV_AUTH_001_ScopedApiKeyCannotReadAnUnrelatedAuthOnlyLayer()
    {
        var services = new ServiceCollection()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = ScopedKey("write:sandbox/points", LayerScopedWriteKey.Role, LayerScopedWriteKey.AuthType)
        };
        var service = Service("private-service");
        var resource = Resource("sensitive-layer");

        var decision = await AccessPolicyHelpers.EvaluateResourceAccessAsync(
            context, resource, service, AuthorizationOperation.Query);

        decision.IsAllowed.Should().BeFalse();
        decision.RequiresAuthentication.Should().BeFalse();
    }

    [UnitTest]
    public async Task SRV_AUTH_001_ScopedReadGrantOnlyReadsItsNamedLayer()
    {
        var services = new ServiceCollection()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = ScopedKey("read:catalog/roads", LayerScopedWriteKey.ScopedKeyRole, "admin-api-key")
        };

        (await AccessPolicyHelpers.EvaluateResourceAccessAsync(
            context, Resource("roads"), Service("catalog"), AuthorizationOperation.Query))
            .IsAllowed.Should().BeTrue();
        (await AccessPolicyHelpers.EvaluateResourceAccessAsync(
            context, Resource("parcels"), Service("catalog"), AuthorizationOperation.Query))
            .IsAllowed.Should().BeFalse();
    }

    private static ClaimsPrincipal ScopedKey(string permission, string role, string authType) => new(
        new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, role), new Claim("auth_type", authType), new Claim("permission", permission)],
            "ApiKey"));

    private static MetadataV2Service Service(string name) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = name, Name = name }
    };

    private static MetadataV2Resource Resource(string name) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = name, Name = name }
    };
}
