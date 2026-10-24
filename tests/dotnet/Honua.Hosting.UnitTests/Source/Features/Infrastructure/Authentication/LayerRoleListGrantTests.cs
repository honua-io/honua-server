// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Authentication;

/// <summary>
/// Built-in viewer and editor wildcard grants must not skip a layer role list (#6358).
/// </summary>
public sealed class LayerRoleListGrantTests
{
    [UnitTest]
    public async Task ViewerGrant_ExcludedAllowedRoles_IsDenied()
    {
        var decision = await EvaluateAsync(
            ["viewer"],
            new AccessPolicy { AllowedRoles = ["gis-analyst"] },
            servicePolicy: null,
            AuthorizationOperation.Query);

        decision.IsAllowed.Should().BeFalse();
        decision.FailureReason.Should().Be(AccessPolicyEvaluator.RequiredRoleDeniedReason);
    }

    [UnitTest]
    public async Task ViewerGrant_ListedRole_IsAllowed()
    {
        var decision = await EvaluateAsync(
            ["viewer", "gis-analyst"],
            new AccessPolicy { AllowedRoles = ["gis-analyst"] },
            servicePolicy: null,
            AuthorizationOperation.Query);

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task ListedRoleWithoutGrant_IsAllowed()
    {
        var decision = await EvaluateAsync(
            ["gis-analyst"],
            new AccessPolicy { AllowedRoles = ["gis-analyst"] },
            servicePolicy: null,
            AuthorizationOperation.Query);

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task NamedLayerGrant_ExcludedAllowedRoles_IsAllowed()
    {
        var decision = await EvaluateAsync(
            ["parcel-clerk"],
            new AccessPolicy { AllowedRoles = ["gis-analyst"] },
            servicePolicy: null,
            AuthorizationOperation.Query);

        decision.IsAllowed.Should().BeTrue(
            "a grant that names this service and layer is an explicit allow");
    }

    [UnitTest]
    public async Task ViewerGrant_EmptyRoleList_IsAllowed()
    {
        var decision = await EvaluateAsync(
            ["viewer"],
            layerPolicy: null,
            servicePolicy: null,
            AuthorizationOperation.Query);

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task EditorGrant_ExcludedWriteRoles_IsDenied()
    {
        var decision = await EvaluateAsync(
            ["editor"],
            new AccessPolicy { AllowedWriteRoles = ["gis-analyst"] },
            servicePolicy: null,
            AuthorizationOperation.Update);

        decision.IsAllowed.Should().BeFalse();
        decision.FailureReason.Should().Be(AccessPolicyEvaluator.RequiredRoleDeniedReason);
    }

    [UnitTest]
    public async Task EditorGrant_UnspecifiedWriteRoles_UsesAllowedRoles()
    {
        var decision = await EvaluateAsync(
            ["editor"],
            new AccessPolicy { AllowedRoles = ["gis-analyst"] },
            servicePolicy: null,
            AuthorizationOperation.Update);

        decision.IsAllowed.Should().BeFalse();
        decision.FailureReason.Should().Be(AccessPolicyEvaluator.RequiredRoleDeniedReason);
    }

    [UnitTest]
    public async Task EditorGrant_ListedWriteRole_IsAllowed()
    {
        var byWriteList = await EvaluateAsync(
            ["editor"],
            new AccessPolicy { AllowedWriteRoles = ["editor"] },
            servicePolicy: null,
            AuthorizationOperation.Update);
        var byReadList = await EvaluateAsync(
            ["editor"],
            new AccessPolicy { AllowedRoles = ["editor"] },
            servicePolicy: null,
            AuthorizationOperation.Update);

        byWriteList.IsAllowed.Should().BeTrue();
        byReadList.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task EditorGrant_EmptyRoleList_IsAllowed()
    {
        var decision = await EvaluateAsync(
            ["editor"],
            layerPolicy: null,
            servicePolicy: null,
            AuthorizationOperation.Update);

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task AllowAnonymous_StillAllowsWhenViewerIsNotListed()
    {
        var anonymous = await EvaluateAsync(
            roles: [],
            new AccessPolicy { AllowAnonymous = true, AllowedRoles = ["gis-analyst"] },
            servicePolicy: null,
            AuthorizationOperation.Query,
            authenticated: false);
        var viewer = await EvaluateAsync(
            ["viewer"],
            new AccessPolicy { AllowAnonymous = true, AllowedRoles = ["gis-analyst"] },
            servicePolicy: null,
            AuthorizationOperation.Query);

        anonymous.IsAllowed.Should().BeTrue();
        viewer.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task ViewerGrant_CustomForbiddenReason_IsStillDenied()
    {
        await using var provider = BuildProvider(rewriteRoleDenials: true);
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = Principal(["viewer"]),
        };

        var decision = await AccessPolicyHelpers.EvaluateResourceAccessAsync(
            context,
            Resource(new AccessPolicy { AllowedRoles = ["gis-analyst"] }),
            Service(accessPolicy: null),
            AuthorizationOperation.Query);

        decision.IsAllowed.Should().BeFalse();
        decision.FailureReason.Should().Be(ReasonRewritingEvaluator.CustomReason);
    }

    [UnitTest]
    public async Task ServiceGrant_ExcludedAllowedRoles_IsDenied()
    {
        await using var provider = BuildProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = Principal(["viewer"]),
        };
        var service = Service(new AccessPolicy { AllowedRoles = ["gis-analyst"] });

        var decision = await AccessPolicyHelpers.EvaluateResourceAccessAsync(
            context,
            Resource(layerPolicy: null),
            service,
            AuthorizationOperation.Query);
        var serviceResult = await AccessPolicyHelpers.RequireServiceAccessAsync(
            context,
            service,
            AuthorizationOperation.Query);

        decision.IsAllowed.Should().BeFalse();
        decision.FailureReason.Should().Be(AccessPolicyEvaluator.RequiredRoleDeniedReason);
        serviceResult.Should().NotBeNull();
    }

    [UnitTest]
    public async Task ServiceGrant_EmptyRoleList_IsAllowed()
    {
        await using var provider = BuildProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = Principal(["editor"]),
        };
        var service = Service(accessPolicy: null);

        var serviceResult = await AccessPolicyHelpers.RequireServiceAccessAsync(
            context,
            service,
            AuthorizationOperation.Update);

        serviceResult.Should().BeNull();
    }

    private static async Task<AccessDecision> EvaluateAsync(
        string[] roles,
        AccessPolicy? layerPolicy,
        AccessPolicy? servicePolicy,
        AuthorizationOperation operation,
        bool authenticated = true)
    {
        await using var provider = BuildProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            User = Principal(roles, authenticated),
        };
        return await AccessPolicyHelpers.EvaluateResourceAccessAsync(
            context,
            Resource(layerPolicy),
            Service(servicePolicy),
            operation);
    }

    private static ServiceProvider BuildProvider(bool rewriteRoleDenials = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<AccessPolicyEvaluator>();
        if (rewriteRoleDenials)
        {
            services.AddSingleton<IAccessPolicyEvaluator>(sp =>
                new ReasonRewritingEvaluator(sp.GetRequiredService<AccessPolicyEvaluator>()));
        }
        else
        {
            services.AddSingleton<IAccessPolicyEvaluator>(sp => sp.GetRequiredService<AccessPolicyEvaluator>());
        }

        services.AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions()));
        services.AddSingleton<IRoleStore, BuiltInGrantRoleStore>();
        services.AddSingleton<IPermissionResolver>(sp => new PermissionResolver(sp.GetRequiredService<IRoleStore>()));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Replacement evaluator whose forbidden text is not the built-in role-denial sentence.
    /// </summary>
    private sealed class ReasonRewritingEvaluator(IAccessPolicyEvaluator inner) : IAccessPolicyEvaluator
    {
        public const string CustomReason = "policy denied the principal";

        public Task<AccessDecision> EvaluateAsync(ClaimsPrincipal principal, string resource, string action)
            => inner.EvaluateAsync(principal, resource, action);

        public AccessDecision Evaluate(ClaimsPrincipal principal, string resource, string action)
            => inner.Evaluate(principal, resource, action);

        public async Task<AccessDecision> EvaluateAsync(
            ClaimsPrincipal principal,
            AccessPolicy? layerPolicy,
            AccessPolicy? servicePolicy,
            object? scope = null)
            => Rewrite(await inner.EvaluateAsync(principal, layerPolicy, servicePolicy, scope).ConfigureAwait(false));

        public AccessDecision Evaluate(
            ClaimsPrincipal principal,
            AccessPolicy? layerPolicy,
            AccessPolicy? servicePolicy,
            object? scope = null)
            => Rewrite(inner.Evaluate(principal, layerPolicy, servicePolicy, scope));

        private static AccessDecision Rewrite(AccessDecision decision)
            => decision is { IsAllowed: false, RequiresAuthentication: false }
                ? AccessDecision.Forbidden(CustomReason)
                : decision;
    }

    private static ClaimsPrincipal Principal(string[] roles, bool authenticated = true)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "user-1") };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "Test" : null));
    }

    private static MetadataV2Resource Resource(AccessPolicy? layerPolicy)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-1", Name = "parcels" },
            AccessPolicy = layerPolicy,
        };

    private static MetadataV2Service Service(AccessPolicy? accessPolicy)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-1", Name = "cadastre" },
            AccessPolicy = accessPolicy,
        };

    /// <summary>
    /// Mirrors the built-in viewer and editor wildcard grants. The grants stay broad;
    /// the role list is what narrows them.
    /// </summary>
    private sealed class BuiltInGrantRoleStore : IRoleStore
    {
        private static readonly PermissionGrant[] ViewerGrants =
        [
            new() { Service = "*", Layer = "*", Operation = "query" },
            new() { Service = "*", Layer = "*", Operation = "metadata" },
        ];

        private static readonly PermissionGrant[] EditorGrants =
        [
            new() { Service = "*", Layer = "*", Operation = "query" },
            new() { Service = "*", Layer = "*", Operation = "insert" },
            new() { Service = "*", Layer = "*", Operation = "update" },
            new() { Service = "*", Layer = "*", Operation = "delete" },
            new() { Service = "*", Layer = "*", Operation = "export" },
            new() { Service = "*", Layer = "*", Operation = "metadata" },
        ];

        public Task<EffectivePermissions> GetEffectivePermissionsAsync(
            string userId,
            IReadOnlyList<string> roles,
            CancellationToken cancellationToken = default)
        {
            var permissions = new List<PermissionGrant>();
            foreach (var role in roles)
            {
                if (string.Equals(role, "viewer", StringComparison.OrdinalIgnoreCase))
                {
                    permissions.AddRange(ViewerGrants);
                }
                else if (string.Equals(role, "editor", StringComparison.OrdinalIgnoreCase))
                {
                    permissions.AddRange(EditorGrants);
                }
                else if (string.Equals(role, "parcel-clerk", StringComparison.OrdinalIgnoreCase))
                {
                    permissions.Add(new PermissionGrant
                    {
                        Service = "cadastre",
                        Layer = "parcels",
                        Operation = "query",
                    });
                }
            }

            return Task.FromResult(new EffectivePermissions
            {
                UserId = userId,
                Roles = roles,
                Permissions = permissions,
            });
        }

        public Task<IReadOnlyList<RoleDefinition>> ListRolesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RoleDefinition>>([]);

        public Task<RoleDefinition?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
            => Task.FromResult<RoleDefinition?>(null);

        public Task<RoleDefinition> CreateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
            => Task.FromResult(role);

        public Task<RoleDefinition?> UpdateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
            => Task.FromResult<RoleDefinition?>(role);

        public Task<bool> DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<PermissionGrant>> GetPermissionsAsync(Guid roleId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PermissionGrant>>([]);

        public Task<IReadOnlyList<PermissionGrant>> SetPermissionsAsync(
            Guid roleId,
            IReadOnlyList<PermissionGrant> permissions,
            CancellationToken cancellationToken = default)
            => Task.FromResult(permissions);
    }
}
