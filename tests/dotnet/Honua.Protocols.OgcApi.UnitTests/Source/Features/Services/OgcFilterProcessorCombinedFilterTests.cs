// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// Unit tests for the combined-filter building logic in <see cref="OgcFilterProcessor"/>
/// (BH6-007): a user-supplied CQL2 filter that contains a top-level OR must be
/// parenthesized before being AND-combined with queryable parameter predicates.
/// Without parentheses the CQL2 precedence rule `AND > OR` silently bypasses the
/// queryable restriction for the first OR branch.
/// </summary>
public sealed class OgcFilterProcessorCombinedFilterTests
{
    private static readonly MetadataV2Resource Resource = new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "test-res", Name = "Test" },
        Type = MetadataV2ResourceType.FeatureDataset,
        SchemaFields =
        [
            new MetadataV2Field { Name = "country", Type = MetadataV2FieldType.String },
            new MetadataV2Field { Name = "status", Type = MetadataV2FieldType.String },
            new MetadataV2Field { Name = "priority", Type = MetadataV2FieldType.Integer }
        ]
    };

    /// <summary>
    /// A user filter containing OR must be wrapped in parentheses when combined with a
    /// queryable field parameter so the AND applies to the whole OR expression.
    /// Before the fix the combined filter was:
    ///   status = 'active' OR priority > 5 AND country = 'France'
    /// After the fix it is:
    ///   (status = 'active' OR priority > 5) AND country = 'France'
    /// </summary>
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

    /// <summary>
    /// A user filter without OR does not need parentheses, but they are harmless — the
    /// parenthesized form is still valid CQL2 text and produces the correct predicate.
    /// </summary>
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

    /// <summary>
    /// When there is no user filter at all, only the queryable parameter predicate is used
    /// (no parens needed around an absent filter).
    /// </summary>
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

        public FilterParseResult ParseAndNormalize(FilterLanguage language, string? filter, MetadataV2Resource resource)
            => Parse(language, filter);

        public FilterExpression Normalize(FilterExpression expression, MetadataV2Resource resource)
            => throw new NotSupportedException("Not exercised by these tests.");

        public FilterTranslationResult Translate(FilterExpression? expression, MetadataV2Resource resource)
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
