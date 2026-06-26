// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Geoprocessing.Execution.Expressions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Unit coverage for the safe, AOT-friendly computed-field expression engine. The
/// engine parses a whitelisted grammar into an immutable AST and evaluates it
/// against a field-reference context — no reflection, no runtime code generation.
/// These tests exercise arithmetic, string, conditional, comparison/logical, math,
/// date, and field-reference evaluation, and assert that unsafe / malformed input
/// is rejected at parse time.
/// </summary>
public sealed class ExpressionEngineTests
{
    [InlineData("1 + 2 * 3", 7d)]
    [InlineData("(1 + 2) * 3", 9d)]
    [InlineData("10 / 4", 2.5d)]
    [InlineData("10 % 3", 1d)]
    [InlineData("-5 + 2", -3d)]
    [InlineData("2 + 3 == 5", true)]
    [InlineData("4 < 5 && 5 <= 5", true)]
    [InlineData("4 > 5 || 1 == 1", true)]
    [InlineData("!(1 == 2)", true)]
    [Theory]
    public void Evaluate_Arithmetic_LogicalAndComparison(string expression, object expected)
    {
        var result = ExpressionEngine.Evaluate(expression, new MapContext());
        result.Should().Be(expected);
    }

    [UnitTest]
    public void Evaluate_StringFunctions_ComposeAsExpected()
    {
        var context = new MapContext { ["name"] = "  Acme  ", ["year"] = 2026d };

        // The headline example from the ticket.
        ExpressionEngine.Evaluate("upper(trim(name)) + \"-\" + cast(year, string)", context)
            .Should().Be("ACME-2026");

        ExpressionEngine.Evaluate("lower(\"HELLO\")", context).Should().Be("hello");
        ExpressionEngine.Evaluate("substr(\"abcdef\", 1, 3)", context).Should().Be("bcd");
        ExpressionEngine.Evaluate("replace(\"a-b-c\", \"-\", \"_\")", context).Should().Be("a_b_c");
        ExpressionEngine.Evaluate("concat(\"a\", \"b\", \"c\")", context).Should().Be("abc");
        ExpressionEngine.Evaluate("length(\"abcd\")", context).Should().Be(4d);
    }

    [UnitTest]
    public void Evaluate_Conditionals_IfCoalesceAndTernary()
    {
        var context = new MapContext { ["score"] = 80d, ["blank"] = string.Empty, ["filled"] = "ok" };

        ExpressionEngine.Evaluate("if(score >= 60, \"pass\", \"fail\")", context).Should().Be("pass");
        ExpressionEngine.Evaluate("score >= 90 ? \"A\" : \"B\"", context).Should().Be("B");
        ExpressionEngine.Evaluate("coalesce(blank, filled)", context).Should().Be("ok");
        ExpressionEngine.Evaluate("coalesce(missingField, \"fallback\")", context).Should().Be("fallback");
    }

    [UnitTest]
    public void Evaluate_MathFunctions()
    {
        var context = new MapContext();
        ExpressionEngine.Evaluate("abs(-7)", context).Should().Be(7d);
        ExpressionEngine.Evaluate("round(3.14159, 2)", context).Should().Be(3.14d);
        ExpressionEngine.Evaluate("floor(3.9)", context).Should().Be(3d);
        ExpressionEngine.Evaluate("ceil(3.1)", context).Should().Be(4d);
        ExpressionEngine.Evaluate("sqrt(9)", context).Should().Be(3d);
        ExpressionEngine.Evaluate("pow(2, 10)", context).Should().Be(1024d);
        ExpressionEngine.Evaluate("min(3, 1, 2)", context).Should().Be(1d);
        ExpressionEngine.Evaluate("max(3, 1, 2)", context).Should().Be(3d);
    }

    [UnitTest]
    public void Evaluate_DateFunctions()
    {
        var context = new MapContext { ["d"] = "2024-03-15T00:00:00Z" };
        ExpressionEngine.Evaluate("year(parsedate(d))", context).Should().Be(2024d);
        ExpressionEngine.Evaluate("month(parsedate(d))", context).Should().Be(3d);
        ExpressionEngine.Evaluate("day(parsedate(d))", context).Should().Be(15d);

        var now = ExpressionEngine.Evaluate("now()", context);
        now.Should().BeOfType<DateTimeOffset>();
    }

    [UnitTest]
    public void Evaluate_FieldReferences_ResolveThroughContext()
    {
        var context = new MapContext { ["a"] = 10d, ["b"] = 5d };
        ExpressionEngine.Evaluate("a + b", context).Should().Be(15d);
        // A missing field resolves to null; used in a string concat it is empty.
        ExpressionEngine.Evaluate("\"x=\" + missing", context).Should().Be("x=");
    }

    [UnitTest]
    public void Evaluate_CastFunction_CoercesTypes()
    {
        var context = new MapContext { ["n"] = "42" };
        ExpressionEngine.Evaluate("cast(n, int)", context).Should().Be(42d);
        ExpressionEngine.Evaluate("cast(3.9, int)", context).Should().Be(3d);
        ExpressionEngine.Evaluate("cast(1, bool)", context).Should().Be(true);
        ExpressionEngine.Evaluate("cast(123, string)", context).Should().Be("123");
    }

    [InlineData("1 +")]                       // dangling operator
    [InlineData("(1 + 2")]                     // unbalanced paren
    [InlineData("1 + + 2")]                    // double operator
    [InlineData("\"unterminated")]            // unterminated string
    [InlineData("1 = 2")]                      // single '=' is not allowed
    [InlineData("name @ value")]              // disallowed character
    [Theory]
    public void Parse_RejectsMalformedInput(string expression)
    {
        var act = () => ExpressionEngine.Parse(expression);
        act.Should().Throw<ExpressionParseException>();
    }

    [InlineData("system(\"rm -rf /\")")]       // arbitrary function name
    [InlineData("GetType()")]                  // reflection-flavored identifier as call
    [InlineData("eval(\"1\")")]               // not whitelisted
    [InlineData("File.ReadAllText(\"x\")")]   // dotted member access is not in the grammar
    [Theory]
    public void Parse_RejectsUnsafeOrUnknownFunctions(string expression)
    {
        var act = () => ExpressionEngine.Parse(expression);
        act.Should().Throw<ExpressionParseException>();
    }

    [UnitTest]
    public void Parse_RejectsWrongArity()
    {
        var act = () => ExpressionEngine.Parse("upper(\"a\", \"b\")");
        act.Should().Throw<ExpressionParseException>().WithMessage("*argument*");
    }

    [UnitTest]
    public void Parse_RejectsOverlyDeepNesting()
    {
        var deep = string.Concat(Enumerable.Repeat("(", ExpressionEngine.MaxDepth + 2))
            + "1"
            + string.Concat(Enumerable.Repeat(")", ExpressionEngine.MaxDepth + 2));
        var act = () => ExpressionEngine.Parse(deep);
        act.Should().Throw<ExpressionParseException>();
    }

    [UnitTest]
    public void Evaluate_DivisionByZero_ThrowsEvaluationError()
    {
        var tree = ExpressionEngine.Parse("1 / 0");
        var act = () => tree.Evaluate(new MapContext());
        act.Should().Throw<ExpressionEvaluationException>();
    }

    [UnitTest]
    public void ParsedTree_IsReusable_AcrossManyContexts()
    {
        var tree = ExpressionEngine.Parse("a * 2");
        tree.Evaluate(new MapContext { ["a"] = 3d }).Should().Be(6d);
        tree.Evaluate(new MapContext { ["a"] = 10d }).Should().Be(20d);
    }

    private sealed class MapContext : IExpressionContext
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public object? this[string key]
        {
            set => _values[key] = value;
        }

        public bool TryGetField(string name, out object? value) => _values.TryGetValue(name, out value);
    }
}
