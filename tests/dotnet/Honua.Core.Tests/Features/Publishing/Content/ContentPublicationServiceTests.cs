// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Publishing.Content;
using Honua.Core.Features.Publishing.Content.Abstractions;
using Honua.Core.Features.Publishing.Content.Domain;
using Honua.Core.Features.Publishing.Content.Services;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Publishing.Content;

/// <summary>
/// Unit tests for <see cref="ContentPublicationService"/> over the in-memory store:
/// immutable versions, route pointer semantics, rollback/republish, policy/public-link,
/// generated-app revision preview, dependency validation, and JSON serialization.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class ContentPublicationServiceTests
{
    private const string Actor = "tester";

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task Publish_CreatesImmutableRevisionOneAndActiveRoute()
    {
        var (service, _) = CreateService();
        var request = new PublishContentRequest
        {
            Kind = ContentPublicationKind.Map,
            RouteSlug = "Quarterly Map",
            Title = "Quarterly Map",
            ContentPayload = "payload",
        };

        var detail = await service.PublishAsync(request, Actor, "corr-1");

        detail.Route.RouteSlug.Should().Be("quarterly-map");
        detail.Route.RoutePath.Should().Be("/api/v1/published/quarterly-map");
        detail.Route.ActiveRevision.Should().Be(1);
        detail.Route.Lifecycle.Should().Be(ContentPublicationLifecycle.Active);
        detail.Versions.Should().ContainSingle();
        var version = detail.Versions[0];
        version.Revision.Should().Be(1);
        version.ContentHash.Should().Be(ContentPublicationCrypto.Sha256Hex("payload"));
        version.PublicationId.Should().Be(detail.Route.PublicationId);
        detail.Route.ActiveVersionId.Should().Be(version.VersionId);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task Publish_DuplicateSlug_ThrowsConflict()
    {
        var (service, _) = CreateService();
        await service.PublishAsync(new PublishContentRequest { Kind = ContentPublicationKind.Map, RouteSlug = "dash" }, Actor, null);

        var act = () => service.PublishAsync(new PublishContentRequest { Kind = ContentPublicationKind.Dashboard, RouteSlug = "dash" }, Actor, null);

        await act.Should().ThrowAsync<ContentPublicationConflictException>();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task Publish_WithUndefinedPolicyVisibility_ThrowsValidation()
    {
        var (service, _) = CreateService();
        var request = new PublishContentRequest
        {
            Kind = ContentPublicationKind.Map,
            RouteSlug = "map",
            Policy = new ContentPublicationPolicy { Visibility = (ContentPublicationVisibility)999 },
        };

        var act = () => service.PublishAsync(request, Actor, null);

        await act.Should().ThrowAsync<ContentPublicationValidationException>()
            .WithMessage("*visibility*");
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task Republish_AllocatesNextRevision_MovesPointer_AndKeepsOldVersionImmutable()
    {
        var (service, _) = CreateService();
        var published = await service.PublishAsync(new PublishContentRequest { Kind = ContentPublicationKind.Map, RouteSlug = "map", ContentPayload = "v1" }, Actor, null);
        var v1Id = published.Route.ActiveVersionId;
        var originalEtag = published.Route.Etag;

        var republished = await service.RepublishAsync(published.Route.PublicationId, new RepublishContentRequest { ContentPayload = "v2" }, Actor, null);

        republished.Route.ActiveRevision.Should().Be(2);
        republished.Route.PreviousVersionId.Should().Be(v1Id);
        republished.Route.Etag.Should().NotBe(originalEtag);

        // The original version is still retrievable and unchanged.
        var v1 = await service.GetVersionAsync(published.Route.PublicationId, "1");
        v1.Should().NotBeNull();
        v1!.VersionId.Should().Be(v1Id);
        v1.ContentHash.Should().Be(ContentPublicationCrypto.Sha256Hex("v1"));
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task Rollback_MovesPointerToEarlierVersion_WithoutNewVersion()
    {
        var (service, store) = CreateService();
        var published = await service.PublishAsync(new PublishContentRequest { Kind = ContentPublicationKind.Map, RouteSlug = "map", ContentPayload = "v1" }, Actor, null);
        var v1Id = published.Route.ActiveVersionId;
        await service.RepublishAsync(published.Route.PublicationId, new RepublishContentRequest { ContentPayload = "v2" }, Actor, null);

        var rolledBack = await service.RollbackAsync(published.Route.PublicationId, new RollbackContentRequest { TargetRevision = 1 }, Actor, null);

        rolledBack.Route.ActiveRevision.Should().Be(1);
        rolledBack.Route.ActiveVersionId.Should().Be(v1Id);
        rolledBack.Route.RollbackTargetVersionId.Should().Be(v1Id);
        // No new version row was created — only v1 and v2 exist.
        (await store.ListVersionsAsync(published.Route.PublicationId, 100)).Should().HaveCount(2);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task Rollback_ToActiveVersion_ThrowsValidation()
    {
        var (service, _) = CreateService();
        var published = await service.PublishAsync(new PublishContentRequest { Kind = ContentPublicationKind.Map, RouteSlug = "map" }, Actor, null);

        var act = () => service.RollbackAsync(published.Route.PublicationId, new RollbackContentRequest { TargetRevision = 1 }, Actor, null);

        await act.Should().ThrowAsync<ContentPublicationValidationException>();
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task Republish_MissingPublication_ThrowsNotFound()
    {
        var (service, _) = CreateService();
        var act = () => service.RepublishAsync(Guid.NewGuid().ToString("D"), new RepublishContentRequest(), Actor, null);
        await act.Should().ThrowAsync<ContentPublicationNotFoundException>();
    }

    [UnitTest]
    [Operation(Operations.GetById)]
    public async Task GeneratedApp_RevisionPreview_ResolvesExactVersionAfterRouteMoved()
    {
        var (service, _) = CreateService();
        var published = await service.PublishAsync(new PublishContentRequest
        {
            Kind = ContentPublicationKind.GeneratedApp,
            RouteSlug = "app",
            AppManifestId = "manifest-v1",
            AppBundleArtifactId = "bundle-v1",
        }, Actor, null);

        await service.RepublishAsync(published.Route.PublicationId, new RepublishContentRequest
        {
            AppManifestId = "manifest-v2",
            AppBundleArtifactId = "bundle-v2",
        }, Actor, null);

        // Active route is v2, but a preview/reopen of v1 must return v1's manifest.
        var v1 = await service.GetVersionAsync(published.Route.PublicationId, "v1");
        v1!.AppManifestId.Should().Be("manifest-v1");
        v1.AppBundleArtifactId.Should().Be("bundle-v1");

        var v1ById = await service.GetVersionAsync(published.Route.PublicationId, v1.VersionId);
        v1ById!.Revision.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.GetById)]
    public async Task GetVersion_InvalidSelector_ThrowsValidation()
    {
        var (service, _) = CreateService();
        var published = await service.PublishAsync(new PublishContentRequest { Kind = ContentPublicationKind.Map, RouteSlug = "map" }, Actor, null);

        var act = () => service.GetVersionAsync(published.Route.PublicationId, "not-a-version");
        await act.Should().ThrowAsync<ContentPublicationValidationException>();
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task UpdatePolicy_CreatesPublicLink_ReturnsTokenOnce_StoresHashOnly()
    {
        var (service, _) = CreateService();
        var published = await service.PublishAsync(new PublishContentRequest { Kind = ContentPublicationKind.Map, RouteSlug = "map" }, Actor, null);

        var result = await service.UpdatePolicyAsync(published.Route.PublicationId, new UpdatePublicationPolicyRequest
        {
            Visibility = ContentPublicationVisibility.Public,
            CreatePublicLink = new ContentPublicLinkRequest { Label = "share", Token = "raw-token" },
        }, Actor, null);

        result.CreatedPublicLinkToken.Should().Be("raw-token");
        result.CreatedPublicLinkId.Should().NotBeNullOrEmpty();
        result.Detail.Route.Policy.Visibility.Should().Be(ContentPublicationVisibility.Public);
        result.Detail.Route.Policy.PublicLink.Enabled.Should().BeTrue();

        var link = result.Detail.Route.Policy.PublicLink.Links.Should().ContainSingle().Subject;
        link.TokenHash.Should().NotBeNullOrEmpty();
        link.TokenHash.Should().NotContain("raw-token");
        ContentPublicationCrypto.TokenMatchesHash("raw-token", link.TokenHash!).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task UpdatePolicy_WithUndefinedVisibility_ThrowsValidation()
    {
        var (service, _) = CreateService();
        var published = await service.PublishAsync(new PublishContentRequest { Kind = ContentPublicationKind.Map, RouteSlug = "map" }, Actor, null);

        var act = () => service.UpdatePolicyAsync(published.Route.PublicationId, new UpdatePublicationPolicyRequest
        {
            Visibility = (ContentPublicationVisibility)999,
        }, Actor, null);

        await act.Should().ThrowAsync<ContentPublicationValidationException>()
            .WithMessage("*visibility*");
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task UpdatePolicy_RevokePublicLink_MarksRevoked()
    {
        var (service, _) = CreateService();
        var published = await service.PublishAsync(new PublishContentRequest { Kind = ContentPublicationKind.Map, RouteSlug = "map" }, Actor, null);
        var created = await service.UpdatePolicyAsync(published.Route.PublicationId, new UpdatePublicationPolicyRequest
        {
            CreatePublicLink = new ContentPublicLinkRequest { Label = "share" },
        }, Actor, null);

        var revoked = await service.UpdatePolicyAsync(published.Route.PublicationId, new UpdatePublicationPolicyRequest
        {
            RevokePublicLinkId = created.CreatedPublicLinkId,
        }, Actor, null);

        revoked.Detail.Route.Policy.PublicLink.Links.Single(l => l.LinkId == created.CreatedPublicLinkId)
            .Revoked.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task Publish_WithUnknownGraphDependency_ThrowsDependencyConflict()
    {
        var (service, _) = CreateService(GraphWith("res.known"));
        var request = new PublishContentRequest
        {
            Kind = ContentPublicationKind.Map,
            RouteSlug = "map",
            Dependencies = [new ContentPublicationDependencyRef { Kind = ContentPublicationDependencyKind.Resource, RefId = "res.unknown" }],
        };

        var act = () => service.PublishAsync(request, Actor, null);
        (await act.Should().ThrowAsync<ContentPublicationDependencyException>()).Which.StatusCode.Should().Be(409);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task Publish_WithUndefinedDependencyKind_ThrowsValidation()
    {
        var (service, _) = CreateService();
        var request = new PublishContentRequest
        {
            Kind = ContentPublicationKind.Map,
            RouteSlug = "map",
            Dependencies = [new ContentPublicationDependencyRef { Kind = (ContentPublicationDependencyKind)999, RefId = "dep" }],
        };

        var act = () => service.PublishAsync(request, Actor, null);

        await act.Should().ThrowAsync<ContentPublicationValidationException>()
            .WithMessage("*dependency.kind*");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task Publish_WithKnownGraphDependency_Succeeds()
    {
        var (service, _) = CreateService(GraphWith("res.known"));
        var request = new PublishContentRequest
        {
            Kind = ContentPublicationKind.Map,
            RouteSlug = "map",
            Dependencies = [new ContentPublicationDependencyRef { Kind = ContentPublicationDependencyKind.Resource, RefId = "res.known" }],
        };

        var detail = await service.PublishAsync(request, Actor, null);
        detail.Versions[0].Dependencies.Should().ContainSingle(d => d.RefId == "res.known");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task Publish_WithPublishedServiceDependency_AndNoStore_ThrowsServiceUnavailable()
    {
        var (service, _) = CreateService();
        var request = new PublishContentRequest
        {
            Kind = ContentPublicationKind.Map,
            RouteSlug = "map",
            Dependencies = [new ContentPublicationDependencyRef { Kind = ContentPublicationDependencyKind.PublishedService, RefId = "svc-1" }],
        };

        var act = () => service.PublishAsync(request, Actor, null);
        (await act.Should().ThrowAsync<ContentPublicationDependencyException>()).Which.StatusCode.Should().Be(503);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task Publish_WithOutOfRangeWgs84Bbox_ThrowsValidation()
    {
        var (service, _) = CreateService();
        var request = new PublishContentRequest
        {
            Kind = ContentPublicationKind.Map,
            RouteSlug = "map",
            DefaultViewBbox = new ContentPublicationBbox { Crs = "EPSG:4326", MinX = -200, MinY = 0, MaxX = 10, MaxY = 10 },
        };

        var act = () => service.PublishAsync(request, Actor, null);
        await act.Should().ThrowAsync<ContentPublicationValidationException>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Version_JsonRoundTrip_PreservesContract()
    {
        var version = new ContentPublicationVersion
        {
            PublicationId = Guid.NewGuid().ToString("D"),
            VersionId = Guid.NewGuid().ToString("D"),
            Revision = 3,
            Kind = ContentPublicationKind.GeneratedApp,
            RouteSlug = "apps/demo",
            RoutePath = "/api/v1/published/apps/demo",
            Policy = new ContentPublicationPolicy { Visibility = ContentPublicationVisibility.Organization },
            DefaultViewBbox = new ContentPublicationBbox { Crs = "EPSG:4326", MinX = -1, MinY = -1, MaxX = 1, MaxY = 1 },
            Dependencies = [new ContentPublicationDependencyRef { Kind = ContentPublicationDependencyKind.MapPackage, RefId = "pkg-1" }],
            CreatedBy = Actor,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var json = JsonSerializer.Serialize(version, ContentPublicationJsonContext.Default.ContentPublicationVersion);
        var round = JsonSerializer.Deserialize(json, ContentPublicationJsonContext.Default.ContentPublicationVersion);

        round.Should().NotBeNull();
        round!.Revision.Should().Be(3);
        round.Kind.Should().Be(ContentPublicationKind.GeneratedApp);
        round.RouteSlug.Should().Be("apps/demo");
        round.Policy.Visibility.Should().Be(ContentPublicationVisibility.Organization);
        round.DefaultViewBbox!.Crs.Should().Be("EPSG:4326");
        round.Dependencies.Should().ContainSingle(d => d.RefId == "pkg-1");
        json.Should().Contain("\"generated-app\"");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void Projection_ToPublishedView_RedactsServerOnlyPolicy()
    {
        var route = new ContentPublicationRouteState
        {
            PublicationId = "pub",
            RouteSlug = "slug",
            RoutePath = "/api/v1/published/slug",
            Kind = ContentPublicationKind.Map,
            ActiveVersionId = "ver",
            ActiveRevision = 1,
            Policy = new ContentPublicationPolicy
            {
                Visibility = ContentPublicationVisibility.Public,
                Access = new AccessPolicy { AllowedRoles = ["secret-role"] },
                Embed = new ContentEmbedPolicy { AllowEmbedding = true, AllowedOrigins = ["https://app.example"] },
                PublicLink = new ContentPublicLinkPolicy { Enabled = true, Links = [new ContentPublicLink { LinkId = "l", TokenHash = "hash", CreatedBy = "u", CreatedAt = DateTimeOffset.UtcNow }] },
            },
            Generation = 1,
            Etag = "\"e\"",
            UpdatedBy = "u",
            UpdatedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var version = new ContentPublicationVersion
        {
            PublicationId = "pub",
            VersionId = "ver",
            Revision = 1,
            Kind = ContentPublicationKind.Map,
            RouteSlug = "slug",
            RoutePath = "/api/v1/published/slug",
            Policy = route.Policy,
            Dependencies = [new ContentPublicationDependencyRef { Kind = ContentPublicationDependencyKind.Resource, RefId = "res" }],
            CreatedBy = "u",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var view = ContentPublicationProjections.ToPublishedView(route, version, includeDependencies: false);
        var json = JsonSerializer.Serialize(view, ContentPublicationJsonContext.Default.PublishedArtifactView);

        json.Should().NotContain("secret-role");
        json.Should().NotContain("hash");
        view.Embeddable.Should().BeTrue();
        view.AllowedEmbedOrigins.Should().Contain("https://app.example");
        view.Dependencies.Should().BeNull();

        var withDeps = ContentPublicationProjections.ToPublishedView(route, version, includeDependencies: true);
        withDeps.Dependencies.Should().ContainSingle(d => d.RefId == "res");
    }

    private static (IContentPublicationService Service, IContentPublicationStore Store) CreateService(
        IMetadataV2GraphProvider? graphProvider = null)
    {
        var store = new InMemoryContentPublicationStore();
        var service = new ContentPublicationService(
            store,
            TimeProvider.System,
            graphProvider,
            publishedServiceStore: null,
            NullLogger<ContentPublicationService>.Instance);
        return (service, store);
    }

    private static StubGraphProvider GraphWith(params string[] resourceIds)
    {
        var graph = new MetadataV2Graph
        {
            Environment = "test",
            Revision = 1,
            GeneratedAt = DateTimeOffset.UtcNow,
            Resources = resourceIds
                .Select(id => new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
                    Type = MetadataV2ResourceType.FeatureDataset,
                })
                .ToArray(),
        };
        return new StubGraphProvider(new MetadataV2GraphSnapshot(graph, "etag-test-1", DateTimeOffset.UtcNow));
    }

    private sealed class StubGraphProvider(MetadataV2GraphSnapshot snapshot) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(snapshot.Revision == revision ? snapshot : null);
    }
}
