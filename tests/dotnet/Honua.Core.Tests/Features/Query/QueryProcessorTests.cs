// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
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
    private readonly LayerDefinition _layer = new(
        Id: 1,
        Name: "test",
        Description: null,
        GeometryType: GeometryType.Point,
        SpatialReference: SpatialReference.WGS84,
        Fields:
        [
            new(FieldNames.ObjectId, FieldType.Integer, Nullable: false),
            new("shape", FieldType.Geometry, Nullable: false),
            new("name", FieldType.String)
        ]);

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

        var result = _processor.ValidateQuery(query, _layer);

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

        var result = _processor.ValidateQuery(query, _layer);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("Unknown order by fields:");
        result.ErrorMessage.Should().NotContain("javascript:");
        result.ErrorMessage.Should().NotContain("<svg");
        result.ErrorMessage.Should().NotContain("onload=");
    }
}
