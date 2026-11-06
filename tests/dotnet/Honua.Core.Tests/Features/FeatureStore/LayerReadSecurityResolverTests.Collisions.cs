// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
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
    [InlineData("permanent")]
    [InlineData("row-failure")]
    [InlineData("mask-failure")]
    public async Task ApplyAsync_NumericCollisionWithApplicablePolicy_FailsClosed(string policy)
    {
        var (resolver, _) = CreateCollisionResolver(policy);

        var read = () => resolver.ApplyAsync(LayerId, new FeatureQuery(), CancellationToken.None);

        await AssertNumericCollisionDeniedAsync(read, policy);
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("row")]
    [InlineData("mask")]
    [InlineData("permanent")]
    [InlineData("row-failure")]
    [InlineData("mask-failure")]
    public async Task ResolveRowLevelSecurityFilterAsync_NumericCollisionWithApplicablePolicy_FailsClosed(string policy)
    {
        var (resolver, _) = CreateCollisionResolver(policy);

        var resolve = () => resolver.ResolveRowLevelSecurityFilterAsync(LayerId, CancellationToken.None);

        await AssertNumericCollisionDeniedAsync(resolve, policy);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task NumericCollision_WithoutApplicablePolicy_PreservesReadAndWriteCompatibility()
    {
        var (resolver, _) = CreateCollisionResolver("none");

        var read = await resolver.ApplyAsync(LayerId, new FeatureQuery(), CancellationToken.None);
        var write = await resolver.ResolveRowLevelSecurityFilterAsync(LayerId, CancellationToken.None);

        read.EnforcedSqlFilter.Should().BeNull();
        read.EnforcedMaskedFields.Should().BeNull();
        write.Should().BeNull();
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task ApplyAsync_ExactResourceWithStorageCollision_EnforcesItsOwnRowAndFieldPolicy()
    {
        var (resolver, resource) = CreateCollisionResolver("row-mask");

        var read = await resolver.ApplyAsync(resource, new FeatureQuery(), CancellationToken.None);

        read.EnforcedSqlFilter.Should().NotBeNull();
        read.EnforcedMaskedFields.Should().NotBeNull();
        read.EnforcedMaskedFields!.Value.Should().Contain("secret");
    }

    private static async Task AssertNumericCollisionDeniedAsync(Func<Task> attempt, string policy)
    {
        if (policy.EndsWith("-failure", StringComparison.Ordinal))
        {
            await attempt.Should().ThrowAsync<InvalidOperationException>().WithMessage("Policy source unavailable.");
        }
        else
        {
            await attempt.Should().ThrowAsync<ResourceAccessDeniedException>()
                .WithMessage("The operation is not permitted for this resource.");
        }
    }

    private static (LayerReadSecurityResolver Resolver, MetadataV2Resource Resource) CreateCollisionResolver(string policy)
    {
        var restricted = CreateResource(withPermanentFilter: policy == "permanent");
        var first = restricted with
        {
            Metadata = restricted.Metadata with { Id = "unrestricted-first", Name = "unrestricted-first" },
            StorageBindingIds = ["first-binding"],
            PermanentFilter = null
        };
        var bindings = new[] { first, restricted }.Select(resource => new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = resource.StorageBindingIds![0], Name = resource.Metadata.Name },
            ResourceId = resource.Metadata.Id,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "honua.features",
            StorageLayerId = LayerId
        }).ToArray();
        var graph = new MetadataV2Graph { Revision = 1, Resources = [first, restricted], StorageBindings = bindings };
        MetadataV2GraphValidator.Validate(graph).IsValid.Should().BeTrue();
        var rows = new Mock<IRowLevelSecurityFilterSource>();
        rows.Setup(source => source.ResolveAsync(It.IsAny<MetadataV2Resource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SqlFragment?)null);
        var restrictedRow = rows.Setup(source => source.ResolveAsync(
            It.Is<MetadataV2Resource>(resource => resource.Metadata.Id == restricted.Metadata.Id), It.IsAny<CancellationToken>()));
        if (policy == "row-failure")
        {
            restrictedRow.ThrowsAsync(new InvalidOperationException("Policy source unavailable."));
        }
        else
        {
            restrictedRow.ReturnsAsync(policy is "row" or "row-mask" ? new SqlFragment("region = @p0", ["north"]) : null);
        }
        var masks = new Mock<IFieldMaskSource>();
        masks.Setup(source => source.ResolveAsync(It.IsAny<MetadataV2Resource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImmutableArray<string>.Empty);
        var restrictedMask = masks.Setup(source => source.ResolveAsync(
            It.Is<MetadataV2Resource>(resource => resource.Metadata.Id == restricted.Metadata.Id), It.IsAny<CancellationToken>()));
        if (policy == "mask-failure")
        {
            restrictedMask.ThrowsAsync(new InvalidOperationException("Policy source unavailable."));
        }
        else
        {
            restrictedMask.ReturnsAsync(policy is "mask" or "row-mask" ? ImmutableArray.Create("secret") : ImmutableArray<string>.Empty);
        }
        var provider = new StubGraphProvider(new MetadataV2GraphSnapshot(graph, "collision", DateTimeOffset.UtcNow));
        return (new LayerReadSecurityResolver(provider, null, policy is "mask" or "permanent" ? null : rows.Object, masks.Object), restricted);
    }
}
