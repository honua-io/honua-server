// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Security;

/// <summary>
/// Builds the PostGIS-backed OGC-classic host the authorization proofs in this folder need.
/// <para>
/// Every other fixture in this test project runs on the stock <see cref="WebAppFixture"/>,
/// whose <c>ApplyCommonHostSettings</c> sets <c>HONUA_DEV_AUTH=true</c> plus
/// <c>HONUA_DEV_AUTH_ALLOW_BYPASS=true</c>. The API-key handler then authenticates every
/// request as <c>dev-bypass</c> before it reads a credential, so no denial path in the whole
/// project is reachable. This fixture turns both off, registers
/// <see cref="TestAuthHandler"/> so a test can post as a named non-admin principal carrying
/// exactly the roles it wants, and swaps in a role store that grants one WFS-T operation at a
/// time — the combination the WFS-T seam tests in <c>CrossProtocolPermissionMatrixTests</c>
/// documented as unavailable ("it needs a PostGIS-backed store and non-admin principal
/// injection").
/// </para>
/// </summary>
internal static class OgcClassicAuthorizationFixture
{
    /// <summary>Role granting only WFS-T Insert on the seeded test service.</summary>
    public const string InsertOnlyRole = "wfs-insert-only";

    /// <summary>Role granting only WFS-T Update (and therefore Replace) on the seeded test service.</summary>
    public const string UpdateOnlyRole = "wfs-update-only";

    /// <summary>Role granting only WFS-T Delete on the seeded test service.</summary>
    public const string DeleteOnlyRole = "wfs-delete-only";

    /// <summary>A principal with no grant at all: authenticated, and entitled to nothing.</summary>
    public const string NoGrantRole = "ogc-no-grant";

    private const string AdminApiKey = "test-ogc-classic-admin-key";

    public static WebAppFixture Create()
    {
        var roleStore = new GrantPerRoleStore(new Dictionary<string, PermissionGrant>(StringComparer.OrdinalIgnoreCase)
        {
            [InsertOnlyRole] = NewGrant("insert"),
            [UpdateOnlyRole] = NewGrant("update"),
            [DeleteOnlyRole] = NewGrant("delete"),
        });

        return new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ReplaceService<IRoleStore>(roleStore)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
                // Real authentication needs an admin credential configured, even though no
                // test here presents one: the principals below arrive through TestAuthHandler.
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminApiKey);

                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Rbac:RoleClaimType"] = "roles",
                        ["Rbac:DataEditorServicePrefix"] = "data-editor:",
                    });
                });

                builder.ConfigureTestServices(services =>
                {
                    services.AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

                    services.PostConfigureAll<AuthenticationOptions>(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                        options.DefaultScheme = TestAuthHandler.SchemeName;
                    });
                });
            });
    }

    /// <summary>
    /// An HTTP client that presents a named principal carrying <paramref name="roles"/>.
    /// With no roles the principal is still authenticated — the denials it receives are
    /// authorization decisions, not missing credentials.
    /// </summary>
    public static HttpClient CreateClientAs(this WebAppFixture fixture, string user, params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        return fixture.CreateClient(client =>
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
            if (roles.Length > 0)
            {
                client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
            }
        });
    }

    /// <summary>An anonymous client: no principal header at all.</summary>
    public static HttpClient CreateAnonymousClient(this WebAppFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return fixture.CreateClient();
    }

    private static PermissionGrant NewGrant(string operation)
        => new()
        {
            Service = WebAppFixture.TestServiceId,
            Layer = "*",
            Operation = operation,
        };

    /// <summary>
    /// Resolves each role to exactly one write grant, so an "insert-only" principal really
    /// does hold nothing but insert. Roles with no entry resolve to no permissions.
    /// </summary>
    private sealed class GrantPerRoleStore(IReadOnlyDictionary<string, PermissionGrant> grantsByRole) : IRoleStore
    {
        public Task<EffectivePermissions> GetEffectivePermissionsAsync(
            string userId,
            IReadOnlyList<string> roles,
            CancellationToken cancellationToken = default)
        {
            var permissions = roles
                .Where(grantsByRole.ContainsKey)
                .Select(role => grantsByRole[role])
                .ToArray();

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

        public Task<IReadOnlyList<PermissionGrant>> SetPermissionsAsync(Guid roleId, IReadOnlyList<PermissionGrant> permissions, CancellationToken cancellationToken = default)
            => Task.FromResult(permissions);
    }
}
