// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features.Services;

public sealed class OgcFilterProcessorCombinedFilterTests
{
    private static readonly MetadataV2Object Resource = new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "test-res", Name = "Test" },
        Type = MetadataV2ObjectType.FeatureDataset,
        SchemaFields =
        [
            new MetadataV2Field { Name = "country", Type = MetadataV2FieldType.String },
            new MetadataV2Field { Name = "status", Type = MetadataV2FieldType.String },
            new MetadataV2Field { Name = "priority", Type = MetadataV2FieldType.Integer }
        ]
    };

    [UnitTest]
    public async Task ProcessFiltersAsync_OrFilterWithQueryableParam_WrapsUserFilterInParens()
    {
        var capturingService = new CapturingFilterService();
        var processor = new OgcFilterProcessor(
            capturingService,
            new StubCrsRegistry(),
            NullLogger<OgcFilterProcessor>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?country=France");

        var result = await processor.ProcessFiltersAsync(
            httpContext.Request,
            Resource,
            filter: "status = 'active' OR priority > 5",
            bbox: null,
            datetime: null,
            crs: null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);

        // The combined CQL2 text passed to Parse must have the user filter in parens.
        capturingService.LastParsedFilter.Should().Be(
            "(status = 'active' OR priority > 5) AND country = 'France'",
            "user filter with OR must be parenthesized before AND-combining with queryable predicates");
    }

    [UnitTest]
    public async Task ProcessFiltersAsync_SimpleFilterWithQueryableParam_FilterIsParenthesized()
    {
        var capturingService = new CapturingFilterService();
        var processor = new OgcFilterProcessor(
            capturingService,
            new StubCrsRegistry(),
            NullLogger<OgcFilterProcessor>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?country=Germany");

        var result = await processor.ProcessFiltersAsync(
            httpContext.Request,
            Resource,
            filter: "status = 'active'",
            bbox: null,
            datetime: null,
            crs: null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);

        capturingService.LastParsedFilter.Should().Be(
            "(status = 'active') AND country = 'Germany'");
    }

    [UnitTest]
    public async Task ProcessFiltersAsync_NoUserFilter_OnlyQueryablePredicateUsed()
    {
        var capturingService = new CapturingFilterService();
        var processor = new OgcFilterProcessor(
            capturingService,
            new StubCrsRegistry(),
            NullLogger<OgcFilterProcessor>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?country=Japan");

        var result = await processor.ProcessFiltersAsync(
            httpContext.Request,
            Resource,
            filter: null,
            bbox: null,
            datetime: null,
            crs: null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);

        capturingService.LastParsedFilter.Should().Be("country = 'Japan'");
    }

    [UnitTest]
    public void TryValidateFilterGeometries_ProjectedAndNorthEastAndGeographicBbox_EnforcesCrsAwareBounds()
    {
        var parser = new Honua.Core.Queries.Filters.Cql2.Cql2Parser();

        var projected = parser.Parse("S_INTERSECTS(geom, BBOX(385000, 5810000, 395000, 5820000))");
        Honua.Infrastructure.Filtering.Cql2FilterProcessor.TryValidateFilterGeometries(projected, out var defaultCrsError)
            .Should().BeFalse("projected coordinates are invalid when filter-crs remains geographic CRS84");
        defaultCrsError.Should().Contain("Invalid BBOX");

        var projectedWithCrs = Honua.Infrastructure.Filtering.Cql2FilterProcessor.ApplyFilterCrs(
            projected,
            new CrsDefinition("http://www.opengis.net/def/crs/EPSG/0/25833", 25833, AxisOrder.EastNorth, false));
        Honua.Infrastructure.Filtering.Cql2FilterProcessor.TryValidateFilterGeometries(projectedWithCrs, out _)
            .Should().BeTrue("projected coordinates are valid once filter-crs sets a projected SRID");

        var northEast = parser.Parse("S_INTERSECTS(geom, BBOX(35, -120, 40, -115))");
        var swapped = Honua.Infrastructure.Filtering.Cql2FilterProcessor.NormalizeFilterAxisOrder(
            northEast,
            AxisOrder.NorthEast);
        Honua.Infrastructure.Filtering.Cql2FilterProcessor.TryValidateFilterGeometries(swapped, out _)
            .Should().BeTrue("NorthEast (lat, lon) BBOX coordinates are valid after axis-order normalization");
    }

    // -------------------------------------------------------------------------
    // Test helpers
    // -------------------------------------------------------------------------

    private sealed class CapturingFilterService : IFilterExpressionService
    {
        public string? LastParsedFilter { get; private set; }

        public FilterParseResult Parse(FilterLanguage language, string? filter)
        {
            // Only capture CQL2-text calls — these carry the combined user+queryable filter.
            if (language == FilterLanguage.Cql2Text && filter is not null)
            {
                LastParsedFilter = filter;
            }

            return FilterParseResult.Success(null);
        }

        public FilterParseResult ParseAndNormalize(FilterLanguage language, string? filter, MetadataV2Object resource)
            => Parse(language, filter);

        public FilterExpression Normalize(FilterExpression expression, MetadataV2Object resource)
            => throw new NotSupportedException("Not exercised by these tests.");

        public FilterTranslationResult Translate(FilterExpression? expression, MetadataV2Object resource)
            => FilterTranslationResult.Success(null, null);
    }

    private sealed class StubCrsRegistry : ICrsRegistry
    {
        private static readonly CrsDefinition Crs84 = new(
            OgcFeaturesUtilities.Crs84Uri, 4326, AxisOrder.EastNorth, true);

        public ValueTask<CrsDefinition?> ResolveAsync(
            string? crsIdentifier, CancellationToken cancellationToken = default)
            => new((CrsDefinition?)Crs84);

        public ValueTask<CrsDefinition?> ResolveBySridAsync(
            int srid, CancellationToken cancellationToken = default)
            => new((CrsDefinition?)Crs84);

        public ValueTask<bool> IsSridSupportedAsync(
            int srid, CancellationToken cancellationToken = default)
            => new(true);
    }
}
