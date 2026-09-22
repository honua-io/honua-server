// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Attributes;
using Moq;

namespace Honua.Core.Tests.Queries.Filters;

/// <summary>
/// The shared parse seam reports an over-long flat logical sequence as an ordinary
/// parse failure in every filter language, before any caller receives a tree.
/// </summary>
public sealed class FilterExpressionServiceParseBoundsTests
{
    private const int OperandsBeyondLimit = FilterParserGuard.MaxExpressionDepth + 1;

    private readonly FilterExpressionService _service = new(Mock.Of<IFilterExpressionTranslator>());

    public static TheoryData<FilterLanguage, string> FlatLogicalSequences => new()
    {
        { FilterLanguage.Cql2Text, string.Join(" AND ", Enumerable.Repeat("name = 'a'", OperandsBeyondLimit)) },
        {
            FilterLanguage.Cql2Json,
            """{"op":"and","args":[""" +
            string.Join(",", Enumerable.Repeat("""{"op":"=","args":[{"property":"name"},"a"]}""", OperandsBeyondLimit)) +
            "]}"
        },
        { FilterLanguage.OData, string.Join(" and ", Enumerable.Repeat("Name eq 'a'", OperandsBeyondLimit)) },
        { FilterLanguage.ArcGisSql, string.Join(" AND ", Enumerable.Repeat("name = 'a'", OperandsBeyondLimit)) }
    };

    [Theory]
    [MemberData(nameof(FlatLogicalSequences))]
    public void Parse_FlatLogicalSequenceBeyondOperandLimit_ReturnsFailure(FilterLanguage language, string filter)
    {
        var result = _service.Parse(language, filter);

        result.IsSuccess.Should().BeFalse();
        result.Expression.Should().BeNull();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public void Parse_FlatLogicalSequenceWithinDepthLimit_ReturnsExpression()
    {
        var filter = string.Join(" AND ", Enumerable.Repeat("name = 'a'", FilterParserGuard.MaxExpressionDepth - 1));

        var result = _service.Parse(FilterLanguage.ArcGisSql, filter);

        result.IsSuccess.Should().BeTrue();
        result.Expression.Should().BeOfType<BinaryExpression>();
    }
}
