// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Protocols.OData;
using Honua.Protocols.OData.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.OData;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class ODataQueryParameterAdapterOrderByTests
{
    private readonly ODataQueryParameterAdapter _adapter = new(
        new StubFilterExpressionService(),
        Options.Create(new ODataOptions()),
        NullLogger<ODataQueryParameterAdapter>.Instance);

    private static MetadataV2Object CreateResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-test", Name = "Test" },
            Type = MetadataV2ObjectType.FeatureDataset,
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

    [Fact]
    public async Task ConvertAsync_SelectOrOrderByHiddenField_FailsValidation()
    {
        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-hidden", Name = "HiddenTest" },
            Type = MetadataV2ObjectType.FeatureDataset,
            SchemaFields =
            [
                new MetadataV2Field { Name = "state", Type = MetadataV2FieldType.String, Hidden = false },
                new MetadataV2Field { Name = "internal_secret", Type = MetadataV2FieldType.String, Hidden = true }
            ]
        };

        var selectResult = await _adapter.ConvertAsync(
            new ODataQueryParameters { Select = "internal_secret" },
            resource);
        var orderByResult = await _adapter.ConvertAsync(
            new ODataQueryParameters { OrderBy = "internal_secret asc" },
            resource);

        selectResult.IsSuccess.Should().BeFalse();
        orderByResult.IsSuccess.Should().BeFalse();
    }

    private sealed class StubFilterExpressionService : IFilterExpressionService
    {
        public FilterParseResult Parse(FilterLanguage language, string? filter)
            => throw new NotSupportedException("Filter parsing is not exercised by these tests.");

        public FilterParseResult ParseAndNormalize(FilterLanguage language, string? filter, MetadataV2Object resource)
            => throw new NotSupportedException("Filter parsing is not exercised by these tests.");

        public FilterExpression Normalize(FilterExpression expression, MetadataV2Object resource)
            => throw new NotSupportedException("Filter normalization is not exercised by these tests.");

        public FilterTranslationResult Translate(FilterExpression? expression, MetadataV2Object resource)
            => throw new NotSupportedException("Filter translation is not exercised by these tests.");
    }
}
