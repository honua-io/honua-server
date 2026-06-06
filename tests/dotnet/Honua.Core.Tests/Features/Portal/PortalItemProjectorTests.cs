// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Portal.Domain;
using Honua.Core.Features.Portal.Services;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Core.Tests.Features.Portal;

/// <summary>
/// Unit tests for the RBAC-aware Metadata v2 → ArcGIS Portal item projector.
/// Covers service-type mapping, item URL emission (including behind-proxy base
/// URLs), access-tier derivation, and the RBAC visibility rule.
/// </summary>
[Protocol(ProtocolNames.TestQuality)]
public sealed class PortalItemProjectorTests
{
    private const string ProxyBaseUrl = "https://maps.example.gov/honua";

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ProjectVisibleItems_FeatureService_MapsEsriItemShape()
    {
        var snapshot = BuildSnapshot(
            BuildService("service.parcels", "Parcels", ServiceProtocols.FeatureServer, publicAccess: true),
            resourceAccess: AnonymousPolicy());
        var projector = CreateProjector();

        var items = projector.ProjectVisibleItems(snapshot, Anonymous(), ProxyBaseUrl);

        items.Should().ContainSingle();
        var item = items[0];
        item.Id.Should().Be("service.parcels");
        item.Type.Should().Be(PortalItemTypes.FeatureService);
        item.Title.Should().Be("Parcels Title");
        item.TypeKeywords.Should().Contain("Feature Service");
        item.Access.Should().Be(PortalAccessLevels.Public);
        item.Tags.Should().Contain("cadastre");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ProjectVisibleItems_BuildsUrlBehindProxyAndEscapesName()
    {
        var snapshot = BuildSnapshot(
            BuildService("service.basemap", "City Basemap", ServiceProtocols.MapServer, publicAccess: true),
            resourceAccess: AnonymousPolicy());
        var projector = CreateProjector();

        var items = projector.ProjectVisibleItems(snapshot, Anonymous(), ProxyBaseUrl + "/");

        items.Should().ContainSingle();
        // Base URL is honoured verbatim (caller resolves forwarded scheme/host),
        // trailing slash is trimmed, service name is URL-escaped, route segment is
        // the GeoServices type — agreeing with the services directory emission.
        items[0].Url.Should().Be("https://maps.example.gov/honua/rest/services/City%20Basemap/MapServer");
        items[0].Type.Should().Be(PortalItemTypes.MapService);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ProjectVisibleItems_ProjectsExtentFromResourceBbox()
    {
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource.parcels", Name = "parcels" },
            Type = MetadataV2ResourceType.FeatureDataset,
            AccessPolicy = AnonymousPolicy(),
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Polygon,
                Bbox = new MetadataV2Bbox { West = -10, South = -20, East = 30, North = 40 },
            },
        };
        var snapshot = BuildSnapshotWithResource(
            BuildService("service.parcels", "Parcels", ServiceProtocols.FeatureServer, publicAccess: true),
            resource);
        var projector = CreateProjector();

        var item = projector.ProjectVisibleItems(snapshot, Anonymous(), ProxyBaseUrl).Single();

        item.Extent.Should().HaveCount(2);
        item.Extent[0].Should().Equal(-10d, -20d);
        item.Extent[1].Should().Equal(30d, 40d);
        item.SpatialReference.Should().Be("4326");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ProjectVisibleItems_SkipsNonEsriServiceTypes()
    {
        var snapshot = BuildSnapshot(
            BuildService("service.ogc", "OgcFeatures", ServiceProtocols.OgcFeatures, publicAccess: true),
            resourceAccess: AnonymousPolicy());
        var projector = CreateProjector();

        var items = projector.ProjectVisibleItems(snapshot, Anonymous(), ProxyBaseUrl);

        items.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void DeriveAccess_AuthenticatedOnly_YieldsOrg()
    {
        // Resource requires authentication (no anonymous, no role restriction).
        var snapshot = BuildSnapshot(
            BuildService("service.parcels", "Parcels", ServiceProtocols.FeatureServer, publicAccess: false),
            resourceAccess: new AccessPolicy { AllowAnonymous = false });
        var projector = CreateProjector();

        var item = projector.ProjectVisibleItems(snapshot, AuthenticatedUser("editor"), ProxyBaseUrl).Single();

        item.Access.Should().Be(PortalAccessLevels.Org);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void DeriveAccess_RoleRestricted_YieldsPrivate()
    {
        var snapshot = BuildSnapshot(
            BuildService("service.parcels", "Parcels", ServiceProtocols.FeatureServer, publicAccess: false),
            resourceAccess: new AccessPolicy { AllowedRoles = ["admin"] });
        var projector = CreateProjector();

        var item = projector.ProjectVisibleItems(snapshot, AuthenticatedUser("admin"), ProxyBaseUrl).Single();

        item.Access.Should().Be(PortalAccessLevels.Private);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ProjectVisibleItems_UnpermittedPrincipal_OmitsItem()
    {
        // Resource is restricted to "admin"; the caller has only "viewer".
        var snapshot = BuildSnapshot(
            BuildService("service.parcels", "Parcels", ServiceProtocols.FeatureServer, publicAccess: false),
            resourceAccess: new AccessPolicy { AllowedRoles = ["admin"] });
        var projector = CreateProjector();

        var items = projector.ProjectVisibleItems(snapshot, AuthenticatedUser("viewer"), ProxyBaseUrl);

        items.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ProjectItem_UnpermittedPrincipal_ReturnsNull()
    {
        var snapshot = BuildSnapshot(
            BuildService("service.parcels", "Parcels", ServiceProtocols.FeatureServer, publicAccess: false),
            resourceAccess: new AccessPolicy { AllowedRoles = ["admin"] });
        var projector = CreateProjector();

        var item = projector.ProjectItem(snapshot, AuthenticatedUser("viewer"), "service.parcels", ProxyBaseUrl);

        item.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ProjectItem_PermittedPrincipal_ReturnsItem()
    {
        var snapshot = BuildSnapshot(
            BuildService("service.parcels", "Parcels", ServiceProtocols.FeatureServer, publicAccess: false),
            resourceAccess: new AccessPolicy { AllowedRoles = ["admin"] });
        var projector = CreateProjector();

        var item = projector.ProjectItem(snapshot, AuthenticatedUser("admin"), "service.parcels", ProxyBaseUrl);

        item.Should().NotBeNull();
        item!.Id.Should().Be("service.parcels");
        item.Access.Should().Be(PortalAccessLevels.Private);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ProjectItem_UnknownId_ReturnsNull()
    {
        var snapshot = BuildSnapshot(
            BuildService("service.parcels", "Parcels", ServiceProtocols.FeatureServer, publicAccess: true),
            resourceAccess: AnonymousPolicy());
        var projector = CreateProjector();

        projector.ProjectItem(snapshot, Anonymous(), "service.missing", ProxyBaseUrl).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ProjectVisibleItems_RegistryMarksConstructUnservable_OmitsItem()
    {
        // The facade serve decision is sourced from the shared capability
        // registry (#1382). A registry whose feature-service descriptor is not
        // servable must omit the item even though the protocol→item-type table
        // still recognises FeatureServer — proving the verdict comes from the
        // registry, not the private mapping table.
        var snapshot = BuildSnapshot(
            BuildService("service.parcels", "Parcels", ServiceProtocols.FeatureServer, publicAccess: true),
            resourceAccess: AnonymousPolicy());
        var projector = new PortalItemProjector(
            new FakeAccessPolicyEvaluator(),
            new EsriConstructCapabilityRegistry(NonServableFacadeDescriptors()));

        var items = projector.ProjectVisibleItems(snapshot, Anonymous(), ProxyBaseUrl);

        items.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ProjectVisibleItems_BuiltInRegistry_ServesAllFacadeServiceTypes()
    {
        // Behaviour-preservation guard: every Esri service construct the private
        // table used to accept is served by the built-in registry descriptors.
        var registry = new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors);
        registry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.FacadeFeatureService).CanServe.Should().BeTrue();
        registry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.FacadeMapService).CanServe.Should().BeTrue();
        registry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.FacadeImageService).CanServe.Should().BeTrue();
    }

    private static PortalItemProjector CreateProjector()
        => new(
            new FakeAccessPolicyEvaluator(),
            new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors));

    private static EsriConstructCapabilityDescriptor[] NonServableFacadeDescriptors()
        =>
        [
            new EsriConstructCapabilityDescriptor
            {
                ConstructKey = EsriConstructCapabilityRegistry.Keys.FacadeFeatureService,
                AutomationStatus = MigrationFidelityAutomationStatuses.ManualReview,
                Code = ImportCompatibilityCodes.ManualReview,
                Reason = "Test override: feature-service facade construct is not servable.",
                CanTransform = false,
                CanServe = false,
                RequiresCheck = true
            }
        ];

    private static MetadataV2Service BuildService(string id, string name, string protocol, bool publicAccess)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = id,
                Name = name,
                Title = $"{name} Title",
                Description = $"{name} description",
                Publisher = "gis-team",
                Keywords = ["cadastre"],
            },
            Protocols = [protocol],
            AccessPolicy = publicAccess ? AnonymousPolicy() : null,
        };

    private static AccessPolicy AnonymousPolicy() => new() { AllowAnonymous = true };

    private static MetadataV2GraphSnapshot BuildSnapshot(MetadataV2Service service, AccessPolicy? resourceAccess)
    {
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource.parcels", Name = "parcels" },
            Type = MetadataV2ResourceType.FeatureDataset,
            AccessPolicy = resourceAccess,
        };
        return BuildSnapshotWithResource(service, resource);
    }

    private static MetadataV2GraphSnapshot BuildSnapshotWithResource(
        MetadataV2Service service,
        MetadataV2Resource resource)
    {
        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            Resources = [resource],
            Services = [service],
            Publications =
            [
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub.parcels", Name = "parcels" },
                    ResourceId = resource.Metadata.Id,
                    ServiceId = service.Metadata.Id,
                    PublicationType = MetadataV2PublicationType.EsriFeatureLayer,
                    Identifier = new MetadataV2PublicationIdentifier { Value = "0", IsNumeric = true },
                }
            ],
        };
        return new MetadataV2GraphSnapshot(graph, "\"etag\"", DateTimeOffset.UnixEpoch);
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static ClaimsPrincipal AuthenticatedUser(params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "user-1") };
        claims.AddRange(roles.Select(static r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    /// <summary>
    /// Faithful test double for the host's access-policy evaluator. Reproduces the
    /// deny-wins composition of the production <c>AccessPolicyEvaluator</c>
    /// (anonymous flag → authenticated → role membership) so the projector's RBAC
    /// behaviour is exercised end to end without referencing Honua.Hosting.
    /// </summary>
    private sealed class FakeAccessPolicyEvaluator : IAccessPolicyEvaluator
    {
        public Task<AccessDecision> EvaluateAsync(ClaimsPrincipal principal, string resource, string action)
            => Task.FromResult(Evaluate(principal, resource, action));

        public AccessDecision Evaluate(ClaimsPrincipal principal, string resource, string action)
            => principal.Identity?.IsAuthenticated == true
                ? AccessDecision.Allowed()
                : AccessDecision.RequiresAuth();

        public Task<AccessDecision> EvaluateAsync(
            ClaimsPrincipal principal,
            AccessPolicy? layerPolicy,
            AccessPolicy? servicePolicy,
            object? scope = null)
            => Task.FromResult(Evaluate(principal, layerPolicy, servicePolicy, scope));

        public AccessDecision Evaluate(
            ClaimsPrincipal principal,
            AccessPolicy? layerPolicy,
            AccessPolicy? servicePolicy,
            object? scope = null)
        {
            var layer = EvaluateSingle(principal, layerPolicy);
            var service = EvaluateSingle(principal, servicePolicy);

            if (layer is null && service is null)
            {
                return principal.Identity?.IsAuthenticated == true
                    ? AccessDecision.Allowed()
                    : AccessDecision.RequiresAuth();
            }

            if (layer is { IsAllowed: false })
            {
                return layer.Value;
            }

            if (service is { IsAllowed: false })
            {
                return service.Value;
            }

            return AccessDecision.Allowed();
        }

        private static AccessDecision? EvaluateSingle(ClaimsPrincipal principal, AccessPolicy? policy)
        {
            if (policy is null)
            {
                return null;
            }

            if (policy.AllowAnonymous)
            {
                return AccessDecision.Allowed();
            }

            if (principal.Identity?.IsAuthenticated != true)
            {
                return AccessDecision.RequiresAuth();
            }

            if (policy.AllowedRoles is { Length: > 0 } roles &&
                !roles.Any(principal.IsInRole))
            {
                return AccessDecision.Forbidden();
            }

            return AccessDecision.Allowed();
        }
    }
}
