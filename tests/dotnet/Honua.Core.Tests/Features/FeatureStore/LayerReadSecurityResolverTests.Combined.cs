// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Moq;

namespace Honua.Core.Tests.Features.FeatureStore;

public sealed partial class LayerReadSecurityResolverTests
{
    [Fact]
    public async Task ApplyAsync_CompatibleSources_CombinesPermanentFilterAndFreshPairedRestrictions()
    {
        var resource = CreateResource(withPermanentFilter: true);
        var rows = new Mock<IRowLevelSecurityFilterSource>(MockBehavior.Strict);
        var joint = rows.As<ICombinedReadSecuritySource>();
        var masks = new Mock<IFieldMaskSource>(MockBehavior.Strict);
        joint.Setup(s => s.CanResolveWith(masks.Object)).Returns(true);
        joint.Setup(s => s.ResolveCombinedAsync(resource, masks.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadSecurityResolution(new SqlFragment("region = @p0", ["west"]), ["secret"]));
        var filters = new Mock<IFilterExpressionService>();
        var expression = new PropertyReference("status");
        filters.Setup(s => s.ParseAndNormalize(It.IsAny<FilterLanguage>(), It.IsAny<string?>(), resource))
            .Returns(FilterParseResult.Success(expression));
        filters.Setup(s => s.Translate(expression, resource))
            .Returns(FilterTranslationResult.Success(expression, new SqlFragment("status = @p0", ["active"])));
        var resolver = new LayerReadSecurityResolver(null, filters.Object, rows.Object, masks.Object);

        var result = await resolver.ApplyAsync(resource, new FeatureQuery(), CancellationToken.None);
        result.EnforcedSqlFilter!.Sql.Should().Be("(status = @p0) AND (region = @p1)");
        result.EnforcedSqlFilter.Parameters.Should().Equal("active", "west");
        result.EnforcedMaskedFields.Should().Equal(ImmutableArray.Create("secret"));

        joint.Setup(s => s.ResolveCombinedAsync(resource, masks.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadSecurityResolution(new SqlFragment("region = @p0", ["east"]), ["new_secret"]));
        result = await resolver.ApplyAsync(resource, new FeatureQuery(), CancellationToken.None);
        result.EnforcedSqlFilter!.Parameters.Should().Equal("active", "east");
        result.EnforcedMaskedFields.Should().Equal(ImmutableArray.Create("new_secret"));
        joint.Verify(s => s.ResolveCombinedAsync(resource, masks.Object, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyAsync_OneRestrictionAlreadyEnforced_ResolvesOtherConcernIndependently(bool hasRowFilter)
    {
        var resource = CreateResource(false);
        var rows = new Mock<IRowLevelSecurityFilterSource>(MockBehavior.Strict);
        var joint = rows.As<ICombinedReadSecuritySource>();
        var masks = new Mock<IFieldMaskSource>(MockBehavior.Strict);
        rows.Setup(s => s.ResolveAsync(resource, It.IsAny<CancellationToken>())).ReturnsAsync(new SqlFragment("TRUE", []));
        masks.Setup(s => s.ResolveAsync(resource, It.IsAny<CancellationToken>())).ReturnsAsync(ImmutableArray.Create("secret"));
        var query = hasRowFilter
            ? new FeatureQuery { EnforcedSqlFilter = new SqlFragment("FALSE", []) }
            : new FeatureQuery { EnforcedMaskedFields = ImmutableArray.Create("existing") };

        var result = await new LayerReadSecurityResolver(null, null, rows.Object, masks.Object)
            .ApplyAsync(resource, query, CancellationToken.None);

        result.EnforcedSqlFilter!.Sql.Should().Be(hasRowFilter ? "FALSE" : "TRUE");
        result.EnforcedMaskedFields.Should().Equal(ImmutableArray.Create(hasRowFilter ? "secret" : "existing"));
        joint.Verify(s => s.ResolveCombinedAsync(It.IsAny<MetadataV2Resource>(), It.IsAny<IFieldMaskSource>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyAsync_IncompatibleSources_UsesRegisteredIndependentSources()
    {
        var resource = CreateResource(false);
        var rows = new Mock<IRowLevelSecurityFilterSource>();
        var joint = rows.As<ICombinedReadSecuritySource>();
        var masks = new Mock<IFieldMaskSource>();
        joint.Setup(s => s.CanResolveWith(masks.Object)).Returns(false);
        rows.Setup(s => s.ResolveAsync(resource, It.IsAny<CancellationToken>())).ReturnsAsync(new SqlFragment("FALSE", []));
        masks.Setup(s => s.ResolveAsync(resource, It.IsAny<CancellationToken>())).ReturnsAsync(ImmutableArray.Create("secret"));
        var result = await new LayerReadSecurityResolver(null, null, rows.Object, masks.Object)
            .ApplyAsync(resource, new FeatureQuery(), CancellationToken.None);
        result.EnforcedSqlFilter!.Sql.Should().Be("FALSE");
        result.EnforcedMaskedFields.Should().Equal(ImmutableArray.Create("secret"));
        joint.Verify(s => s.ResolveCombinedAsync(It.IsAny<MetadataV2Resource>(), It.IsAny<IFieldMaskSource>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyAsync_CombinedMasks_StillRejectsMaskedQueryExpressions()
    {
        var resource = CreateResource(false);
        var rows = new Mock<IRowLevelSecurityFilterSource>();
        var joint = rows.As<ICombinedReadSecuritySource>();
        var masks = new Mock<IFieldMaskSource>();
        joint.Setup(s => s.CanResolveWith(masks.Object)).Returns(true);
        joint.Setup(s => s.ResolveCombinedAsync(resource, masks.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadSecurityResolution(null, ["secret"]));
        var resolver = new LayerReadSecurityResolver(null, null, rows.Object, masks.Object);
        var query = new FeatureQuery { GroupByFields = ["secret"] };
        await Assert.ThrowsAsync<ArgumentException>(() => resolver.ApplyAsync(resource, query, CancellationToken.None));
    }
}
