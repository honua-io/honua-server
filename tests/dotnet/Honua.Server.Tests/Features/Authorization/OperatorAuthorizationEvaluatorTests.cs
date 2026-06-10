using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Authorization;

public sealed class OperatorAuthorizationEvaluatorTests
{
    private readonly TestRoleStore _roleStore = new();
    private readonly OperatorAuthorizationEvaluator _evaluator;

    public OperatorAuthorizationEvaluatorTests()
    {
        var rbacOptions = Options.Create(new RbacOptions { RoleClaimType = "roles" });
        _evaluator = new OperatorAuthorizationEvaluator(
            _roleStore,
            rbacOptions,
            NullLogger<OperatorAuthorizationEvaluator>.Instance);
    }

    [UnitTest]
    public async Task Evaluate_UnauthenticatedPrincipal_RequiresAuth()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Process,
            Operation = OperatorOperation.Execute
        };

        var decision = await _evaluator.EvaluateAsync(principal, request);

        decision.IsAllowed.Should().BeFalse();
        decision.RequiresAuthentication.Should().BeTrue();
    }

    [UnitTest]
    public async Task Evaluate_AdminRole_AllowedForAnyResource()
    {
        var principal = CreatePrincipal("user-1", "admin");
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Deployment,
            Operation = OperatorOperation.Publish
        };

        var decision = await _evaluator.EvaluateAsync(principal, request);

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task Evaluate_AdminRoleCaseInsensitive_AllowedForAnyResource()
    {
        var principal = CreatePrincipal("user-1", "Admin");
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Package,
            Operation = OperatorOperation.Create
        };

        var decision = await _evaluator.EvaluateAsync(principal, request);

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task Evaluate_PersonalWorkspace_OwnerAllowed()
    {
        _roleStore.AddGrant("operator", "workspace", "*", "read");
        var principal = CreatePrincipal("user-1", "operator");
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Workspace,
            Operation = OperatorOperation.Read,
            WorkspaceVisibility = WorkspaceVisibility.Personal,
            WorkspaceOwnerId = "user-1"
        };

        var decision = await _evaluator.EvaluateAsync(principal, request);

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task Evaluate_PersonalWorkspace_NonOwnerDenied()
    {
        _roleStore.AddGrant("operator", "workspace", "*", "read");
        var principal = CreatePrincipal("user-2", "operator");
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Workspace,
            Operation = OperatorOperation.Read,
            WorkspaceVisibility = WorkspaceVisibility.Personal,
            WorkspaceOwnerId = "user-1"
        };

        var decision = await _evaluator.EvaluateAsync(principal, request);

        decision.IsAllowed.Should().BeFalse();
        decision.RequiresAuthentication.Should().BeFalse();
    }

    [UnitTest]
    public async Task Evaluate_PersonalWorkspace_MissingOwnerDenied()
    {
        _roleStore.AddGrant("operator", "workspace", "*", "read");
        var principal = CreatePrincipal("user-1", "operator");
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Workspace,
            Operation = OperatorOperation.Read,
            WorkspaceVisibility = WorkspaceVisibility.Personal,
            WorkspaceOwnerId = null
        };

        var decision = await _evaluator.EvaluateAsync(principal, request);

        decision.IsAllowed.Should().BeFalse();
        decision.RequiresAuthentication.Should().BeFalse();
    }

    [UnitTest]
    public async Task Evaluate_WildcardGrant_MatchesAllResourceTypes()
    {
        _roleStore.AddGrant("viewer", "*", "*", "read");
        var principal = CreatePrincipal("user-1", "viewer");

        foreach (var resourceType in Enum.GetValues<OperatorResourceType>())
        {
            var request = new OperatorAuthorizationRequest
            {
                ResourceType = resourceType,
                Operation = OperatorOperation.Read,
                WorkspaceVisibility = resourceType == OperatorResourceType.Workspace
                    ? WorkspaceVisibility.Organization
                    : null
            };

            (await _evaluator.EvaluateAsync(principal, request)).IsAllowed.Should().BeTrue(
                $"wildcard grant should match {resourceType}.Read");
        }
    }

    [UnitTest]
    public async Task Evaluate_SpecificResourceTypeGrant_MatchesOnly()
    {
        _roleStore.AddGrant("executor", "process", "*", "execute");
        var principal = CreatePrincipal("user-1", "executor");

        var allowed = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Process,
            Operation = OperatorOperation.Execute
        };
        (await _evaluator.EvaluateAsync(principal, allowed)).IsAllowed.Should().BeTrue();

        var denied = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Deployment,
            Operation = OperatorOperation.Execute
        };
        (await _evaluator.EvaluateAsync(principal, denied)).IsAllowed.Should().BeFalse();
    }

    [UnitTest]
    public async Task Evaluate_SpecificResourceId_MatchesExact()
    {
        _roleStore.AddGrant("scoped", "process", "proc-42", "execute");
        var principal = CreatePrincipal("user-1", "scoped");

        var matchingRequest = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Process,
            ResourceId = "proc-42",
            Operation = OperatorOperation.Execute
        };
        (await _evaluator.EvaluateAsync(principal, matchingRequest)).IsAllowed.Should().BeTrue();

        var nonMatchingRequest = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Process,
            ResourceId = "proc-99",
            Operation = OperatorOperation.Execute
        };
        (await _evaluator.EvaluateAsync(principal, nonMatchingRequest)).IsAllowed.Should().BeFalse();
    }

    [UnitTest]
    public async Task Evaluate_NullResourceId_ScopedGrant_Denied()
    {
        _roleStore.AddGrant("scoped", "process", "proc-42", "execute");
        var principal = CreatePrincipal("user-1", "scoped");

        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Process,
            ResourceId = null,
            Operation = OperatorOperation.Execute
        };

        (await _evaluator.EvaluateAsync(principal, request)).IsAllowed.Should().BeFalse();
    }

    [UnitTest]
    public async Task Evaluate_NullResourceId_WildcardGrant_Allowed()
    {
        _roleStore.AddGrant("executor", "process", "*", "execute");
        var principal = CreatePrincipal("user-1", "executor");

        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Process,
            ResourceId = null,
            Operation = OperatorOperation.Execute
        };

        (await _evaluator.EvaluateAsync(principal, request)).IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task Evaluate_NoRoles_Forbidden()
    {
        var principal = CreatePrincipal("user-1");
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Catalog,
            Operation = OperatorOperation.Discover
        };

        var decision = await _evaluator.EvaluateAsync(principal, request);

        decision.IsAllowed.Should().BeFalse();
        decision.RequiresAuthentication.Should().BeFalse();
    }

    [UnitTest]
    public async Task Evaluate_UnrecognizedServiceString_DeniesGracefully()
    {
        _roleStore.AddGrant("tester", "unknown-service", "*", "read");
        var principal = CreatePrincipal("user-1", "tester");

        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Catalog,
            Operation = OperatorOperation.Read
        };

        (await _evaluator.EvaluateAsync(principal, request)).IsAllowed.Should().BeFalse();
    }

    [UnitTest]
    public async Task Evaluate_WildcardOperationGrant_MatchesAllOperations()
    {
        _roleStore.AddGrant("full-process", "process", "*", "*");
        var principal = CreatePrincipal("user-1", "full-process");

        foreach (var op in Enum.GetValues<OperatorOperation>())
        {
            var request = new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Process,
                Operation = op
            };

            (await _evaluator.EvaluateAsync(principal, request)).IsAllowed.Should().BeTrue(
                $"wildcard operation grant should match Process.{op}");
        }
    }

    [UnitTest]
    public async Task Evaluate_CaseInsensitiveConventionMapping_Works()
    {
        _roleStore.AddGrant("caser", "Catalog", "*", "Discover");
        var principal = CreatePrincipal("user-1", "caser");

        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Catalog,
            Operation = OperatorOperation.Discover
        };

        (await _evaluator.EvaluateAsync(principal, request)).IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task Evaluate_MultipleRoles_CombinesPermissions()
    {
        _roleStore.AddGrant("reader", "catalog", "*", "read");
        _roleStore.AddGrant("executor", "process", "*", "execute");
        var principal = CreatePrincipal("user-1", "reader", "executor");

        (await _evaluator.EvaluateAsync(principal, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Catalog,
            Operation = OperatorOperation.Read
        })).IsAllowed.Should().BeTrue();

        (await _evaluator.EvaluateAsync(principal, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Process,
            Operation = OperatorOperation.Execute
        })).IsAllowed.Should().BeTrue();

        (await _evaluator.EvaluateAsync(principal, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Deployment,
            Operation = OperatorOperation.Publish
        })).IsAllowed.Should().BeFalse();
    }

    [UnitTest]
    public async Task Evaluate_StandardRoleClaim_AdminBypassed()
    {
        var principal = CreatePrincipalWithStandardRoleClaim("user-1", "admin");
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Deployment,
            Operation = OperatorOperation.Publish
        };

        (await _evaluator.EvaluateAsync(principal, request)).IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task Evaluate_StandardRoleClaim_GrantMatched()
    {
        _roleStore.AddGrant("operator", "process", "*", "execute");
        var principal = CreatePrincipalWithStandardRoleClaim("user-1", "operator");
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Process,
            Operation = OperatorOperation.Execute
        };

        (await _evaluator.EvaluateAsync(principal, request)).IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task Evaluate_MixedRoleClaims_CombinesPermissions()
    {
        _roleStore.AddGrant("reader", "catalog", "*", "read");
        _roleStore.AddGrant("executor", "process", "*", "execute");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "user-1"),
            new("roles", "reader"),
            new(ClaimTypes.Role, "executor")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));

        (await _evaluator.EvaluateAsync(principal, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Catalog,
            Operation = OperatorOperation.Read
        })).IsAllowed.Should().BeTrue();

        (await _evaluator.EvaluateAsync(principal, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Process,
            Operation = OperatorOperation.Execute
        })).IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task Evaluate_PublicWorkspaceRead_UnauthenticatedAllowed()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Workspace,
            Operation = OperatorOperation.Read,
            WorkspaceVisibility = WorkspaceVisibility.Public,
            ResourceId = "ws-public-1"
        };

        var decision = await _evaluator.EvaluateAsync(principal, request);

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task Evaluate_PublicWorkspaceDiscover_UnauthenticatedAllowed()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Workspace,
            Operation = OperatorOperation.Discover,
            WorkspaceVisibility = WorkspaceVisibility.Public
        };

        var decision = await _evaluator.EvaluateAsync(principal, request);

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task Evaluate_PublicWorkspaceCreate_UnauthenticatedRequiresAuth()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Workspace,
            Operation = OperatorOperation.Create,
            WorkspaceVisibility = WorkspaceVisibility.Public
        };

        var decision = await _evaluator.EvaluateAsync(principal, request);

        decision.IsAllowed.Should().BeFalse();
        decision.RequiresAuthentication.Should().BeTrue();
    }

    [Theory]
    [InlineData(OperatorOperation.Create)]
    [InlineData(OperatorOperation.Execute)]
    [InlineData(OperatorOperation.Promote)]
    [InlineData(OperatorOperation.Publish)]
    public async Task Evaluate_PublicWorkspaceMutation_AuthenticatedWithGrant_Denied(OperatorOperation operation)
    {
        _roleStore.AddGrant("operator", "workspace", "*", "*");
        var principal = CreatePrincipal("user-1", "operator");
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Workspace,
            Operation = operation,
            WorkspaceVisibility = WorkspaceVisibility.Public,
            ResourceId = "ws-public-1"
        };

        var decision = await _evaluator.EvaluateAsync(principal, request);

        decision.IsAllowed.Should().BeFalse();
        decision.RequiresAuthentication.Should().BeFalse();
    }

    [UnitTest]
    public async Task Evaluate_SharedWorkspace_MatchingScope_Allowed()
    {
        _roleStore.AddGrant("operator", "workspace", "*", "read");
        var principal = CreatePrincipalWithScopeClaim("user-1", "team-alpha", "operator");
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Workspace,
            Operation = OperatorOperation.Read,
            WorkspaceVisibility = WorkspaceVisibility.Shared,
            WorkspaceScopeId = "team-alpha"
        };

        var decision = await _evaluator.EvaluateAsync(principal, request);

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task Evaluate_SharedWorkspace_NonMatchingScope_Denied()
    {
        _roleStore.AddGrant("operator", "workspace", "*", "read");
        var principal = CreatePrincipalWithScopeClaim("user-1", "team-alpha", "operator");
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Workspace,
            Operation = OperatorOperation.Read,
            WorkspaceVisibility = WorkspaceVisibility.Shared,
            WorkspaceScopeId = "team-beta"
        };

        var decision = await _evaluator.EvaluateAsync(principal, request);

        decision.IsAllowed.Should().BeFalse();
        decision.RequiresAuthentication.Should().BeFalse();
    }

    [UnitTest]
    public async Task Evaluate_SharedWorkspace_MissingScopeId_Denied()
    {
        _roleStore.AddGrant("operator", "workspace", "*", "read");
        var principal = CreatePrincipalWithScopeClaim("user-1", "team-alpha", "operator");
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Workspace,
            Operation = OperatorOperation.Read,
            WorkspaceVisibility = WorkspaceVisibility.Shared,
            WorkspaceScopeId = null
        };

        var decision = await _evaluator.EvaluateAsync(principal, request);

        decision.IsAllowed.Should().BeFalse();
        decision.RequiresAuthentication.Should().BeFalse();
    }

    [UnitTest]
    public async Task Evaluate_Workspace_MissingVisibility_Denied()
    {
        _roleStore.AddGrant("operator", "workspace", "*", "read");
        var principal = CreatePrincipal("user-1", "operator");
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Workspace,
            Operation = OperatorOperation.Read,
            WorkspaceVisibility = null
        };

        var decision = await _evaluator.EvaluateAsync(principal, request);

        decision.IsAllowed.Should().BeFalse();
        decision.RequiresAuthentication.Should().BeFalse();
    }

    [UnitTest]
    public async Task Evaluate_OrganizationWorkspace_AuthenticatedWithGrant_Allowed()
    {
        _roleStore.AddGrant("operator", "workspace", "*", "read");
        var principal = CreatePrincipal("user-1", "operator");
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Workspace,
            Operation = OperatorOperation.Read,
            WorkspaceVisibility = WorkspaceVisibility.Organization
        };

        var decision = await _evaluator.EvaluateAsync(principal, request);

        decision.IsAllowed.Should().BeTrue();
    }

    private static ClaimsPrincipal CreatePrincipalWithScopeClaim(string userId, string scopeGroup, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new("groups", scopeGroup)
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim("roles", role));
        }

        var identity = new ClaimsIdentity(claims, "TestScheme");
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal CreatePrincipal(string userId, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId)
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim("roles", role));
        }

        var identity = new ClaimsIdentity(claims, "TestScheme");
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal CreatePrincipalWithStandardRoleClaim(string userId, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId)
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(claims, "TestScheme");
        return new ClaimsPrincipal(identity);
    }

    private sealed class TestRoleStore : IRoleStore
    {
        private readonly Dictionary<string, List<PermissionGrant>> _roleGrants = new(StringComparer.OrdinalIgnoreCase);

        public void AddGrant(string roleName, string service, string layer, string operation)
        {
            if (!_roleGrants.TryGetValue(roleName, out var grants))
            {
                grants = [];
                _roleGrants[roleName] = grants;
            }

            grants.Add(new PermissionGrant { Service = service, Layer = layer, Operation = operation });
        }

        public Task<EffectivePermissions> GetEffectivePermissionsAsync(
            string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken = default)
        {
            var permissions = roles
                .Where(r => _roleGrants.ContainsKey(r))
                .SelectMany(r => _roleGrants[r])
                .ToList();

            return Task.FromResult(new EffectivePermissions
            {
                UserId = userId,
                Roles = roles,
                Permissions = permissions
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
            Guid roleId, IReadOnlyList<PermissionGrant> permissions, CancellationToken cancellationToken = default)
            => Task.FromResult(permissions);
    }
}
