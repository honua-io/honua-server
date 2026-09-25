// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Admin;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Db.Postgres.Tests.Features.Admin;

public sealed class LayerPublicationNamespaceTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("namespace/name")]
    [InlineData("wildcard*")]
    [InlineData("māp")]
    [InlineData("maps\n")]
    [InlineData("maps")]
    public async Task InvalidNamespace_IsRejectedBeforeDiscoveryOrGraphWrites(string publicationNamespace)
    {
        var discovery = new Mock<ITableDiscoveryService>(MockBehavior.Strict);
        var store = new Mock<IMetadataV2GraphStore>(MockBehavior.Strict);
        var service = new PostgreSqlLayerPublishingService(discovery.Object, store.Object,
            NullLogger<PostgreSqlLayerPublishingService>.Instance);
        var publish = () => service.PublishLayerAsync("unused", new LayerPublishRequest
        {
            Schema = "public", Table = "points", LayerName = "Points", Namespace = publicationNamespace
        });

        (await publish.Should().ThrowAsync<LayerPublishingException>()).Which.ErrorKind
            .Should().Be(LayerPublishingErrorKind.Validation);
        discovery.VerifyNoOtherCalls();
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public void NamespaceBoundsAndTrustedTenant_PreserveLegacyAndRejectUnresolvedOwnership()
    {
        LayerPublicationNamespace.IsValid(new string('a', 128)).Should().BeTrue();
        LayerPublicationNamespace.IsValid(new string('a', 129)).Should().BeFalse();
        var resolve = () => PostgreSqlLayerPublishingService.ResolvePublicationScope("Maps_1.0", null);
        resolve.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Validation);
        PostgreSqlLayerPublishingService.ResolvePublicationScope("Maps_1.0", "Tenant-A")
            .Should().Be(new PostgreSqlLayerPublishingService.PublicationScope("Maps_1.0", "Tenant-A"));
        PostgreSqlLayerPublishingService.ResolvePublicationScope(null, "Tenant-A")
            .Should().Be(new PostgreSqlLayerPublishingService.PublicationScope(null, null));
    }

    [Theory]
    [InlineData("maps", "tenant-b", "maps", "tenant-a")]
    [InlineData("other", "tenant-a", "maps", "tenant-a")]
    [InlineData("Maps", "tenant-a", "maps", "tenant-a")]
    [InlineData(null, null, "maps", "tenant-a")]
    [InlineData("maps", "tenant-a", null, null)]
    public void ExistingService_CannotBeRetaggedOrAdopted(
        string? existingNamespace, string? existingTenant, string? requestedNamespace, string? requestedTenant)
    {
        var graph = Graph(existingNamespace, existingTenant);
        var validate = () => PostgreSqlLayerPublishingService.ValidatePublicationScope(graph, "SERVICE",
            new(requestedNamespace, requestedTenant));
        validate.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.Conflict);
        graph.Services[0].Metadata.Namespace.Should().Be(existingNamespace);
        graph.Services[0].Metadata.Tenant.Should().Be(existingTenant);
    }

    [Fact]
    public void FinalWriteBase_RechecksScopeAndRejectsSqlOnlyLegacyAdoption()
    {
        var scope = new PostgreSqlLayerPublishingService.PublicationScope("maps", "tenant-a");
        PostgreSqlLayerPublishingService.ValidatePublicationScope(Graph("maps", "tenant-a"), "service", scope, true);
        var changedBase = () => PostgreSqlLayerPublishingService.ValidatePublicationScope(
            Graph("maps", "tenant-b"), "service", scope, true);
        changedBase.Should().Throw<LayerPublishingException>();
        var sqlOnly = () => PostgreSqlLayerPublishingService.ValidatePublicationScope(new(), "service", scope, true);
        sqlOnly.Should().Throw<LayerPublishingException>();
        PostgreSqlLayerPublishingService.ValidatePublicationScope(new(), "service", scope);
        PostgreSqlLayerPublishingService.ValidatePublicationScope(Graph(null, null), "service", new(null, null));
    }

    private static MetadataV2Graph Graph(string? publicationNamespace, string? tenant) => new()
    {
        Services = [new MetadataV2Service
        {
            Metadata = new() { Id = "service-id", Name = "service", Namespace = publicationNamespace, Tenant = tenant }
        }]
    };

    [Theory]
    [InlineData("service")]
    [InlineData("publication")]
    [InlineData("resource")]
    [InlineData("binding")]
    public void ScopedChain_RejectsForeignTenantAndLegacyLinkWithoutChangingGraph(string scopedNode)
    {
        MetadataV2ObjectMetadata Metadata(string id) => new()
        {
            Id = id, Name = id, Namespace = scopedNode == id ? "maps" : null,
            Tenant = scopedNode == id ? "owner" : null
        };
        var graph = new MetadataV2Graph
        {
            Services = [new() { Metadata = Metadata("service") }],
            Resources = [new() { Metadata = Metadata("resource") }],
            StorageBindings = [new() { Metadata = Metadata("binding"), ResourceId = "resource", StorageLayerId = 42 }],
            Publications = [new()
            {
                Metadata = Metadata("publication"), ServiceId = "service", ResourceId = "resource",
                StorageBindingId = "binding", LayerIndex = 42
            }]
        };
        var original = graph;
        PostgreSqlLayerPublishingService.ValidateTenantAccess(graph, "service", null, "owner");
        PostgreSqlLayerPublishingService.ValidateTenantAccess(graph, null, new HashSet<int> { 42 }, "owner");
        var serviceAccess = () => PostgreSqlLayerPublishingService.ValidateTenantAccess(graph, "service", null, "foreign");
        serviceAccess.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.NotFound);
        var layerAccess = () => PostgreSqlLayerPublishingService.ValidateTenantAccess(graph, null, new HashSet<int> { 42 }, null);
        layerAccess.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.NotFound);
        var sourceLink = () => PostgreSqlLayerPublishingService.BuildLinkedLayerMetadataV2Graph(
            graph, "new-service", 42, "Layer", 4326, DateTimeOffset.UtcNow);
        sourceLink.Should().Throw<LayerPublishingException>().WithMessage("Legacy layer linking*");
        var targetLink = () => PostgreSqlLayerPublishingService.ValidateLegacyLinkScope(graph, "service", 99);
        targetLink.Should().Throw<LayerPublishingException>().WithMessage("Legacy layer linking*");
        graph.Should().BeEquivalentTo(original);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("shared-styles", "owner")]
    public void ReusedDependency_PreservesEntireMetadataWithoutRetagging(string? publicationNamespace, string? tenant)
    {
        var existing = new MetadataV2ObjectMetadata
        {
            Id = "dependency", Name = "Original", Namespace = publicationNamespace, Tenant = tenant,
            Title = "Retained title", Attribution = "Retained credit"
        };
        var created = new MetadataV2ObjectMetadata { Id = "dependency", Name = "Replacement" };
        PostgreSqlLayerPublishingService.PreserveDependencyMetadata(existing, created, new("maps", "owner"))
            .Should().BeSameAs(existing);
        PostgreSqlLayerPublishingService.PreserveDependencyMetadata(null, created, new("maps", "owner"))
            .Should().BeSameAs(created);
        var foreign = () => PostgreSqlLayerPublishingService.PreserveDependencyMetadata(
            existing with { Tenant = "foreign" }, created, new("maps", "owner"));
        foreign.Should().Throw<LayerPublishingException>().Which.ErrorKind.Should().Be(LayerPublishingErrorKind.NotFound);
    }
}
