// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Protocols.OData.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// Unit tests for the OData v4 $orderby null-placement mandate (OData v4.01 Protocol
/// §11.2.6.2): nulls before non-null values when ascending, after them when descending.
/// The OData adapter is the only protocol adapter that requests an explicit
/// <see cref="NullOrdering"/>; every other adapter leaves the provider default.
/// </summary>
public sealed class ODataQueryParameterAdapterOrderByTests
{
    private readonly ODataQueryParameterAdapter _adapter = new(
        new StubFilterExpressionService(),
        NullLogger<ODataQueryParameterAdapter>.Instance);

    private static MetadataV2Resource CreateResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-test", Name = "Test" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields =
            [
                new MetadataV2Field { Name = "population", Type = MetadataV2FieldType.Integer },
                new MetadataV2Field { Name = "state", Type = MetadataV2FieldType.String }
            ]
        };

    [Fact]
    public async Task ConvertAsync_OrderByAscending_RequestsNullsFirst()
    {
        var result = await _adapter.ConvertAsync(
            new ODataQueryParameters { OrderBy = "population asc" },
            CreateResource());

        result.IsSuccess.Should().BeTrue();
        var clause = result.Query!.Value.OrderBy!.Value.Should().ContainSingle().Subject;
        clause.Ascending.Should().BeTrue();
        clause.NullOrdering.Should().Be(NullOrdering.NullsFirst);
    }

    [Fact]
    public async Task ConvertAsync_OrderByDescending_RequestsNullsLast()
    {
        var result = await _adapter.ConvertAsync(
            new ODataQueryParameters { OrderBy = "population desc" },
            CreateResource());

        result.IsSuccess.Should().BeTrue();
        var clause = result.Query!.Value.OrderBy!.Value.Should().ContainSingle().Subject;
        clause.Ascending.Should().BeFalse();
        clause.NullOrdering.Should().Be(NullOrdering.NullsLast);
    }

    [Fact]
    public async Task ConvertAsync_MultipleOrderByClauses_RequestPlacementPerDirection()
    {
        var result = await _adapter.ConvertAsync(
            new ODataQueryParameters { OrderBy = "state asc,population desc" },
            CreateResource());

        result.IsSuccess.Should().BeTrue();
        var clauses = result.Query!.Value.OrderBy!.Value;
        clauses.Should().HaveCount(2);
        clauses[0].NullOrdering.Should().Be(NullOrdering.NullsFirst);
        clauses[1].NullOrdering.Should().Be(NullOrdering.NullsLast);
    }

    [Fact]
    public async Task ConvertAsync_NoOrderBy_DefaultObjectIdClauseKeepsProviderNullPlacement()
    {
        // The implicit ObjectId tiebreaker orders a non-null primary key — no explicit
        // null placement is requested so the SQL stays unchanged for that path.
        var result = await _adapter.ConvertAsync(
            new ODataQueryParameters(),
            CreateResource());

        result.IsSuccess.Should().BeTrue();
        var clause = result.Query!.Value.OrderBy!.Value.Should().ContainSingle().Subject;
        clause.NullOrdering.Should().Be(NullOrdering.Default);
    }

    private sealed class StubFilterExpressionService : IFilterExpressionService
    {
        public FilterParseResult Parse(FilterLanguage language, string? filter)
            => throw new NotSupportedException("Filter parsing is not exercised by these tests.");

        public FilterParseResult ParseAndNormalize(FilterLanguage language, string? filter, MetadataV2Resource resource)
            => throw new NotSupportedException("Filter parsing is not exercised by these tests.");

        public FilterExpression Normalize(FilterExpression expression, MetadataV2Resource resource)
            => throw new NotSupportedException("Filter normalization is not exercised by these tests.");

        public FilterTranslationResult Translate(FilterExpression? expression, MetadataV2Resource resource)
            => throw new NotSupportedException("Filter translation is not exercised by these tests.");
    }
}
