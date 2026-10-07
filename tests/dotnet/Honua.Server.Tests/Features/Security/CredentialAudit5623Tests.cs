// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Validation;
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

    [UnitTheory]
    [InlineData("admin:read", AdminApiKeyPermission.ScopedAdminRole)]
    [InlineData("admin:approve", AdminApiKeyPermission.ScopedAdminRole)]
    [InlineData("admin:operation:deploy", AdminApiKeyPermission.ApprovedOperationRole)]
    [InlineData("ops:read", LayerScopedWriteKey.ScopedKeyRole)]
    public async Task ScopedAdministrativeKeys_DoNotGrantResourceAccess(string permission, string role)
    {
        await using var services = new ServiceCollection()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>().BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = ScopedKey(permission, role, "admin-api-key")
        };
        var service = Service("private-service");
        var resource = Resource("sensitive-layer");
        AccessPolicyHelpers.EvaluateAccess(context, null, null).IsAllowed.Should().BeFalse();
        AccessPolicyHelpers.RequireResourceAccess(context, resource, service).Should().NotBeNull();
        AccessPolicyHelpers.RequireServiceAccess(context, service).Should().NotBeNull();
        foreach (var operation in new[] { AuthorizationOperation.Query, AuthorizationOperation.Metadata, AuthorizationOperation.Export })
        {
            (await AccessPolicyHelpers.EvaluateResourceAccessAsync(context, resource, service, operation))
                .IsAllowed.Should().BeFalse();
            (await AccessPolicyHelpers.RequireServiceAccessAsync(context, service, operation)).Should().NotBeNull();
        }
        ((ClaimsIdentity)context.User.Identity!).AddClaim(new Claim("permission", "read:private-service/sensitive-layer"));
        AccessPolicyHelpers.RequireResourceAccess(context, resource, service).Should().BeNull();
        (await AccessPolicyHelpers.RequireResourceAccessAsync(context, resource, service)).Should().BeNull();
    }

    [UnitTheory]
    [InlineData("read:catalog/roads", "catalog", "roads", AccessScope.Read, true)]
    [InlineData("read:catalog/roads", "catalog", "parcels", AccessScope.Read, false)]
    [InlineData("read:catalog/roads", "other", "roads", AccessScope.Read, false)]
    [InlineData("read:catalog/roads", "catalog", "roads", AccessScope.Write, false)]
    [InlineData("read:catalog", "catalog", "roads", AccessScope.Read, true)]
    [InlineData("write:catalog/roads", "catalog", "roads", AccessScope.Write, true)]
    [InlineData("write:catalog", "catalog", "roads", AccessScope.Write, true)]
    public async Task ScopedGrants_AgreeAcrossResourceAccessPaths(
        string permission, string serviceName, string layerName, AccessScope scope, bool allowed)
    {
        await using var services = new ServiceCollection()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>().BuildServiceProvider();
        var writeKey = permission.StartsWith("write:", StringComparison.Ordinal);
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = ScopedKey(permission, writeKey ? LayerScopedWriteKey.Role : LayerScopedWriteKey.ScopedKeyRole,
                writeKey ? LayerScopedWriteKey.AuthType : "admin-api-key")
        };
        var service = Service(serviceName);
        var resource = Resource(layerName);
        AccessPolicyHelpers.EvaluateResourceAccess(context, resource, service, scope).IsAllowed.Should().Be(allowed);
        (AccessPolicyHelpers.RequireResourceAccess(context, resource, service, scope) is null).Should().Be(allowed);
        (await AccessPolicyHelpers.RequireResourceAccessAsync(context, resource, service, scope) is null).Should().Be(allowed);
        foreach (var operation in new[] { AuthorizationOperation.Admin, AuthorizationOperation.Approve })
        {
            (await AccessPolicyHelpers.EvaluateResourceAccessAsync(context, resource, service, operation))
                .IsAllowed.Should().BeFalse();
            (await AccessPolicyHelpers.RequireServiceAccessAsync(context, service, operation)).Should().NotBeNull();
        }
    }

    [UnitTheory]
    [InlineData("read:catalog", "catalog", true)]
    [InlineData("read:catalog", "other", false)]
    [InlineData("read:catalog/roads", "catalog", false)]
    public async Task ScopedReadGrants_ServiceAccessRequiresServiceWideGrant(string permission, string serviceName, bool allowed)
    {
        await using var services = new ServiceCollection()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>().BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = ScopedKey(permission, LayerScopedWriteKey.ScopedKeyRole, "admin-api-key")
        };
        var service = Service(serviceName);
        (AccessPolicyHelpers.RequireServiceAccess(context, service) is null).Should().Be(allowed);
        foreach (var operation in new[] { AuthorizationOperation.Query, AuthorizationOperation.Read, AuthorizationOperation.Metadata, AuthorizationOperation.Export })
        {
            (await AccessPolicyHelpers.RequireServiceAccessAsync(context, service, operation) is null).Should().Be(allowed);
            (await AccessPolicyHelpers.RequireResourceAccessAsync(context, Resource("roads"), operation, service) is null)
                .Should().Be(serviceName == "catalog");
        }
        (await AccessPolicyHelpers.RequireServiceAccessAsync(context, service, AuthorizationOperation.Update)).Should().NotBeNull();
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
