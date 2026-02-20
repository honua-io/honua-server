// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Core.Queries.Filters.OData;

internal sealed class ODataFilterLexer
{
    private readonly string _input;
    private int _position;

    public ODataFilterLexer(string input)
    {
        _input = input ?? string.Empty;
    }

    public IReadOnlyList<ODataFilterToken> Tokenize()
    {
        var tokens = new List<ODataFilterToken>();

        while (!IsAtEnd())
        {
            var c = Current();

            if (char.IsWhiteSpace(c))
            {
                _position++;
                continue;
            }

            if (c == '(')
            {
                tokens.Add(new ODataFilterToken(ODataFilterTokenType.LeftParen, "(", _position++));
                continue;
            }

            if (c == ')')
            {
                tokens.Add(new ODataFilterToken(ODataFilterTokenType.RightParen, ")", _position++));
                continue;
            }

            if (c == ',')
            {
                tokens.Add(new ODataFilterToken(ODataFilterTokenType.Comma, ",", _position++));
                continue;
            }

            if (c == '\'')
            {
                tokens.Add(ReadStringLiteral());
                continue;
            }

            if (char.IsDigit(c))
            {
                if (TryReadIsoDateLiteral(out var dateToken))
                {
                    tokens.Add(dateToken);
                    continue;
                }

                tokens.Add(ReadNumberLiteral());
                continue;
            }

            if (IsNumberStart(c))
            {
                if (c == '-' && IsOperatorContext(tokens))
                {
                    tokens.Add(new ODataFilterToken(ODataFilterTokenType.Minus, "-", _position++));
                    continue;
                }

                tokens.Add(ReadNumberLiteral());
                continue;
            }

            if (c == '-')
            {
                tokens.Add(new ODataFilterToken(ODataFilterTokenType.Minus, "-", _position++));
                continue;
            }

            if (IsIdentifierStart(c))
            {
                tokens.Add(ReadIdentifierOrKeyword());
                continue;
            }

            throw new ODataFilterParseException($"Unexpected character '{c}'", _position);
        }

        tokens.Add(new ODataFilterToken(ODataFilterTokenType.EndOfInput, string.Empty, _position));
        return tokens;
    }

    private ODataFilterToken ReadStringLiteral()
    {
        var start = _position;
        _position++; // skip opening quote
        var builder = new StringBuilder();

        while (!IsAtEnd())
        {
            var c = Current();
            _position++;

            if (c == '\'')
            {
                if (!IsAtEnd() && Current() == '\'')
                {
                    builder.Append('\'');
                    _position++;
                    continue;
                }

                return new ODataFilterToken(ODataFilterTokenType.StringLiteral, builder.ToString(), start);
            }

            builder.Append(c);
        }

        throw new ODataFilterParseException("Unclosed string literal", start);
    }

    private ODataFilterToken ReadNumberLiteral()
    {
        var start = _position;
        var hasDecimal = false;
        var hasExponent = false;

        if (Current() is '+' or '-')
        {
            _position++;
        }

        while (!IsAtEnd())
        {
            var c = Current();

            if (char.IsDigit(c))
            {
                _position++;
                continue;
            }

            if (c == '.' && !hasDecimal && !hasExponent)
            {
                hasDecimal = true;
                _position++;
                continue;
            }

            if ((c == 'e' || c == 'E') && !hasExponent)
            {
                hasExponent = true;
                _position++;
                if (!IsAtEnd() && (Current() == '+' || Current() == '-'))
                {
                    _position++;
                }
                continue;
            }

            break;
        }

        var value = _input[start.._position];
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            throw new ODataFilterParseException($"Invalid numeric literal '{value}'", start);
        }

        return new ODataFilterToken(ODataFilterTokenType.NumberLiteral, value, start);
    }

    private bool TryReadIsoDateLiteral(out ODataFilterToken token)
    {
        token = default;

        if (_position + 10 > _input.Length)
        {
            return false;
        }

        if (!char.IsDigit(_input[_position]) ||
            !char.IsDigit(_input[_position + 1]) ||
            !char.IsDigit(_input[_position + 2]) ||
            !char.IsDigit(_input[_position + 3]) ||
            _input[_position + 4] != '-' ||
            !char.IsDigit(_input[_position + 5]) ||
            !char.IsDigit(_input[_position + 6]) ||
            _input[_position + 7] != '-' ||
            !char.IsDigit(_input[_position + 8]) ||
            !char.IsDigit(_input[_position + 9]))
        {
            return false;
        }

        var start = _position;
        var end = _position + 10;

        while (end < _input.Length)
        {
            var c = _input[end];
            if (char.IsWhiteSpace(c) || c == ',' || c == ')' || c == '(')
            {
                break;
            }

            end++;
        }

        var value = _input[start..end];
        _position = end;

        var type = value.Contains('T') || value.Contains('t')
            ? ODataFilterTokenType.DateTimeLiteral
            : ODataFilterTokenType.DateLiteral;

        token = new ODataFilterToken(type, value, start);
        return true;
    }

    private ODataFilterToken ReadIdentifierOrKeyword()
    {
        var start = _position;
        _position++;

        while (!IsAtEnd())
        {
            var c = Current();
            if (!IsIdentifierPart(c))
            {
                break;
            }

            _position++;
        }

        var value = _input[start.._position];
        var normalized = value.ToLowerInvariant();

        return normalized switch
        {
            "and" => new ODataFilterToken(ODataFilterTokenType.And, value, start),
            "or" => new ODataFilterToken(ODataFilterTokenType.Or, value, start),
            "not" => new ODataFilterToken(ODataFilterTokenType.Not, value, start),
            "eq" => new ODataFilterToken(ODataFilterTokenType.Eq, value, start),
            "ne" => new ODataFilterToken(ODataFilterTokenType.Ne, value, start),
            "gt" => new ODataFilterToken(ODataFilterTokenType.Gt, value, start),
            "ge" => new ODataFilterToken(ODataFilterTokenType.Ge, value, start),
            "lt" => new ODataFilterToken(ODataFilterTokenType.Lt, value, start),
            "le" => new ODataFilterToken(ODataFilterTokenType.Le, value, start),
            "in" => new ODataFilterToken(ODataFilterTokenType.In, value, start),
            "add" => new ODataFilterToken(ODataFilterTokenType.Add, value, start),
            "sub" => new ODataFilterToken(ODataFilterTokenType.Sub, value, start),
            "mul" => new ODataFilterToken(ODataFilterTokenType.Mul, value, start),
            "div" => new ODataFilterToken(ODataFilterTokenType.Div, value, start),
            "mod" => new ODataFilterToken(ODataFilterTokenType.Mod, value, start),
            "true" or "false" => new ODataFilterToken(ODataFilterTokenType.BooleanLiteral, normalized, start),
            "null" => new ODataFilterToken(ODataFilterTokenType.NullLiteral, normalized, start),
            _ => new ODataFilterToken(ODataFilterTokenType.Identifier, value, start)
        };
    }

    private bool IsAtEnd() => _position >= _input.Length;

    private char Current() => _input[_position];

    private bool IsNumberStart(char c)
    {
        if (char.IsDigit(c))
        {
            return true;
        }

        if ((c == '+' || c == '-') && _position + 1 < _input.Length && char.IsDigit(_input[_position + 1]))
        {
            return true;
        }

        return false;
    }

    private static bool IsOperatorContext(List<ODataFilterToken> tokens)
    {
        if (tokens.Count == 0)
        {
            return false;
        }

        var lastType = tokens[^1].Type;
        return lastType is ODataFilterTokenType.Identifier
            or ODataFilterTokenType.NumberLiteral
            or ODataFilterTokenType.StringLiteral
            or ODataFilterTokenType.BooleanLiteral
            or ODataFilterTokenType.RightParen;
    }

    private static bool IsIdentifierStart(char c)
        => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == '.';
}
