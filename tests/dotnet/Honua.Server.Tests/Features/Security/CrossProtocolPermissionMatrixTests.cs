// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.OData.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Security;

/// <summary>
/// Cross-protocol conformance matrix for the canonical per-operation RBAC
/// resolver (#1375) wired through every protocol adapter via the shared
/// <c>AccessPolicyHelpers</c> seam (#1376).
/// <para>
/// Each protocol is exercised twice against the same coarse <see cref="AccessPolicy"/>
/// deny: once where the principal holds a per-operation grant that the resolver
/// must honor (overriding the coarse deny), and once where the principal holds no
/// matching grant so behavior must fall back to the coarse policy exactly as
/// before (no regression). A per-operation grant that allows <c>query</c> but not
/// <c>update</c> is also proven to deny writes consistently across protocols.
/// </para>
/// </summary>
public sealed class CrossProtocolPermissionMatrixTests
{
    private const string GrantedRole = "matrix-grant-role";
    private const string UngrantedRole = "matrix-ungranted-role";
    private const string CoarseOnlyRole = "coarse-only-role";

    // ---- Helpers --------------------------------------------------------

    /// <summary>
    /// Builds a factory for the READ matrix: the alpha service has an explicit
    /// coarse read policy requiring a role the principal never holds (so the
    /// legacy AccessPolicy read seam denies), and seeds the supplied per-operation
    /// grants onto <see cref="GrantedRole"/>. The resolver overrides the explicit
    /// coarse read deny when a matching query grant is present.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(params PermissionGrant[] grants)
        => CreateFactory(
            ServiceRbacTestFixture.CreateServiceMetadata(readRoles: [CoarseOnlyRole]),
            grants);

    /// <summary>
    /// Builds a factory for the WRITE matrix: the alpha service carries NO explicit
    /// write policy, so the coarse data-editor role gate denies a principal lacking
    /// the data-editor/admin role. A per-operation write grant (#1376) overrides
    /// that coarse deny; a query-only grant does not. Explicit AccessPolicy write
    /// restrictions remain authoritative and are covered separately, so they are
    /// intentionally absent here.
    /// </summary>
    private static WebApplicationFactory<Program> CreateWriteFactory(params PermissionGrant[] grants)
        => CreateFactory(alphaServiceMetadata: null, grants);

    private static WebApplicationFactory<Program> CreateFactory(
        AccessPolicy? alphaServiceMetadata,
        PermissionGrant[] grants)
        => ServiceRbacTestFixture.CreateFactory(
            layerCatalogFactory: () => new RbacTestLayerCatalog(alphaServiceMetadata: alphaServiceMetadata),
            configureServices: services =>
            {
                services.RemoveAll<ICrsDetectionService>();
                services.AddSingleton<ICrsDetectionService, NoopCrsDetectionService>();
                services.RemoveAll<IRoleStore>();
                services.AddSingleton<IRoleStore>(new MatrixRoleStore(GrantedRole, grants));
            });

    private static PermissionGrant Grant(string operation, string layer = "*")
        => new() { Service = ServiceRbacTestFixture.AlphaService, Layer = layer, Operation = operation };

    // ---- FeatureServer query (already wired in #1385; kept for matrix completeness) ----

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task FeatureServerQuery_PerOperationGrant_OverridesCoarseDeny()
    {
        using var factory = CreateFactory(Grant("query"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, GrantedRole);

        var response = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/query?where=1=1&f=json");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task FeatureServerQuery_NoGrant_FallsBackToCoarseDeny()
    {
        using var factory = CreateFactory(Grant("query"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, UngrantedRole);

        var response = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/query?where=1=1&f=json");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    // ---- FeatureServer edits (applyEdits) ----

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task FeatureServerApplyEdits_UpdateGrant_OverridesCoarseDeny()
    {
        using var factory = CreateWriteFactory(Grant("update"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, GrantedRole);

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/applyEdits",
            ServiceRbacTestFixture.CreateApplyEditsContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task FeatureServerApplyEdits_QueryOnlyGrant_DeniesWrite()
    {
        // The principal can read but holds NO write grant; the coarse policy also
        // denies, so the write must be forbidden — proving query!=update.
        using var factory = CreateWriteFactory(Grant("query"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, GrantedRole);

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/applyEdits",
            ServiceRbacTestFixture.CreateApplyEditsContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task FeatureServerApplyEdits_NoGrant_FallsBackToCoarseDeny()
    {
        using var factory = CreateWriteFactory(Grant("update"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, UngrantedRole);

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/applyEdits",
            ServiceRbacTestFixture.CreateApplyEditsContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    // ---- FeatureServer layer-not-granted (per-layer scoping) ----

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task FeatureServerQuery_GrantScopedToOtherLayer_DeniesUngrantedLayer()
    {
        // Grant query only on layer "Beta Layer"; the request targets alpha layer 0
        // (named "Alpha Layer"), which has no grant → coarse deny applies.
        using var factory = CreateFactory(Grant("query", layer: "Beta Layer"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, GrantedRole);

        var response = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/query?where=1=1&f=json");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task FeatureServerQuery_GrantScopedToTargetLayer_AllowsThatLayer()
    {
        using var factory = CreateFactory(Grant("query", layer: "Alpha Layer"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, GrantedRole);

        var response = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/query?where=1=1&f=json");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    // ---- OData query ----

    [IntegrationTest]
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers({layerId})/Features")]
    public async Task ODataQuery_QueryGrant_OverridesCoarseDeny()
    {
        using var factory = CreateFactory(Grant("query"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, GrantedRole);

        var response = await client.GetAsync(
            $"/odata/Layers({ServiceRbacTestFixture.AlphaLayerId})/Features");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers({layerId})/Features")]
    public async Task ODataQuery_NoGrant_FallsBackToCoarseDeny()
    {
        using var factory = CreateFactory(Grant("query"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, UngrantedRole);

        var response = await client.GetAsync(
            $"/odata/Layers({ServiceRbacTestFixture.AlphaLayerId})/Features");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    // ---- OData CRUD (create) ----

    [IntegrationTest]
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task ODataCreate_UpdateGrant_OverridesCoarseDeny()
    {
        using var factory = CreateWriteFactory(Grant("update"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, GrantedRole);

        var response = await client.PostAsync(
            $"/odata/Layers({ServiceRbacTestFixture.AlphaLayerId})/Features",
            ServiceRbacTestFixture.CreateODataFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Created);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task ODataCreate_QueryOnlyGrant_DeniesWrite()
    {
        using var factory = CreateWriteFactory(Grant("query"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, GrantedRole);

        var response = await client.PostAsync(
            $"/odata/Layers({ServiceRbacTestFixture.AlphaLayerId})/Features",
            ServiceRbacTestFixture.CreateODataFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    // ---- OData batch ----

    [IntegrationTest]
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task ODataBatch_QueryGrant_AllowsBatchedRead()
    {
        using var factory = CreateFactory(Grant("query"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, GrantedRole);

        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(new ODataBatchRequestItem
            {
                Id = "read-alpha",
                Method = "GET",
                Url = $"Layers({ServiceRbacTestFixture.AlphaLayerId})"
            })
        };

        var json = JsonSerializer.Serialize(batchRequest, ODataJsonContext.Default.ODataBatchRequest);
        var response = await client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var responses = ServiceRbacTestFixture.GetPropertyCaseInsensitive(document.RootElement, "responses");
        responses.GetArrayLength().Should().Be(1);
        ServiceRbacTestFixture.GetPropertyCaseInsensitive(responses[0], "status").GetInt32().Should().Be(200);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task ODataBatch_NoGrant_FallsBackToCoarseDeny()
    {
        using var factory = CreateFactory(Grant("query"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, UngrantedRole);

        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(new ODataBatchRequestItem
            {
                Id = "read-alpha",
                Method = "GET",
                Url = $"Layers({ServiceRbacTestFixture.AlphaLayerId})"
            })
        };

        var json = JsonSerializer.Serialize(batchRequest, ODataJsonContext.Default.ODataBatchRequest);
        var response = await client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var responses = ServiceRbacTestFixture.GetPropertyCaseInsensitive(document.RootElement, "responses");
        responses.GetArrayLength().Should().Be(1);
        ServiceRbacTestFixture.GetPropertyCaseInsensitive(responses[0], "status").GetInt32().Should().Be(403);
    }

    // ---- OGC API Features read ----

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task OgcFeaturesRead_QueryGrant_OverridesCoarseDeny()
    {
        using var factory = CreateFactory(Grant("query"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, GrantedRole);

        var response = await client.GetAsync(
            $"/ogc/features/collections/{ServiceRbacTestFixture.AlphaLayerId}/items");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task OgcFeaturesRead_NoGrant_FallsBackToCoarseDeny()
    {
        using var factory = CreateFactory(Grant("query"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, UngrantedRole);

        var response = await client.GetAsync(
            $"/ogc/features/collections/{ServiceRbacTestFixture.AlphaLayerId}/items");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    // ---- OGC API Features mutation (create) ----

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task OgcFeaturesCreate_UpdateGrant_OverridesCoarseDeny()
    {
        using var factory = CreateWriteFactory(Grant("update"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, GrantedRole);

        var response = await client.PostAsync(
            $"/ogc/features/collections/{ServiceRbacTestFixture.AlphaLayerId}/items",
            ServiceRbacTestFixture.CreateOgcFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Created);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task OgcFeaturesCreate_QueryOnlyGrant_DeniesWrite()
    {
        using var factory = CreateWriteFactory(Grant("query"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, GrantedRole);

        var response = await client.PostAsync(
            $"/ogc/features/collections/{ServiceRbacTestFixture.AlphaLayerId}/items",
            ServiceRbacTestFixture.CreateOgcFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    // ---- OGC API Tiles read ----

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiTiles)]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}")]
    public async Task OgcTilesRead_QueryGrant_OverridesCoarseDeny()
    {
        using var factory = CreateFactory(Grant("query"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, GrantedRole);

        var response = await client.GetAsync(
            $"/ogc/tiles/collections/{ServiceRbacTestFixture.AlphaLayerId}");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiTiles)]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}")]
    public async Task OgcTilesRead_NoGrant_FallsBackToCoarseDeny()
    {
        using var factory = CreateFactory(Grant("query"));
        using var client = ServiceRbacTestFixture.CreateClient(factory, UngrantedRole);

        var response = await client.GetAsync(
            $"/ogc/tiles/collections/{ServiceRbacTestFixture.AlphaLayerId}");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    // ---- WFS GetFeature + Transaction ----
    //
    // WFS read (GetFeature / capabilities feature-type list) flows through the
    // shared GetPublishedFeatureTypesAsync filter, now resolver-aware via
    // AccessPolicyHelpers.IsResourceAccessibleAsync(Query). WFS Transaction flows
    // through LayerValidationHelpers.ValidateLayerWithAccessV2Async(Write) plus
    // ServiceDataEditorAuthorization.RequireResourceDataEditorAsync, both of which
    // are resolver-wired and exercised by the OData/OGC/FeatureServer matrix cases
    // above. The ServiceRbacTestFixture in-memory graph cannot render a full WFS
    // GetCapabilities document (unrelated to authorization), so WFS GetFeature
    // visibility is covered by WfsReadVisibilityTests against the WFS handler seam
    // rather than a fixture HTTP round-trip here.

    /// <summary>
    /// <see cref="IRoleStore"/> that resolves the supplied per-operation grants for
    /// a single role; any other role resolves to no grants.
    /// </summary>
    private sealed class MatrixRoleStore(string roleName, PermissionGrant[] grants) : IRoleStore
    {
        public Task<EffectivePermissions> GetEffectivePermissionsAsync(
            string userId,
            IReadOnlyList<string> roles,
            CancellationToken cancellationToken = default)
        {
            var resolved = roles.Any(r => string.Equals(r, roleName, StringComparison.OrdinalIgnoreCase))
                ? grants
                : [];

            return Task.FromResult(new EffectivePermissions
            {
                UserId = userId,
                Roles = roles,
                Permissions = resolved,
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

/// <summary>
/// Direct coverage of the WFS GetFeature read seam: the WFS adapter filters
/// published feature types through <c>AccessPolicyHelpers.IsResourceAccessibleAsync</c>
/// with the <see cref="AuthorizationOperation.Query"/> operation (#1376). These
/// tests exercise that exact shared seam so a per-operation grant is proven to be
/// honored for WFS reads, with the no-grant case falling back to the coarse
/// <see cref="AccessPolicy"/> exactly as before.
/// </summary>
[Protocol(TestProtocols.Wfs20)]
[Operation(Operations.Query)]
public sealed class WfsReadVisibilitySeamTests
{
    private const string ServiceName = "alpha";
    private const string GrantedRole = "wfs-grant-role";

    [UnitTest]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task WfsReadSeam_QueryGrant_OverridesCoarseDeny()
    {
        // Coarse policy requires a role the principal lacks (would hide the layer),
        // but the principal's role carries a query grant on the service.
        var context = CreateContext(
            roles: [GrantedRole],
            grant: new PermissionGrant { Service = ServiceName, Layer = "*", Operation = "query" });

        var accessible = await AccessPolicyHelpers.IsResourceAccessibleAsync(
            context,
            CreateResource(),
            CreateService(coarseReadRole: "coarse-only"),
            AuthorizationOperation.Query);

        accessible.Should().BeTrue("the WFS read seam honors the per-operation query grant");
    }

    [UnitTest]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task WfsReadSeam_NoGrant_FallsBackToCoarseDeny()
    {
        var context = CreateContext(
            roles: [GrantedRole],
            grant: new PermissionGrant { Service = "other", Layer = "*", Operation = "query" });

        var accessible = await AccessPolicyHelpers.IsResourceAccessibleAsync(
            context,
            CreateResource(),
            CreateService(coarseReadRole: "coarse-only"),
            AuthorizationOperation.Query);

        accessible.Should().BeFalse("no matching grant falls back to the coarse deny (no regression)");
    }

    private static DefaultHttpContext CreateContext(string[] roles, PermissionGrant grant)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.Configure<RbacOptions>(opts => opts.RoleClaimType = "roles");
        services.AddSingleton<IRoleStore>(new SingleGrantRoleStore(GrantedRole, grant));
        services.AddSingleton<IPermissionResolver>(sp =>
            new PermissionResolver(sp.GetRequiredService<IRoleStore>()));

        var claims = new List<Claim> { new(ClaimTypes.Name, "wfs-user") };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
            claims.Add(new Claim("roles", role));
        }

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
    }

    private static MetadataV2Resource CreateResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-alpha", Name = "Alpha Layer" }
        };

    private static MetadataV2Service CreateService(string coarseReadRole)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-alpha", Name = ServiceName },
            AccessPolicy = new AccessPolicy { AllowedRoles = [coarseReadRole] }
        };

    private sealed class SingleGrantRoleStore(string roleName, PermissionGrant grant) : IRoleStore
    {
        public Task<EffectivePermissions> GetEffectivePermissionsAsync(
            string userId,
            IReadOnlyList<string> roles,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new EffectivePermissions
            {
                UserId = userId,
                Roles = roles,
                Permissions = roles.Any(r => string.Equals(r, roleName, StringComparison.OrdinalIgnoreCase))
                    ? [grant]
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

        public Task<IReadOnlyList<PermissionGrant>> SetPermissionsAsync(Guid roleId, IReadOnlyList<PermissionGrant> permissions, CancellationToken cancellationToken = default)
            => Task.FromResult(permissions);
    }
}
