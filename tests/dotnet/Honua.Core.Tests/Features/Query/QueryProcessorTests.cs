// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Core.Tests.Features.Query;

/// <summary>
/// Unit tests for <see cref="QueryProcessor"/>.
/// </summary>
public sealed class QueryProcessorTests
{
    private readonly QueryProcessor _processor;
    private readonly MetadataV2Resource _resource = new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "res.test", Name = "test" },
        Type = MetadataV2ResourceType.FeatureDataset,
        Spatial = new MetadataV2ResourceSpatial
        {
            GeometryType = MetadataV2GeometryType.Point,
            SpatialReference = MetadataV2SpatialReference.Wgs84
        },
        SchemaFields =
        [
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = false },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }
        ]
    };

    public QueryProcessorTests()
    {
        var filterTranslator = new Mock<IFilterExpressionTranslator>(MockBehavior.Strict);
        var featureReader = new Mock<IFeatureReader>(MockBehavior.Strict);

        _processor = new QueryProcessor(
            filterTranslator.Object,
            featureReader.Object,
            NullLogger<QueryProcessor>.Instance);
    }

    [Theory]
    [InlineData("javascript:alert('xss')")]
    [InlineData("<script>alert('xss')</script>")]
    [InlineData("<img src=x onerror=alert('xss')>")]
    public void ValidateQuery_InvalidOutFields_SanitizesReflectedFieldNames(string payload)
    {
        var query = new UnifiedQuery
        {
            OutFields = ImmutableArray.Create(payload)
        };

        var result = _processor.ValidateQuery(query, _resource);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("Unknown fields:");
        result.ErrorMessage.Should().NotContain("javascript:");
        result.ErrorMessage.Should().NotContain("<script>");
        result.ErrorMessage.Should().NotContain("onerror=");
    }

    [Theory]
    [InlineData("javascript:alert('xss')")]
    [InlineData("<svg onload=alert('xss')>")]
    public void ValidateQuery_InvalidOrderByFields_SanitizesReflectedFieldNames(string payload)
    {
        var query = new UnifiedQuery
        {
            OrderBy = ImmutableArray.Create(new OrderByClause(payload))
        };

        var result = _processor.ValidateQuery(query, _resource);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("Unknown order by fields:");
        result.ErrorMessage.Should().NotContain("javascript:");
        result.ErrorMessage.Should().NotContain("<svg");
        result.ErrorMessage.Should().NotContain("onload=");
    }

    [Fact]
    public void BuildCacheKey_FilterParametersWithDelimiter_DoNotCollide()
    {
        var firstQuery = UnifiedQuery.WithFilter(QueryFilter.FromSql(
            new SqlFragment("name IN (@p0, @p1)", ["a,b", "c"])));
        var secondQuery = UnifiedQuery.WithFilter(QueryFilter.FromSql(
            new SqlFragment("name IN (@p0, @p1)", ["a", "b,c"])));

        var firstKey = _processor.BuildCacheKey(firstQuery, _resource, "test");
        var secondKey = _processor.BuildCacheKey(secondQuery, _resource, "test");

        firstKey.Should().NotBe(secondKey);
    }

    [Fact]
    public void BuildCacheKey_ResultAffectingExtensions_DoNotCollide()
    {
        var firstQuery = new UnifiedQuery
        {
            Extensions = ImmutableDictionary<string, object>.Empty.Add("includeNullGeometry", false)
        };
        var secondQuery = firstQuery with
        {
            Extensions = ImmutableDictionary<string, object>.Empty.Add("includeNullGeometry", true)
        };

        var firstKey = _processor.BuildCacheKey(firstQuery, _resource, "OGC-API-Features");
        var secondKey = _processor.BuildCacheKey(secondQuery, _resource, "OGC-API-Features");

        firstKey.Should().NotBe(secondKey);
    }

    [Fact]
    public void BuildCacheKey_ResultAffectingHints_DoNotCollide()
    {
        var firstQuery = new UnifiedQuery
        {
            Hints = QueryHints.Create(preferStreaming: false)
        };
        var secondQuery = firstQuery with
        {
            Hints = QueryHints.Create(preferStreaming: true)
        };

        var firstKey = _processor.BuildCacheKey(firstQuery, _resource, "OGC-API-Features");
        var secondKey = _processor.BuildCacheKey(secondQuery, _resource, "OGC-API-Features");

        firstKey.Should().NotBe(secondKey);
    }

    [Fact]
    public void BuildCacheKey_TemporalFilterEndPropertyName_DoesNotCollide()
    {
        // EndPropertyName changes the SQL predicate from instant filtering on
        // PropertyName alone to interval intersection over [PropertyName,
        // COALESCE(EndPropertyName, PropertyName)] (#379). Two filters that
        // differ only by EndPropertyName produce different result sets, so
        // the cache key must distinguish them; otherwise an interval-style
        // request would collide with the instant-style cache entry.
        var instantStart = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var instantEnd = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero);

        var instantFilterQuery = new UnifiedQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "start_time",
                PropertyType = TemporalPropertyType.DateTime,
                Start = instantStart,
                End = instantEnd
            }
        };

        var intervalFilterQuery = instantFilterQuery with
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "start_time",
                PropertyType = TemporalPropertyType.DateTime,
                EndPropertyName = "end_time",
                Start = instantStart,
                End = instantEnd
            }
        };

        var instantKey = _processor.BuildCacheKey(instantFilterQuery, _resource, "test");
        var intervalKey = _processor.BuildCacheKey(intervalFilterQuery, _resource, "test");

        instantKey.Should().NotBe(intervalKey);
    }

    [Fact]
    public void ToFeatureQuery_IncludeNullGeometryExtension_PropagatesToFeatureQuery()
    {
        var query = new UnifiedQuery
        {
            Extensions = ImmutableDictionary<string, object>.Empty.Add("includeNullGeometry", true)
        };

        var featureQuery = _processor.ToFeatureQuery(query, _resource);

        featureQuery.IncludeNullGeometry.Should().BeTrue();
    }
}
