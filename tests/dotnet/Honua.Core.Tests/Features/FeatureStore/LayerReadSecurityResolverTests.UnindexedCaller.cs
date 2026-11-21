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
    private const int UnindexedLayerId = LayerId + 1000;

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task UnindexedLayer_CallerRead_IsRefused()
    {
        var resolver = CreateCallerResolver();

        var read = () => resolver.ApplyAsync(UnindexedLayerId, new FeatureQuery(), CancellationToken.None);

        await read.Should().ThrowAsync<ResourceAccessDeniedException>();
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task UnindexedLayer_ServerInternalRead_StillReads()
    {
        var resolver = CreateServerInternalResolver();

        var read = await resolver.ApplyAsync(UnindexedLayerId, new FeatureQuery(), CancellationToken.None);

        read.EnforcedSqlFilter.Should().BeNull();
        read.EnforcedMaskedFields.Should().BeNull();
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task IndexedLayer_CallerRead_AppliesPolicy()
    {
        var resolver = CreateCallerResolver();

        var read = await resolver.ApplyAsync(LayerId, new FeatureQuery(), CancellationToken.None);

        read.EnforcedSqlFilter.Should().NotBeNull();
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task UnindexedLayer_NoMetadataIndex_StillReads()
    {
        var rows = new Mock<IRowLevelSecurityFilterSource>();
        rows.SetupGet(source => source.IsCallerRead).Returns(true);
        var resolver = new LayerReadSecurityResolver(null, null, rows.Object, null);

        var read = await resolver.ApplyAsync(UnindexedLayerId, new FeatureQuery(), CancellationToken.None);

        read.EnforcedSqlFilter.Should().BeNull();
    }

    private static LayerReadSecurityResolver CreateCallerResolver()
        => CreateResolverWithRows(rows => rows.SetupGet(source => source.IsCallerRead).Returns(true));

    private static LayerReadSecurityResolver CreateServerInternalResolver()
        => CreateResolverWithRows(rows => rows.SetupGet(source => source.IsCallerRead).Returns(false));

    private static LayerReadSecurityResolver CreateResolverWithRows(Action<Mock<IRowLevelSecurityFilterSource>> configure)
    {
        var rows = new Mock<IRowLevelSecurityFilterSource>();
        configure(rows);
        rows.Setup(source => source.ResolveAsync(It.IsAny<MetadataV2Object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SqlFragment("region = @p0", ["north"]));
        var masks = new Mock<IFieldMaskSource>();
        masks.Setup(source => source.ResolveAsync(It.IsAny<MetadataV2Object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImmutableArray<string>.Empty);
        return new LayerReadSecurityResolver(
            new StubGraphProvider(CreateUnboundSnapshot(ambiguous: false)), null, rows.Object, masks.Object);
    }
}
