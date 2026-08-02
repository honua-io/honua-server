// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Security;

public sealed class RbacRoleClaimEntitlementTests
{
    private const string CustomRoleClaimType = "groups";
    private const string MappedRole = "mapped-role";

    [UnitTest]
    public async Task OperatorAuthorization_CustomRoleClaim_FollowsLiveEntitlement()
    {
        var entitlements = new MutableLicenseEntitlementService(HonuaEdition.Community);
        using var provider = BuildServices(entitlements);
        var evaluator = provider.GetRequiredService<IOperatorAuthorizationEvaluator>();
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Process,
            Operation = OperatorOperation.Execute,
        };

        (await evaluator.EvaluateAsync(CreatePrincipal(CustomRoleClaimType, MappedRole), request))
            .IsAllowed.Should().BeFalse("raw provider groups are custom claims mapping");
        (await evaluator.EvaluateAsync(CreatePrincipal(ClaimTypes.Role, MappedRole), request))
            .IsAllowed.Should().BeTrue("normalized application roles remain available in every edition");

        entitlements.Apply(HonuaEdition.Enterprise);
        (await evaluator.EvaluateAsync(CreatePrincipal(CustomRoleClaimType, MappedRole), request))
            .IsAllowed.Should().BeTrue("an Enterprise snapshot admits the configured role claim");

        entitlements.Expire();
        (await evaluator.EvaluateAsync(CreatePrincipal(CustomRoleClaimType, MappedRole), request))
            .IsAllowed.Should().BeFalse("expiry must take effect on the next authorization decision");
    }

    [UnitTest]
    public async Task AccessPolicyGrant_CustomRoleClaim_FollowsLiveEntitlement()
    {
        var entitlements = new MutableLicenseEntitlementService(HonuaEdition.Community);
        using var provider = BuildServices(entitlements);
        var context = CreateContext(provider, CreatePrincipal(CustomRoleClaimType, MappedRole));
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-1", Name = "layer-1" },
        };
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-1", Name = "alpha" },
            AccessPolicy = new AccessPolicy { AllowedRoles = ["coarse-only"] },
        };

        (await AccessPolicyHelpers.IsResourceAccessibleAsync(
            context, resource, service, AuthorizationOperation.Query)).Should().BeFalse();

        entitlements.Apply(HonuaEdition.Enterprise);
        (await AccessPolicyHelpers.IsResourceAccessibleAsync(
            context, resource, service, AuthorizationOperation.Query)).Should().BeTrue();

        entitlements.Expire();
        (await AccessPolicyHelpers.IsResourceAccessibleAsync(
            context, resource, service, AuthorizationOperation.Query)).Should().BeFalse();
    }

    [UnitTest]
    public async Task ServiceDataEditor_CustomRoleClaim_FollowsLiveEntitlement()
    {
        var entitlements = new MutableLicenseEntitlementService(HonuaEdition.Community);
        using var provider = BuildServices(entitlements);
        var context = CreateContext(provider, CreatePrincipal(CustomRoleClaimType, MappedRole));

        (await ServiceDataEditorAuthorization.EvaluateServiceAccessAsync(
            context, "alpha", CancellationToken.None)).IsAllowed.Should().BeFalse();

        entitlements.Apply(HonuaEdition.Enterprise);
        (await ServiceDataEditorAuthorization.EvaluateServiceAccessAsync(
            context, "alpha", CancellationToken.None)).IsAllowed.Should().BeTrue();

        entitlements.Expire();
        (await ServiceDataEditorAuthorization.EvaluateServiceAccessAsync(
            context, "alpha", CancellationToken.None)).IsAllowed.Should().BeFalse();
    }

    [UnitTest]
    public async Task RowLevelSecurity_CustomRoleClaim_FollowsLiveEntitlement()
    {
        var entitlements = new MutableLicenseEntitlementService(HonuaEdition.Community);
        using var provider = BuildServices(entitlements);
        var context = CreateContext(provider, CreatePrincipal(CustomRoleClaimType, MappedRole));
        var policyStore = new RecordingRlsPolicyStore();
        var source = new RowLevelSecurityFilterSource(
            new HttpContextAccessor { HttpContext = context },
            policyStore,
            new ThrowingGraphProvider(),
            Substitute.For<Honua.Core.Queries.Filters.IFilterExpressionService>(),
            provider.GetRequiredService<IOptions<RbacOptions>>(),
            NullLogger<RowLevelSecurityFilterSource>.Instance);
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-1", Name = "layer-1" },
        };

        await source.ResolveAsync(resource);
        policyStore.LastRoles.Should().NotContain(MappedRole);

        entitlements.Apply(HonuaEdition.Enterprise);
        await source.ResolveAsync(resource);
        policyStore.LastRoles.Should().Contain(MappedRole);

        entitlements.Expire();
        await source.ResolveAsync(resource);
        policyStore.LastRoles.Should().NotContain(MappedRole);
    }

    private static ServiceProvider BuildServices(MutableLicenseEntitlementService entitlements)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ILicenseEntitlementService>(entitlements);
        services.AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions
        {
            RoleClaimType = CustomRoleClaimType,
            DataEditorRoles = [MappedRole],
        }));
        services.AddSingleton<IRoleStore, GrantingRoleStore>();
        services.AddSingleton<IPermissionResolver>(sp =>
            new PermissionResolver(sp.GetRequiredService<IRoleStore>()));
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.AddSingleton<IOperatorAuthorizationEvaluator, OperatorAuthorizationEvaluator>();
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext CreateContext(IServiceProvider provider, ClaimsPrincipal principal)
        => new()
        {
            RequestServices = provider,
            User = principal,
        };

    private static ClaimsPrincipal CreatePrincipal(string roleClaimType, string role)
        => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim(roleClaimType, role),
        ], "Test"));

    private sealed class MutableLicenseEntitlementService(HonuaEdition edition) : ILicenseEntitlementService
    {
        private LicenseSnapshot _snapshot = LicenseTestSupport.CreateSnapshot(edition);

        public void Apply(HonuaEdition updatedEdition)
            => _snapshot = LicenseTestSupport.CreateSnapshot(updatedEdition);

        public void Expire()
            => _snapshot = LicenseTestSupport.CreateSnapshot(
                HonuaEdition.Community,
                LicenseValidationState.Expired,
                entitlements: []);

        public LicenseSnapshot GetSnapshot() => _snapshot;

        public LicenseEntitlementDecision CheckEntitlement(string entitlementKey)
        {
            var active = _snapshot.HasEntitlement(entitlementKey);
            return new LicenseEntitlementDecision(
                entitlementKey,
                active,
                _snapshot.Edition,
                _snapshot.ValidationState,
                RequiredEdition: null,
                UpgradeMessage: active ? string.Empty : $"'{entitlementKey}' is not active.");
        }
    }

    private sealed class GrantingRoleStore : IRoleStore
    {
        public Task<EffectivePermissions> GetEffectivePermissionsAsync(
            string userId,
            IReadOnlyList<string> roles,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new EffectivePermissions
            {
                UserId = userId,
                Roles = roles,
                Permissions = roles.Contains(MappedRole, StringComparer.OrdinalIgnoreCase)
                    ?
                    [
                        new PermissionGrant { Service = "process", Layer = "*", Operation = "execute" },
                        new PermissionGrant { Service = "alpha", Layer = "*", Operation = "query" },
                    ]
                    : [],
            });

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

    private sealed class RecordingRlsPolicyStore : IRlsPolicyStore
    {
        public IReadOnlyList<string> LastRoles { get; private set; } = [];

        public Task<IReadOnlyList<RlsPolicy>> GetEffectivePoliciesAsync(
            IReadOnlyList<string> roles,
            string service,
            string layer,
            CancellationToken cancellationToken = default)
        {
            LastRoles = roles;
            return Task.FromResult<IReadOnlyList<RlsPolicy>>([]);
        }

        public Task<IReadOnlyList<RlsPolicy>> ListPoliciesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RlsPolicy>>([]);

        public Task<RlsPolicy?> GetPolicyAsync(Guid policyId, CancellationToken cancellationToken = default)
            => Task.FromResult<RlsPolicy?>(null);

        public Task<RlsPolicy> CreatePolicyAsync(RlsPolicy policy, CancellationToken cancellationToken = default)
            => Task.FromResult(policy);

        public Task<bool> DeletePolicyAsync(Guid policyId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class ThrowingGraphProvider : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<MetadataV2GraphSnapshot>(new InvalidOperationException("No graph needed."));

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
            long revision,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(null);
    }
}
