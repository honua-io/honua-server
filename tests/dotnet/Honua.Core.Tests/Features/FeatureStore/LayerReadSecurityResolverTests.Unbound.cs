// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions.Execution;
using Honua.Core.Exceptions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Moq;

namespace Honua.Core.Tests.Features.FeatureStore;

public sealed partial class LayerReadSecurityResolverTests
{
    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("row")]
    [InlineData("mask")]
    public async Task UnboundPublication_UniqueHandle_AppliesResourcePolicy(string policy)
    {
        var resolver = CreateUnboundResolver(policy, ambiguous: false);

        var read = await resolver.ApplyAsync(LayerId, new FeatureQuery(), CancellationToken.None);
        var write = await resolver.ResolveRowLevelSecurityFilterAsync(LayerId, CancellationToken.None);

        using var scope = new AssertionScope();
        if (policy == "row")
        {
            read.EnforcedSqlFilter.Should().NotBeNull();
            write.Should().NotBeNull();
        }
        else
        {
            read.EnforcedMaskedFields.Should().NotBeNull();
            if (read.EnforcedMaskedFields.HasValue)
            {
                read.EnforcedMaskedFields.Value.Should().Contain("secret");
            }
        }
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("row")]
    [InlineData("mask")]
    public async Task UnboundPublication_AmbiguousHandle_FailsClosed(string policy)
    {
        var resolver = CreateUnboundResolver(policy, ambiguous: true);

        var read = () => resolver.ApplyAsync(LayerId, new FeatureQuery(), CancellationToken.None);
        var write = () => resolver.ResolveRowLevelSecurityFilterAsync(LayerId, CancellationToken.None);

        using var scope = new AssertionScope();
        await read.Should().ThrowAsync<ResourceAccessDeniedException>().WithMessage("The operation is not permitted for this resource.");
        await write.Should().ThrowAsync<ResourceAccessDeniedException>().WithMessage("The operation is not permitted for this resource.");
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task UnboundPublication_PolicyFreeAliases_PreserveCompatibility()
    {
        var resolver = CreateUnboundResolver("none", ambiguous: true);

        var read = await resolver.ApplyAsync(LayerId, new FeatureQuery(), CancellationToken.None);
        var write = await resolver.ResolveRowLevelSecurityFilterAsync(LayerId, CancellationToken.None);

        read.EnforcedSqlFilter.Should().BeNull();
        read.EnforcedMaskedFields.Should().BeNull();
        write.Should().BeNull();
    }

    private static LayerReadSecurityResolver CreateUnboundResolver(string policy, bool ambiguous)
    {
        var snapshot = CreateUnboundSnapshot(ambiguous);
        var rows = new Mock<IRowLevelSecurityFilterSource>();
        rows.Setup(source => source.ResolveAsync(It.IsAny<MetadataV2Object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(policy == "row" ? new SqlFragment("region = @p0", ["north"]) : null);
        var masks = new Mock<IFieldMaskSource>();
        masks.Setup(source => source.ResolveAsync(It.IsAny<MetadataV2Object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(policy == "mask" ? ImmutableArray.Create("secret") : ImmutableArray<string>.Empty);
        return new LayerReadSecurityResolver(new StubGraphProvider(snapshot), null, rows.Object, masks.Object);
    }

    private static MetadataV2GraphSnapshot CreateUnboundSnapshot(bool ambiguous)
    {
        var resource = CreateResource(withPermanentFilter: false) with { StorageBindingIds = [], PrimaryStorageBindingId = null };
        MetadataV2Object[] resources = ambiguous
            ? [resource, resource with { Metadata = resource.Metadata with { Id = "other-resource", Name = "other-resource" } }]
            : [resource];
        var services = resources.Select(item => new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = $"service-{item.Metadata.Id}", Name = $"service-{item.Metadata.Id}" },
            Protocols = [ServiceProtocols.OData],
            SpatialReference = MetadataV2SpatialReference.Wgs84
        }).ToArray();
        var publications = resources.Select((item, index) => new MetadataV2Layer
        {
            Metadata = new MetadataV2ObjectMetadata { Id = $"pub-{item.Metadata.Id}", Name = item.Metadata.Name },
            ServiceId = services[index].Metadata.Id,
            ResourceId = item.Metadata.Id,
            PublicationType = MetadataV2LayerType.ODataEntitySet,
            LayerIndex = LayerId
        }).ToArray();
        var graph = new MetadataV2Graph { Revision = 1, Resources = resources, Services = services, Publications = publications };
        MetadataV2GraphValidator.Validate(graph).IsValid.Should().BeTrue();
        var snapshot = new MetadataV2GraphSnapshot(graph, "unbound", DateTimeOffset.UtcNow);
        snapshot.ResolveStorageLayerId(publications[0], resource).Should().Be(LayerId);
        return snapshot;
    }
}
