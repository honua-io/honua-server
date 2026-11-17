// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Moq;

namespace Honua.Core.Tests.Features.FeatureStore;

public sealed partial class LayerReadSecurityResolverTests
{
    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(null, FilterLanguage.GeoServicesSql)]
    [InlineData("arcgis-sql", FilterLanguage.GeoServicesSql)]
    [InlineData(" ARCGIS ", FilterLanguage.GeoServicesSql)]
    [InlineData("geoservices-sql", FilterLanguage.GeoServicesSql)]
    [InlineData("cql2-text", FilterLanguage.Cql2Text)]
    [InlineData(" CQL2 ", FilterLanguage.Cql2Text)]
    [InlineData("cql2-json", FilterLanguage.Cql2Json)]
    public void ParseSavedExpression_LegacyLanguageAliases_ReachTheExpectedParser(string? language, FilterLanguage expected)
    {
        var resource = CreateResource(true) with
        {
            PermanentFilter = new MetadataV2PermanentFilter { Expression = "status = 'active'", Language = language! }
        };
        var filters = new Mock<IFilterExpressionService>(MockBehavior.Strict);
        var expression = new PropertyReference("status");
        filters.Setup(service => service.ParseAndNormalize(expected, resource.PermanentFilter.Expression, resource))
            .Returns(FilterParseResult.Success(expression));

        PermanentFilterResolver.ParseSavedExpression(resource, filters.Object).Should().BeSameAs(expression);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("sql")]
    public void ParseSavedExpression_UnsupportedLanguage_FailsBeforeParsing(string language)
    {
        var resource = CreateResource(true) with
        {
            PermanentFilter = new MetadataV2PermanentFilter { Expression = "status = 'active'", Language = language }
        };
        var filters = new Mock<IFilterExpressionService>(MockBehavior.Strict);

        var act = () => PermanentFilterResolver.ParseSavedExpression(resource, filters.Object);

        act.Should().Throw<InvalidOperationException>().WithMessage("*unsupported language*");
        filters.VerifyNoOtherCalls();
    }
}
