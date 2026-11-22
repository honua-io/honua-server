// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters.Cql2;

internal static class Cql2ScalarLookahead
{
    internal static bool IsParenthesizedScalarAhead(IReadOnlyList<Cql2Token> tokens, int position)
    {
        var closeIndex = FindMatchingRightParen(tokens, position);
        if (closeIndex < 0 || closeIndex + 1 >= tokens.Count)
        {
            return false;
        }

        var next = tokens[closeIndex + 1].Type;
        if (IsArithmeticOperator(next) || Cql2Parser.IsComparisonOperator(next) ||
            next is Cql2TokenType.IsNull or Cql2TokenType.Like or Cql2TokenType.Between or Cql2TokenType.In)
        {
            return true;
        }

        if (next == Cql2TokenType.Not && closeIndex + 2 < tokens.Count)
        {
            return tokens[closeIndex + 2].Type is Cql2TokenType.Like or Cql2TokenType.Between or Cql2TokenType.In;
        }

        return false;
    }

    private static int FindMatchingRightParen(IReadOnlyList<Cql2Token> tokens, int startIndex)
    {
        var depth = 0;
        for (var i = startIndex; i < tokens.Count; i++)
        {
            if (tokens[i].Type == Cql2TokenType.LeftParen)
            {
                depth++;
            }
            else if (tokens[i].Type == Cql2TokenType.RightParen && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsArithmeticOperator(Cql2TokenType type)
    {
        return type is Cql2TokenType.Plus or Cql2TokenType.Minus or
               Cql2TokenType.Star or Cql2TokenType.Slash or
               Cql2TokenType.Percent or Cql2TokenType.Div or
               Cql2TokenType.Caret;
    }
}
