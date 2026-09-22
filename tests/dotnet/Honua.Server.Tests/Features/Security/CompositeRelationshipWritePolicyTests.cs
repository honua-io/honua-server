// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Validation;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Honua.Server.Tests.Features.Security;

/// <summary>Exercises the shared HTTP/non-HTTP edit decision seams before grants.</summary>
public sealed class CompositeRelationshipWritePolicyTests
{
    [UnitTest]
    public async Task CompositeResources_RemainReadableButDenyEveryWriteDespiteWildcardGrant()
    {
        var roles = new Mock<IRoleStore>();
        roles.Setup(store => store.GetEffectivePermissionsAsync(It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectivePermissions
            {
                UserId = "operator",
                Roles = ["grant-role"],
                Permissions = [new PermissionGrant { Service = "*", Layer = "*", Operation = "*" }]
            });
        var services = new ServiceCollection();
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.Configure<RbacOptions>(options => options.RoleClaimType = "roles");
        services.AddSingleton(roles.Object);
        services.AddSingleton<IPermissionResolver, PermissionResolver>();
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "operator"), new Claim("roles", "grant-role")], "Test"))
        };
        var service = new MetadataV2Service { Metadata = new MetadataV2ObjectMetadata { Id = "svc", Name = "Summary" } };
        var resources = new[] { "esriRelRoleOrigin", "esriRelRoleDestination" }
            .Select(role => new MetadataV2Resource
            {
                Metadata = new MetadataV2ObjectMetadata { Id = role, Name = role },
                Relationships = [new MetadataV2Relationship { Id = "summary", Composite = true, Role = role }]
            });
        foreach (var resource in resources)
        {
            var read = await AccessPolicyHelpers.EvaluateResourceAccessAsync(context, resource, service, AuthorizationOperation.Query);
            read.IsAllowed.Should().BeTrue();
            foreach (var operation in new[] { AuthorizationOperation.Insert, AuthorizationOperation.Update, AuthorizationOperation.Delete })
            {
                var unbound = resource with { Relationships = [] };
                (await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(context, unbound, service, operation))
                    .Should().BeNull("the wildcard grant must authorize ordinary writes in this fixture");
                var access = await AccessPolicyHelpers.EvaluateResourceAccessAsync(context, resource, service, operation);
                access.IsAllowed.Should().BeFalse();
                (await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(context, resource, service, operation))
                    .Should().NotBeNull();
            }
            AccessPolicyHelpers.RequireResourceAccess(context, resource, service, AccessScope.Write).Should().NotBeNull();
            (await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(context, resource, service)).Should().NotBeNull();
        }
    }
}
