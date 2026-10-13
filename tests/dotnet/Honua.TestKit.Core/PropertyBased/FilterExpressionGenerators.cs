// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FsCheck;
using FsCheck.Fluent;
using Honua.Core.Queries.Filters;

using Arb = FsCheck.Fluent.Arb;
using Gen = FsCheck.Fluent.Gen;

namespace Honua.TestKit.PropertyBased;

/// <summary>
/// Property-based generators for filter expressions to test CQL2 parsing and evaluation.
/// </summary>
public static class FilterExpressionGenerators
{
    /// <summary>
    /// Generates valid property names for filter expressions.
    /// </summary>
    public static Arbitrary<string> PropertyName() =>
        Arb.From(
            Gen.OneOf(
                Gen.Constant("name"),
                Gen.Constant("status"),
                Gen.Constant("created_at"),
                Gen.Constant("geometry"),
                Gen.Constant("id"),
                Gen.Constant("category"),
                from length in Gen.Choose(3, 20)
                from name in Gen.ArrayOf(Gen.Elements("abcdefghijklmnopqrstuvwxyz_".ToCharArray()), length)
                select new string(name)));

    /// <summary>
    /// Generates valid comparison operators.
    /// </summary>
    public static Arbitrary<BinaryOperator> ComparisonOperator() =>
        Arb.From(Gen.Elements(
            BinaryOperator.Equal,
            BinaryOperator.NotEqual,
            BinaryOperator.LessThan,
            BinaryOperator.LessThanOrEqual,
            BinaryOperator.GreaterThan,
            BinaryOperator.GreaterThanOrEqual));

    /// <summary>
    /// Generates valid string literals for filters.
    /// </summary>
    public static Arbitrary<string> StringLiteral() =>
        Arb.From(
            Gen.OneOf(
                // Simple strings
                Gen.Elements("active", "inactive", "pending", "complete"),
                // Strings with special characters
                Gen.Elements("O'Connor", "José María", "test@example.com"),
                // Empty and whitespace
                Gen.Constant(""),
                Gen.Constant("   "),
                // SQL injection attempts (should be safely handled)
                Gen.Constant("'; DROP TABLE users; --"),
                Gen.Constant("' OR 1=1 --"),
                // Unicode characters
                Gen.Constant("文档测试"),
                Gen.Constant("🚀✨")));

    /// <summary>
    /// Generates valid numeric literals.
    /// </summary>
    public static Arbitrary<decimal> NumericLiteral() =>
        Arb.From(
            Gen.OneOf(
                Gen.Choose(1, 100).Select(x => (decimal)x),
                Gen.Choose(-100, -1).Select(x => (decimal)x),
                Gen.Constant(0m),
                Gen.Elements(1.5m, -1.5m, 0.001m, 999999.99m),
                Gen.Constant(decimal.MaxValue),
                Gen.Constant(decimal.MinValue)));

    /// <summary>
    /// Generates complex nested filter expressions.
    /// </summary>
    public static Arbitrary<BinaryExpression> NestedExpression() =>
        Arb.From(GenerateNestedExpression(depth: 0, maxDepth: 3));

    private static Gen<BinaryExpression> GenerateNestedExpression(int depth, int maxDepth)
    {
        var leafExpression =
            from prop in PropertyName().Generator
            from op in ComparisonOperator().Generator
            from value in Gen.OneOf(
                StringLiteral().Generator.Select(s => (object)s),
                NumericLiteral().Generator.Select(n => (object)n))
            select new BinaryExpression(
                new PropertyReference(prop),
                op,
                CreateLiteral(value));

        if (depth >= maxDepth)
            return leafExpression;

        var nestedExpression =
            from left in GenerateNestedExpression(depth + 1, maxDepth)
            from op in Gen.Elements(BinaryOperator.And, BinaryOperator.Or)
            from right in GenerateNestedExpression(depth + 1, maxDepth)
            select new BinaryExpression(left, op, right);

        return Gen.OneOf(leafExpression, nestedExpression);
    }

    /// <summary>
    /// Generates malformed CQL2 expressions for error handling tests.
    /// </summary>
    public static Arbitrary<string> MalformedCql2() =>
        Arb.From(Gen.OneOf(
            // Unmatched parentheses
            Gen.Constant("name = 'test'("),
            Gen.Constant(")name = 'test'"),
            Gen.Constant("((name = 'test')"),
            // Invalid operators
            Gen.Constant("name === 'test'"),
            Gen.Constant("name <> 'test'"),
            // Missing operands
            Gen.Constant("name ="),
            Gen.Constant("= 'test'"),
            Gen.Constant("AND name = 'test'"),
            // Invalid property names
            Gen.Constant("123abc = 'test'"),
            Gen.Constant("name-with-dashes = 'test'"),
            // Unmatched quotes
            Gen.Constant("name = 'test"),
            Gen.Constant("name = test'"),
            // Invalid spatial functions
            Gen.Constant("ST_INVALID(geometry, 'POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))')"),
            // Empty expressions
            Gen.Constant(""),
            Gen.Constant("   ")));

    /// <summary>
    /// Generates valid CQL2 expressions for parsing tests.
    /// </summary>
    public static Arbitrary<string> ValidCql2() =>
        Arb.From(Gen.OneOf(
            // Simple comparisons
            Gen.Constant("name = 'test'"),
            Gen.Constant("age > 18"),
            Gen.Constant("status IN ('active', 'pending')"),
            // Spatial operations
            Gen.Constant("ST_INTERSECTS(geometry, POLYGON((0 0, 1 0, 1 1, 0 1, 0 0)))"),
            Gen.Constant("ST_WITHIN(geometry, POLYGON((0 0, 10 0, 10 10, 0 10, 0 0)))"),
            // Complex expressions
            Gen.Constant("name = 'test' AND age > 18"),
            Gen.Constant("(status = 'active' OR status = 'pending') AND created_at > '2023-01-01'"),
            // Date/time operations
            Gen.Constant("created_at BETWEEN '2023-01-01' AND '2023-12-31'"),
            Gen.Constant("updated_at IS NOT NULL")));

    private static Literal CreateLiteral(object? value)
    {
        return value switch
        {
            null => new Literal(null, LiteralType.Null),
            string text => new Literal(text, LiteralType.Text),
            bool boolean => new Literal(boolean, LiteralType.Boolean),
            DateOnly date => new Literal(date, LiteralType.Date),
            DateTime dateTime => new Literal(dateTime, LiteralType.DateTime),
            sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => new Literal(value, LiteralType.Number),
            _ => new Literal(value, LiteralType.Text)
        };
    }
}
