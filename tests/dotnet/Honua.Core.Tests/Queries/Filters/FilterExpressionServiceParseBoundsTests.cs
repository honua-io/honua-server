// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Linq;
using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Attributes;
using Moq;

namespace Honua.Core.Tests.Queries.Filters;

/// <summary>
/// The shared parse seam reports an over-long flat logical sequence, and any string
/// literal, identifier or value list beyond the shared parser limits, as an ordinary
/// parse failure in every filter language, before any caller receives a tree.
/// </summary>
public sealed class FilterExpressionServiceParseBoundsTests
{
    private const int OperandsBeyondLimit = FilterParserGuard.MaxExpressionDepth + 1;
    private const int ValuesBeyondLimit = FilterParserGuard.MaxInListSize + 1;

    private static readonly string TextBeyondLimit = new('a', FilterParserGuard.MaxStringLiteralLength + 1);
    private static readonly string TextAtLimit = new('a', FilterParserGuard.MaxStringLiteralLength);
    private static readonly string NameBeyondLimit = new('a', FilterParserGuard.MaxIdentifierLength + 1);
    private static readonly string NameAtLimit = new('a', FilterParserGuard.MaxIdentifierLength);

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

    public static TheoryData<FilterLanguage, string> StringLiteralsBeyondLimit => new()
    {
        { FilterLanguage.Cql2Text, $"name = '{TextBeyondLimit}'" },
        { FilterLanguage.Cql2Json, $$"""{"op":"=","args":[{"property":"name"},"{{TextBeyondLimit}}"]}""" },
        { FilterLanguage.OData, $"Name eq '{TextBeyondLimit}'" },
        { FilterLanguage.ArcGisSql, $"name = '{TextBeyondLimit}'" }
    };

    [UnitTheory]
    [MemberData(nameof(StringLiteralsBeyondLimit))]
    public void Parse_StringLiteralBeyondLengthLimit_ReturnsFailure(FilterLanguage language, string filter)
    {
        var result = _service.Parse(language, filter);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("maximum string literal length");
    }

    public static TheoryData<FilterLanguage, string> IdentifiersBeyondLimit => new()
    {
        { FilterLanguage.Cql2Text, $"{NameBeyondLimit} = 'a'" },
        { FilterLanguage.Cql2Json, $$"""{"op":"=","args":[{"property":"{{NameBeyondLimit}}"},"a"]}""" },
        { FilterLanguage.Cql2Json, $$"""{"op":"=","args":[{"op":"{{NameBeyondLimit}}","args":[]},"a"]}""" },
        { FilterLanguage.OData, $"{NameBeyondLimit} eq 'a'" },
        { FilterLanguage.ArcGisSql, $"{NameBeyondLimit} = 'a'" }
    };

    [UnitTheory]
    [MemberData(nameof(IdentifiersBeyondLimit))]
    public void Parse_IdentifierBeyondLengthLimit_ReturnsFailure(FilterLanguage language, string filter)
    {
        var result = _service.Parse(language, filter);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("maximum identifier length");
    }

    public static TheoryData<FilterLanguage, string> ValueListsBeyondLimit => new()
    {
        { FilterLanguage.Cql2Json, $$"""{"op":"in","args":[{"property":"name"},[{{Values(ValuesBeyondLimit)}}]]}""" },
        { FilterLanguage.Cql2Json, $$"""{"op":"=","args":[{"op":"f","args":[{{Values(ValuesBeyondLimit)}}]},1]}""" },
        { FilterLanguage.OData, $"Name in ({Values(ValuesBeyondLimit)})" },
        { FilterLanguage.OData, $"concat({Values(ValuesBeyondLimit)}) eq '1'" },
        { FilterLanguage.ArcGisSql, $"name IN ({Values(ValuesBeyondLimit)})" },
        { FilterLanguage.ArcGisSql, $"CONCAT({Values(ValuesBeyondLimit)}) = '1'" }
    };

    [UnitTheory]
    [MemberData(nameof(ValueListsBeyondLimit))]
    public void Parse_ValueListBeyondSizeLimit_ReturnsFailure(FilterLanguage language, string filter)
    {
        var result = _service.Parse(language, filter);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain(
            $"maximum of {FilterParserGuard.MaxInListSize.ToString(CultureInfo.InvariantCulture)} values");
    }

    public static TheoryData<FilterLanguage, string> InputsAtLimits => new()
    {
        { FilterLanguage.Cql2Json, $$"""{"op":"=","args":[{"property":"name"},"{{TextAtLimit}}"]}""" },
        { FilterLanguage.Cql2Json, $$"""{"op":"=","args":[{"property":"{{NameAtLimit}}"},"a"]}""" },
        { FilterLanguage.Cql2Json, $$"""{"op":"in","args":[{"property":"name"},[{{Values(FilterParserGuard.MaxInListSize)}}]]}""" },
        { FilterLanguage.Cql2Json, $$"""{"op":"=","args":[{"op":"f","args":[{{Values(FilterParserGuard.MaxInListSize)}}]},1]}""" },
        { FilterLanguage.Cql2Text, $"A_CONTAINS(tags, ({Values(FilterParserGuard.MaxInListSize)}))" },
        { FilterLanguage.Cql2Text, $"f({Values(FilterParserGuard.MaxInListSize)}) = 1" },
        { FilterLanguage.OData, $"Name eq '{TextAtLimit}'" },
        { FilterLanguage.OData, $"concat({Values(FilterParserGuard.MaxInListSize)}) eq '1'" }
    };

    [UnitTheory]
    [MemberData(nameof(InputsAtLimits))]
    public void Parse_InputAtParserLimits_ReturnsExpression(FilterLanguage language, string filter)
    {
        var result = _service.Parse(language, filter);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Expression.Should().NotBeNull();
    }

    private static string Values(int count) => string.Join(",", Enumerable.Repeat("1", count));
}
