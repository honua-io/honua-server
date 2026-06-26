// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing.Execution.Expressions;

/// <summary>
/// Recursive-descent / precedence-climbing parser that turns the lexer's token
/// stream into the immutable <see cref="ExpressionNode"/> tree. Only the fixed
/// grammar documented on <see cref="ExpressionEngine"/> is accepted; function names
/// are validated against the whitelist at parse time so an unknown function is
/// rejected before any evaluation. Depth is bounded by
/// <see cref="ExpressionEngine.MaxDepth"/> to prevent stack exhaustion.
/// </summary>
internal sealed class ExpressionParser(IReadOnlyList<ExpressionToken> tokens)
{
    private int _position;
    private int _depth;

    /// <summary>Parses a full expression starting at the current position.</summary>
    public ExpressionNode ParseExpression() => ParseTernary();

    /// <summary>Asserts the token stream is fully consumed (only <see cref="ExpressionTokenKind.End"/> remains).</summary>
    public void ExpectEnd()
    {
        if (Current.Kind != ExpressionTokenKind.End)
        {
            throw new ExpressionParseException(
                $"unexpected trailing token '{Current.Text}' after a complete expression");
        }
    }

    private ExpressionToken Current => tokens[_position];

    private ExpressionToken Advance() => tokens[_position++];

    private bool Match(ExpressionTokenKind kind)
    {
        if (Current.Kind == kind)
        {
            _position++;
            return true;
        }

        return false;
    }

    private void Expect(ExpressionTokenKind kind, string what)
    {
        if (!Match(kind))
        {
            throw new ExpressionParseException($"expected {what} but found '{Current.Text}'");
        }
    }

    private void EnterDepth()
    {
        if (++_depth > ExpressionEngine.MaxDepth)
        {
            throw new ExpressionParseException(
                $"expression nesting exceeds the maximum depth of {ExpressionEngine.MaxDepth}");
        }
    }

    private void ExitDepth() => _depth--;

    private ExpressionNode ParseTernary()
    {
        var condition = ParseLogicalOr();
        if (Match(ExpressionTokenKind.Question))
        {
            EnterDepth();
            var whenTrue = ParseExpression();
            Expect(ExpressionTokenKind.Colon, "':' in a ternary expression");
            var whenFalse = ParseExpression();
            ExitDepth();
            return new ConditionalNode(condition, whenTrue, whenFalse);
        }

        return condition;
    }

    private ExpressionNode ParseLogicalOr()
    {
        var node = ParseLogicalAnd();
        while (Current.Kind == ExpressionTokenKind.OrOr)
        {
            Advance();
            node = new BinaryNode(ExpressionTokenKind.OrOr, node, ParseLogicalAnd());
        }

        return node;
    }

    private ExpressionNode ParseLogicalAnd()
    {
        var node = ParseEquality();
        while (Current.Kind == ExpressionTokenKind.AndAnd)
        {
            Advance();
            node = new BinaryNode(ExpressionTokenKind.AndAnd, node, ParseEquality());
        }

        return node;
    }

    private ExpressionNode ParseEquality()
    {
        var node = ParseComparison();
        while (Current.Kind is ExpressionTokenKind.EqualEqual or ExpressionTokenKind.NotEqual)
        {
            var op = Advance().Kind;
            node = new BinaryNode(op, node, ParseComparison());
        }

        return node;
    }

    private ExpressionNode ParseComparison()
    {
        var node = ParseAdditive();
        while (Current.Kind is ExpressionTokenKind.LessThan or ExpressionTokenKind.LessEqual
               or ExpressionTokenKind.GreaterThan or ExpressionTokenKind.GreaterEqual)
        {
            var op = Advance().Kind;
            node = new BinaryNode(op, node, ParseAdditive());
        }

        return node;
    }

    private ExpressionNode ParseAdditive()
    {
        var node = ParseMultiplicative();
        while (Current.Kind is ExpressionTokenKind.Plus or ExpressionTokenKind.Minus)
        {
            var op = Advance().Kind;
            node = new BinaryNode(op, node, ParseMultiplicative());
        }

        return node;
    }

    private ExpressionNode ParseMultiplicative()
    {
        var node = ParseUnary();
        while (Current.Kind is ExpressionTokenKind.Star or ExpressionTokenKind.Slash or ExpressionTokenKind.Percent)
        {
            var op = Advance().Kind;
            node = new BinaryNode(op, node, ParseUnary());
        }

        return node;
    }

    private ExpressionNode ParseUnary()
    {
        if (Current.Kind is ExpressionTokenKind.Minus or ExpressionTokenKind.Not)
        {
            var op = Advance().Kind;
            EnterDepth();
            var operand = ParseUnary();
            ExitDepth();
            return new UnaryNode(op, operand);
        }

        return ParsePrimary();
    }

    private ExpressionNode ParsePrimary()
    {
        var token = Current;
        switch (token.Kind)
        {
            case ExpressionTokenKind.Number:
                Advance();
                return new LiteralNode(token.Number);
            case ExpressionTokenKind.String:
                Advance();
                return new LiteralNode(token.Text);
            case ExpressionTokenKind.True:
                Advance();
                return new LiteralNode(true);
            case ExpressionTokenKind.False:
                Advance();
                return new LiteralNode(false);
            case ExpressionTokenKind.Null:
                Advance();
                return new LiteralNode(null);
            case ExpressionTokenKind.LeftParen:
                Advance();
                EnterDepth();
                var inner = ParseExpression();
                ExitDepth();
                Expect(ExpressionTokenKind.RightParen, "')'");
                return inner;
            case ExpressionTokenKind.Identifier:
                return ParseIdentifierOrCall();
            default:
                throw new ExpressionParseException(
                    $"unexpected token '{token.Text}' where a value was expected");
        }
    }

    private ExpressionNode ParseIdentifierOrCall()
    {
        var name = Advance().Text;
        if (!Match(ExpressionTokenKind.LeftParen))
        {
            // Bare identifier -> field reference.
            return new FieldNode(name);
        }

        var arguments = new List<ExpressionNode>();
        if (Current.Kind != ExpressionTokenKind.RightParen)
        {
            EnterDepth();
            arguments.Add(ParseExpression());
            while (Match(ExpressionTokenKind.Comma))
            {
                arguments.Add(ParseExpression());
            }

            ExitDepth();
        }

        Expect(ExpressionTokenKind.RightParen, "')' to close the function call");

        if (!ExpressionFunctions.IsKnown(name, out var lowered))
        {
            throw new ExpressionParseException(
                $"unknown function '{name}'. Allowed functions: {ExpressionFunctions.AllowedFunctionList}");
        }

        ExpressionFunctions.ValidateArity(lowered, arguments.Count);
        return new FunctionNode(lowered, arguments);
    }
}
